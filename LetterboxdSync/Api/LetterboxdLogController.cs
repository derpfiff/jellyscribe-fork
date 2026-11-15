using System;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using LetterboxdSync.LetterboxdLog;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Api;

/// <summary>Body of <c>POST /LetterboxdLog/Account</c>.</summary>
public sealed class LogAccountRequest
{
    /// <summary>Letterboxd username (not the email). Optional when only <see cref="Enabled"/> changes.</summary>
    public string? Username { get; set; }

    /// <summary>Password. Required when linking or changing the username; empty keeps the stored one.</summary>
    public string? Password { get; set; }

    /// <summary>Sending on/off. Null keeps the current value (a new link starts on).</summary>
    public bool? Enabled { get; set; }
}

/// <summary>
/// Fork: the client-facing log API. Every call acts for the user of the calling Jellyfin token
/// (any signed-in user, no admin rights needed), except <c>Overview</c>, which is admin-only.
/// </summary>
[ApiController]
[Authorize]
[Route("LetterboxdLog")]
[Produces(MediaTypeNames.Application.Json)]
public class LetterboxdLogController : JellyfinUserApiController
{
    private readonly LetterboxdLogService _log;
    private readonly LetterboxdRatingsService _ratings;
    private readonly ILogger<LetterboxdLogController> _logger;

    public LetterboxdLogController(LetterboxdLogService log, LetterboxdRatingsService ratings, IUserManager userManager, ILogger<LetterboxdLogController> logger)
        : base(userManager)
    {
        _log = log;
        _ratings = ratings;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static readonly System.Text.Json.JsonSerializerOptions RatingsJson = new();

    /// <summary>Test-only replacement for the official-API sign-in in <see cref="SaveAccount"/>.</summary>
    internal static Func<string, string, Task>? AccountLoginForTesting;

    /// <summary>{ enabled, configured, queued }: enabled = the user switched Letterboxd logging on;
    /// configured = enabled and credentials are stored.</summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Status()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        var account = LetterboxdLogService.AccountFor(userId);
        var enabled = account != null;
        var configured = enabled && LetterboxdLogService.HasCredentials(account!);
        if (configured)
            _ = Task.Run(() => _log.RetryUserAsync(userId, dueOnly: false));
        return Ok(new { enabled, configured, queued = _log.QueuedCount(userId) });
    }

    /// <summary>The caller's linked Letterboxd account (the primary one; the password never leaves the server).</summary>
    [HttpGet("Account")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetAccount()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        return Ok(AccountState(userId));
    }

    /// <summary>
    /// Links or updates the caller's Letterboxd account. A new password (or a new username) is
    /// first checked by signing in through Letterboxd's official API; only then is it stored
    /// (encrypted, like every account password). <c>{ "enabled": false }</c> alone switches sending
    /// off without touching the login.
    /// </summary>
    [HttpPost("Account")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> SaveAccount([FromBody] LogAccountRequest? request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        if (request == null)
            return BadRequest(new { error = "missing body" });

        var mine = Config.Accounts.Where(a => a.UserJellyfinId == userId).ToList();
        var current = mine.FirstOrDefault(a => a.Enabled && a.IsPrimary) ?? mine.FirstOrDefault(a => a.Enabled) ?? mine.FirstOrDefault();
        var username = request.Username?.Trim().TrimStart('@');
        if (string.IsNullOrEmpty(username))
            username = current?.LetterboxdUsername;
        if (string.IsNullOrEmpty(username))
            return BadRequest(new { error = "Username and password are required." });
        if (LetterboxdController.EmailAsUsernameError(username) is { } emailError)
            return BadRequest(new { error = emailError });

        var target = mine.FirstOrDefault(a => string.Equals(a.LetterboxdUsername, username, StringComparison.OrdinalIgnoreCase));
        var password = request.Password ?? string.Empty;
        if (password.Length == 0 && (target == null || !target.HasPassword))
            return BadRequest(new { error = "Username and password are required." });

        if (password.Length > 0)
        {
            if (!LoginCheckLimiter.Letterboxd.TryAcquire(userId, out var stamp, out var retryAfter))
                return TooManyLoginChecks(retryAfter);
            try
            {
                if (AccountLoginForTesting != null)
                    await AccountLoginForTesting(username, password).ConfigureAwait(false);
                else
                {
                    using var api = new LetterboxdApiClient(_logger);
                    await api.AuthenticateAsync(username, password).ConfigureAwait(false);
                }
                LoginCheckLimiter.Letterboxd.Refund(userId, stamp);
            }
            catch (Exception ex) when ((ex is HttpRequestException h && (h.StatusCode == null || (int)h.StatusCode >= 500))
                || (ex is LetterboxdApiAuthException a && (int)a.StatusCode >= 500))
            {
                LoginCheckLimiter.Letterboxd.Refund(userId, stamp);
                _logger.LogWarning("LetterboxdLog: Letterboxd unreachable while linking {LbUser}: {Message}", username, ex.Message);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Letterboxd is not reachable right now. Try again later." });
            }
            catch (TaskCanceledException)
            {
                LoginCheckLimiter.Letterboxd.Refund(userId, stamp);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Letterboxd is not reachable right now. Try again later." });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("LetterboxdLog: linking {LbUser} failed: {Reason}", username, LetterboxdController.DescribeLoginError(ex));
                return BadRequest(new { error = "Login failed: " + LetterboxdController.DescribeLoginError(ex) });
            }
        }

        if (target == null)
        {
            // New link: only what this fork uses. No sync, watchlist, favourites or import.
            target = new Account
            {
                UserJellyfinId = userId,
                LetterboxdUsername = username,
                Enabled = true,
                SyncRatings = false,
                SyncFavorites = false,
                EnableDiaryImport = false,
                EnableWatchlistSync = false,
            };
            Config.Accounts.Add(target);
        }

        target.LetterboxdUsername = username;
        if (password.Length > 0)
            target.LetterboxdPassword = password;
        if (request.Enabled.HasValue)
            target.Enabled = request.Enabled.Value;
        else if (password.Length > 0)
            target.Enabled = true;
        foreach (var other in mine.Where(a => !ReferenceEquals(a, target)))
            other.IsPrimary = false;
        target.IsPrimary = true;

        Config.NormalisePrimaryFlags();
        Plugin.Instance!.SaveConfiguration();
        if (password.Length > 0)
            AuthBreaker.Reset(userId, username);
        _logger.LogInformation("LetterboxdLog: user {UserId} saved Letterboxd account {LbUser} (sending {State})", userId, username, target.Enabled ? "on" : "off");
        return Ok(AccountState(userId));
    }

    /// <summary>Unlinks: removes every Letterboxd account (and stored password) of the caller.</summary>
    [HttpDelete("Account")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteAccount()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        var mine = Config.Accounts.Where(a => a.UserJellyfinId == userId).ToList();
        if (mine.Count > 0)
        {
            Config.Accounts.RemoveAll(a => a.UserJellyfinId == userId);
            Plugin.Instance!.SaveConfiguration();
            _ratings.Forget(userId);
            foreach (var a in mine)
                AuthBreaker.Reset(userId, a.LetterboxdUsername);
            _logger.LogInformation("LetterboxdLog: user {UserId} unlinked Letterboxd", userId);
        }
        return Ok(AccountState(userId));
    }

    /// <summary>
    /// Every film the caller's linked Letterboxd member has rated, liked or marked watched (also the
    /// ones without a diary entry), from the official API, cached 6 h per user. <c>?refresh=1</c>
    /// reads again, at most once per 10 minutes. Not linked: 200 with linked=false and no items.
    /// Letterboxd unreachable: the cached copy with stale=true, or 502 { error } without one.
    /// </summary>
    [HttpGet("Ratings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> Ratings([FromQuery] string? refresh = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        var force = refresh is "1" or "true" or "True";
        var res = await _ratings.GetAsync(userId, force, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!res.Linked)
            return Ok(new { linked = false, items = Array.Empty<object>() });
        if (res.Data == null)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = res.Error ?? "Letterboxd is not reachable right now. Try again later." });
        // Own serializer options: Jellyfin's drop null values, but rating: null is part of the contract.
        return new JsonResult(new
        {
            linked = true,
            username = res.Data.Username,
            fetchedAt = res.Data.FetchedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            stale = res.Stale,
            error = res.Stale ? res.Error : null,
            truncated = res.Data.Truncated,
            count = res.Data.Items.Count,
            items = res.Data.Items.Select(i => new { tmdbId = i.TmdbId, title = i.Title, year = i.Year, rating = i.Rating, liked = i.Liked, watched = i.Watched }),
        }, RatingsJson);
    }

    /// <summary>Admin: per Jellyfin user with a linked account, the account and its entry counts.</summary>
    [HttpGet("Overview")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Overview()
    {
        var rows = Config.Accounts
            .GroupBy(a => a.UserJellyfinId)
            .Select(g =>
            {
                var a = g.FirstOrDefault(x => x.Enabled && x.IsPrimary) ?? g.FirstOrDefault(x => x.Enabled) ?? g.First();
                var c = _log.Counts(g.Key);
                return new
                {
                    userId = g.Key,
                    username = a.LetterboxdUsername,
                    enabled = a.Enabled,
                    hasLogin = LetterboxdLogService.HasCredentials(a),
                    loginFailing = AuthBreaker.IsOpen(g.Key, a.LetterboxdUsername),
                    queued = c.Queued,
                    posted = c.Posted,
                    errors = c.Errors,
                };
            })
            .ToList();
        return Ok(rows);
    }

    private object AccountState(string userId)
    {
        var mine = Config.Accounts.Where(a => a.UserJellyfinId == userId).ToList();
        var a = mine.FirstOrDefault(x => x.Enabled && x.IsPrimary) ?? mine.FirstOrDefault(x => x.Enabled) ?? mine.FirstOrDefault();
        return new
        {
            linked = a != null && LetterboxdLogService.HasCredentials(a),
            username = a?.LetterboxdUsername,
            enabled = a != null && a.Enabled,
            loginFailing = a != null && AuthBreaker.IsOpen(userId, a.LetterboxdUsername),
            queued = _log.QueuedCount(userId),
        };
    }

    /// <summary>Logs (or updates) one diary entry. Returns { status: posted|queued|error, message }.</summary>
    [HttpPost("Entry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Entry([FromBody] LogEntryRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        if (request == null)
            return BadRequest(new { status = "error", message = "missing body" });
        var res = await _log.SubmitAsync(userId, request).ConfigureAwait(false);
        return Ok(new { status = res.Status, message = res.Message });
    }

    /// <summary>The caller's stored entries (newest first), for a "pending" indicator.</summary>
    [HttpGet("Entries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Entries()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        return Ok(_log.List(userId).OrderByDescending(r => r.UpdatedUtc).Select(r => new
        {
            r.ItemId, r.TmdbId, r.Title, r.WatchedDate, r.Rating, r.Liked, r.Rewatch, r.ContainsSpoilers,
            hasReview = r.Review != null, r.State, r.LastError, r.UpdatedUtc,
        }));
    }
}
