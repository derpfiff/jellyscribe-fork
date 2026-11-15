using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.LetterboxdLog;

/// <summary>Fork: one cached copy of a member's rated / liked / watched films.</summary>
public sealed class RatingsCache
{
    public string Username { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; }
    public bool Truncated { get; set; }
    public List<RatingsItem> Items { get; set; } = new();
}

public sealed class RatingsItem
{
    public int TmdbId { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public double? Rating { get; set; }
    public bool Liked { get; set; }
    public bool Watched { get; set; }
}

/// <summary>What <see cref="LetterboxdRatingsService.GetAsync"/> hands the controller.</summary>
public sealed record RatingsResult(bool Linked, RatingsCache? Data, bool Stale, string? Error);

/// <summary>
/// Fork: reads every film the linked member rated, liked or watched through the official API
/// and keeps it per Jellyfin user on disk (plugins/configurations/LetterboxdLog/ratings/&lt;user&gt;.json)
/// for <see cref="MaxAge"/>. A forced refresh is honoured at most once per <see cref="MinRefresh"/>.
/// One read per user at a time; pages are read one after another with a short pause.
/// </summary>
public sealed class LetterboxdRatingsService
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    internal static readonly TimeSpan MinRefresh = TimeSpan.FromMinutes(10);
    /// <summary>After a failed read, wait this long before contacting Letterboxd again for that user.</summary>
    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(2);
    internal const int MaxFilms = 5000;
    internal static TimeSpan PageDelay { get; } = TimeSpan.FromMilliseconds(
        double.TryParse(Environment.GetEnvironmentVariable("JELLYSCRIBE_RATINGS_PAGE_DELAY_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0 ? ms : 400);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly ILogger<LetterboxdRatingsService> _logger;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, (DateTime At, string Error)> _lastFailure = new();

    public LetterboxdRatingsService(IApplicationPaths paths, ILogger<LetterboxdRatingsService> logger)
    {
        _logger = logger;
        _dir = Path.Combine(paths.PluginConfigurationsPath, "LetterboxdLog", "ratings");
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>The account ratings are read from: the primary enabled one, else any with a stored login.</summary>
    private static Account? AccountFor(string userIdN)
    {
        var mine = Config.Accounts.Where(a => a.UserJellyfinId == userIdN && LetterboxdLogService.HasCredentials(a)).ToList();
        return mine.FirstOrDefault(a => a.Enabled && a.IsPrimary) ?? mine.FirstOrDefault(a => a.Enabled) ?? mine.FirstOrDefault();
    }

    public async Task<RatingsResult> GetAsync(string userIdN, bool refresh, CancellationToken cancellationToken)
    {
        var account = AccountFor(userIdN);
        if (account == null || !account.HasPassword)
            return new RatingsResult(false, null, false, null);

        var gate = _locks.GetOrAdd(userIdN, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var cache = Load(userIdN);
            if (cache != null && !string.Equals(cache.Username, account.LetterboxdUsername, StringComparison.OrdinalIgnoreCase))
                cache = null; // another Letterboxd account was linked since
            var age = cache != null ? now - cache.FetchedAt : TimeSpan.MaxValue;

            if (cache != null && age < MaxAge && (!refresh || age < MinRefresh))
                return new RatingsResult(true, cache, false, null);

            if (_lastFailure.TryGetValue(userIdN, out var fail) && now - fail.At < FailureBackoff)
                return cache != null ? new RatingsResult(true, cache, true, fail.Error) : new RatingsResult(true, null, true, fail.Error);

            try
            {
                using var api = new LetterboxdApiClient(_logger);
                await api.AuthenticateAsync(account.LetterboxdUsername, account.LetterboxdPassword).ConfigureAwait(false);
                var (films, truncated) = await api.GetMemberFilmsAsync(MaxFilms, PageDelay, cancellationToken).ConfigureAwait(false);
                var fresh = new RatingsCache
                {
                    Username = account.LetterboxdUsername,
                    FetchedAt = DateTime.UtcNow,
                    Truncated = truncated,
                    Items = films.Select(f => new RatingsItem { TmdbId = f.TmdbId, Title = f.Title, Year = f.Year, Rating = f.Rating, Liked = f.Liked, Watched = f.Watched }).ToList(),
                };
                Save(userIdN, fresh);
                _lastFailure.TryRemove(userIdN, out _);
                _logger.LogInformation("LetterboxdLog: read {Count} rated/watched films of {LbUser}{Cut}", fresh.Items.Count, account.LetterboxdUsername, truncated ? " (cut at the limit)" : string.Empty);
                return new RatingsResult(true, fresh, false, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = Describe(ex);
                _lastFailure[userIdN] = (DateTime.UtcNow, error);
                _logger.LogWarning("LetterboxdLog: could not read the films of {LbUser}: {Error}", account.LetterboxdUsername, AuthBreaker.Sanitize(ex.Message));
                return new RatingsResult(true, cache, true, error);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        LetterboxdApiAuthException a when (int)a.StatusCode < 500 => "Letterboxd rejected the stored login. Link the account again.",
        HttpRequestException or TaskCanceledException or LetterboxdApiAuthException => "Letterboxd is not reachable right now. Try again later.",
        _ => "Letterboxd answered unexpectedly: " + (AuthBreaker.Sanitize(ex.Message) ?? "unknown error"),
    };

    private string PathFor(string userIdN) => Path.Combine(_dir, userIdN + ".json");

    private RatingsCache? Load(string userIdN)
    {
        try
        {
            var p = PathFor(userIdN);
            return File.Exists(p) ? JsonSerializer.Deserialize<RatingsCache>(File.ReadAllText(p)) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("LetterboxdLog: ignoring an unreadable ratings cache of {UserId}: {Message}", userIdN, ex.Message);
            return null;
        }
    }

    private void Save(string userIdN, RatingsCache data)
    {
        Directory.CreateDirectory(_dir);
        var p = PathFor(userIdN);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>Drops a user's cache (on unlink).</summary>
    public void Forget(string userIdN)
    {
        _lastFailure.TryRemove(userIdN, out _);
        try { File.Delete(PathFor(userIdN)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
