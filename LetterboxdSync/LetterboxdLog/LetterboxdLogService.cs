using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.LetterboxdLog;

/// <summary>What a client sends to <c>POST /LetterboxdLog/Entry</c>.</summary>
public sealed class LogEntryRequest
{
    public string? ItemId { get; set; }
    public int? TmdbId { get; set; }
    public string? WatchedDate { get; set; }
    public double? Rating { get; set; }
    public bool Liked { get; set; }
    public string? Review { get; set; }
    public bool ContainsSpoilers { get; set; }
    public bool Rewatch { get; set; }
}

/// <summary>One confirmed diary entry, as kept in the per-user store.</summary>
public sealed class LogRecord
{
    public string Key { get; set; } = string.Empty;
    public string? ItemId { get; set; }
    public int TmdbId { get; set; }
    public string? Title { get; set; }
    public string WatchedDate { get; set; } = string.Empty;
    public double? Rating { get; set; }
    public bool Liked { get; set; }
    public string? Review { get; set; }
    public bool ContainsSpoilers { get; set; }
    public bool Rewatch { get; set; }

    /// <summary>queued | posted | error.</summary>
    public string State { get; set; } = "queued";
    public string? LetterboxdEntryId { get; set; }
    public string? LetterboxdAccount { get; set; }

    /// <summary>Fingerprint of the content that is on Letterboxd now (null until first posted).</summary>
    public string? PostedFingerprint { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }

    internal string Fingerprint() => string.Join("\u001f",
        Rating?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-", Liked, Review ?? string.Empty, ContainsSpoilers, Rewatch);
}

public sealed record LogResult(string Status, string Message, LogRecord? Record = null);

/// <summary>
/// Fork: the only way this build writes to Letterboxd. A user confirms a diary entry in the web
/// client (Cinema Glass's log dialog); the entry is stored per user under
/// <c>plugins/configurations/LetterboxdLog/&lt;userId&gt;.json</c> and posted. When Letterboxd cannot
/// be reached it stays queued and <see cref="LetterboxdLogRetryService"/> (and the user's next call)
/// tries again. The key is item + watched date, so the same entry is never posted twice; sending
/// it again with other content updates the existing Letterboxd diary entry.
/// </summary>
public sealed class LetterboxdLogService
{
    internal const int MaxAttempts = 48;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LetterboxdLogService> _logger;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new();

    public LetterboxdLogService(ILibraryManager libraryManager, IApplicationPaths paths, ILogger<LetterboxdLogService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _dir = Path.Combine(paths.PluginConfigurationsPath, "LetterboxdLog");
    }

    internal static TimeSpan RetryInterval { get; } = TimeSpan.FromMinutes(
        double.TryParse(Environment.GetEnvironmentVariable("JELLYSCRIBE_LOG_RETRY_MINUTES"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 30);

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public static Account? AccountFor(string userIdN) => Config.GetPrimaryAccountForUser(userIdN);

    public static bool HasCredentials(Account a) => a.HasPassword || a.HasRawCookies;

    public int QueuedCount(string userIdN) => Load(userIdN).Count(r => r.State == "queued");

    /// <summary>Entry counts of one user by state, for the admin overview.</summary>
    public (int Queued, int Posted, int Errors) Counts(string userIdN)
    {
        var all = Load(userIdN);
        return (all.Count(r => r.State == "queued"), all.Count(r => r.State == "posted"), all.Count(r => r.State == "error"));
    }

    public IReadOnlyList<LogRecord> List(string userIdN) => Load(userIdN);

    public async Task<LogResult> SubmitAsync(string userIdN, LogEntryRequest req)
    {
        // ---- validate ----
        if (!DateTime.TryParseExact(req.WatchedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new LogResult("error", "watchedDate must be YYYY-MM-DD");
        if (date.Date > DateTime.Now.Date.AddDays(1) || date.Year < 1900)
            return new LogResult("error", "watchedDate is not a plausible watch date");
        if (req.Rating is double r && (r < 0.5 || r > 5 || Math.Abs(r * 2 - Math.Round(r * 2)) > 1e-9))
            return new LogResult("error", "rating must be null or 0.5 to 5 in half steps");
        if (req.Review is { Length: > 20000 })
            return new LogResult("error", "review is too long");

        var account = AccountFor(userIdN);
        if (account == null || !HasCredentials(account))
            return new LogResult("error", "Letterboxd logging is not enabled for this user");

        int tmdbId = req.TmdbId ?? 0;
        string? title = null;
        string? itemId = null;
        if (!string.IsNullOrWhiteSpace(req.ItemId) && Guid.TryParse(req.ItemId, out var itemGuid))
        {
            itemId = itemGuid.ToString("N");
            var item = _libraryManager.GetItemById(itemGuid);
            if (item != null)
            {
                title = item.Name;
                if (int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), out var t))
                    tmdbId = t;
            }
        }

        if (tmdbId <= 0)
            return new LogResult("error", "No TMDb id for this film");

        var key = (itemId ?? "tmdb" + tmdbId.ToString(CultureInfo.InvariantCulture)) + "|" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var gate = _userLocks.GetOrAdd(userIdN, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        LogResult result;
        try
        {
            var records = Load(userIdN);
            var rec = records.FirstOrDefault(x => x.Key == key);
            var now = DateTime.UtcNow;
            if (rec == null)
            {
                rec = new LogRecord { Key = key, CreatedUtc = now };
                records.Add(rec);
            }

            rec.ItemId = itemId;
            rec.TmdbId = tmdbId;
            rec.Title = title ?? rec.Title;
            rec.WatchedDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            rec.Rating = req.Rating;
            rec.Liked = req.Liked;
            rec.Review = string.IsNullOrWhiteSpace(req.Review) ? null : req.Review;
            rec.ContainsSpoilers = rec.Review != null && req.ContainsSpoilers;
            rec.Rewatch = req.Rewatch;
            rec.UpdatedUtc = now;

            if (rec.PostedFingerprint == rec.Fingerprint() && rec.LetterboxdEntryId != null)
            {
                rec.State = "posted";
                Save(userIdN, records);
                return new LogResult("posted", "Already on Letterboxd, nothing changed", rec);
            }

            rec.State = "queued";
            rec.Attempts = 0;
            rec.NextAttemptUtc = now;
            result = await TryPostAsync(userIdN, account, rec).ConfigureAwait(false);
            Save(userIdN, records);
        }
        finally
        {
            gate.Release();
        }

        // Anything else this user had waiting gets a go as well (not awaited by the caller).
        _ = Task.Run(() => RetryUserAsync(userIdN, dueOnly: false));
        return result;
    }

    /// <summary>Retries queued entries of every user (or only those that are due).</summary>
    public async Task RetryAllAsync(bool dueOnly, CancellationToken ct)
    {
        if (!Directory.Exists(_dir))
            return;
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            await RetryUserAsync(Path.GetFileNameWithoutExtension(file), dueOnly).ConfigureAwait(false);
        }
    }

    public async Task RetryUserAsync(string userIdN, bool dueOnly)
    {
        var account = AccountFor(userIdN);
        if (account == null || !HasCredentials(account))
            return; // logging switched off for this user: queued entries wait, nothing is sent

        var gate = _userLocks.GetOrAdd(userIdN, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return; // a submit or another retry is running for this user
        try
        {
            var records = Load(userIdN);
            var now = DateTime.UtcNow;
            var due = records.Where(x => x.State == "queued" && (!dueOnly || (x.NextAttemptUtc ?? now) <= now)).ToList();
            if (due.Count == 0)
                return;
            foreach (var rec in due)
            {
                var res = await TryPostAsync(userIdN, account, rec).ConfigureAwait(false);
                Save(userIdN, records);
                if (res.Status == "queued")
                    break; // Letterboxd still unreachable: leave the rest for the next round
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LetterboxdLog: retry for user {UserId} failed", userIdN);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Posts (or updates) one record and sets its state. Never throws.</summary>
    private async Task<LogResult> TryPostAsync(string userIdN, Account account, LogRecord rec)
    {
        rec.Attempts++;
        var date = DateTime.ParseExact(rec.WatchedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        ILetterboxdService? service = null;
        try
        {
            service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                .ConfigureAwait(false);
            var film = await service.LookupFilmByTmdbIdAsync(rec.TmdbId).ConfigureAwait(false);

            if (service is LetterboxdApiClient api)
            {
                // A first post (or one whose earlier outcome is unknown) checks the diary first, so
                // an entry that already went through is updated instead of logged a second time.
                var entryId = rec.LetterboxdEntryId
                    ?? await api.FindLogEntryIdOnDateAsync(film.FilmId, date).ConfigureAwait(false);
                if (entryId == null)
                {
                    entryId = await api.CreateLogEntryAsync(film.FilmId, date, rec.Rating, rec.Liked, rec.Review, rec.ContainsSpoilers, rec.Rewatch)
                        .ConfigureAwait(false);
                    entryId ??= await api.FindLogEntryIdOnDateAsync(film.FilmId, date).ConfigureAwait(false);
                    Done(rec, account, entryId);
                    _logger.LogInformation("LetterboxdLog: logged {Title} ({Date}) for user {UserId}", rec.Title ?? film.Slug, rec.WatchedDate, userIdN);
                    return new LogResult("posted", "Logged on Letterboxd", rec);
                }

                await api.UpdateLogEntryAsync(entryId, date, rec.Rating, rec.Liked, rec.Review, rec.ContainsSpoilers, rec.Rewatch)
                    .ConfigureAwait(false);
                Done(rec, account, entryId);
                _logger.LogInformation("LetterboxdLog: updated the {Date} entry of {Title} for user {UserId}", rec.WatchedDate, rec.Title ?? film.Slug, userIdN);
                return new LogResult("posted", "Updated the existing Letterboxd diary entry", rec);
            }

            // Website session (official API sign-in not available for this account): it can only
            // add entries, so an already posted entry cannot be edited from here.
            if (rec.PostedFingerprint != null)
            {
                rec.State = "error";
                rec.LastError = "Editing a posted entry needs the official Letterboxd API sign-in";
                return new LogResult("error", rec.LastError, rec);
            }

            if (rec.Review != null)
                await service.PostReviewAsync(film.Slug, rec.Review, rec.ContainsSpoilers, rec.Rewatch, rec.WatchedDate, rec.Rating, rec.TmdbId).ConfigureAwait(false);
            else
                await service.MarkAsWatchedAsync(film.Slug, film.FilmId, date, rec.Liked, film.ProductionId, rec.Rewatch, rec.Rating).ConfigureAwait(false);
            Done(rec, account, "web");
            return new LogResult("posted", "Logged on Letterboxd", rec);
        }
        catch (Exception ex) when (IsPermanent(ex))
        {
            rec.State = "error";
            rec.LastError = AuthBreaker.Sanitize(ex.Message);
            rec.NextAttemptUtc = null;
            _logger.LogWarning("LetterboxdLog: {Title} for user {UserId} was refused: {Message}", rec.Title, userIdN, rec.LastError);
            return new LogResult("error", rec.LastError ?? "refused", rec);
        }
        catch (Exception ex)
        {
            rec.LastError = AuthBreaker.Sanitize(ex.Message);
            if (rec.Attempts >= MaxAttempts)
            {
                rec.State = "error";
                rec.NextAttemptUtc = null;
                return new LogResult("error", "Gave up after repeated failures: " + rec.LastError, rec);
            }

            rec.State = "queued";
            rec.NextAttemptUtc = DateTime.UtcNow + RetryInterval;
            _logger.LogInformation("LetterboxdLog: Letterboxd not reachable for {Title} (user {UserId}), queued: {Message}", rec.Title, userIdN, rec.LastError);
            return new LogResult("queued", "Letterboxd is not reachable right now; the entry is saved and will be sent automatically", rec);
        }
        finally
        {
            service?.Dispose();
        }
    }

    private static void Done(LogRecord rec, Account account, string? entryId)
    {
        rec.State = "posted";
        rec.LetterboxdEntryId = entryId;
        rec.LetterboxdAccount = account.LetterboxdUsername;
        rec.PostedFingerprint = rec.Fingerprint();
        rec.LastError = null;
        rec.NextAttemptUtc = null;
    }

    /// <summary>Errors that a retry cannot fix: unknown film, refused credentials, a rejected entry.</summary>
    internal static bool IsPermanent(Exception ex) => ex switch
    {
        FilmNotFoundException => true,
        LetterboxdApiAuthException a => a.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized,
        LetterboxdLogHttpException h => h.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity,
        _ => false,
    };

    private string PathFor(string userIdN) => Path.Combine(_dir, userIdN + ".json");

    private List<LogRecord> Load(string userIdN)
    {
        try
        {
            var p = PathFor(userIdN);
            return File.Exists(p) ? JsonSerializer.Deserialize<List<LogRecord>>(File.ReadAllText(p)) ?? new() : new();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LetterboxdLog: could not read the store of user {UserId}", userIdN);
            return new();
        }
    }

    private void Save(string userIdN, List<LogRecord> records)
    {
        Directory.CreateDirectory(_dir);
        var p = PathFor(userIdN);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(records, JsonOpts));
        File.Move(tmp, p, overwrite: true);
    }
}
