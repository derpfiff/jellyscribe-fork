using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdSync.LetterboxdLog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LetterboxdSync.Api;

/// <summary>
/// Fork: the client-facing log API. Every call acts for the user of the calling Jellyfin token.
/// </summary>
[ApiController]
[Authorize]
[Route("LetterboxdLog")]
[Produces(MediaTypeNames.Application.Json)]
public class LetterboxdLogController : ControllerBase
{
    private readonly LetterboxdLogService _log;

    public LetterboxdLogController(LetterboxdLogService log)
    {
        _log = log;
    }

    private string? CallerId()
        => User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value?.Replace("-", string.Empty);

    /// <summary>{ enabled, configured, queued }: enabled = the user switched Letterboxd logging on;
    /// configured = enabled and credentials are stored.</summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Status()
    {
        var userId = CallerId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        var account = LetterboxdLogService.AccountFor(userId);
        var enabled = account != null;
        var configured = enabled && LetterboxdLogService.HasCredentials(account!);
        if (configured)
            _ = Task.Run(() => _log.RetryUserAsync(userId, dueOnly: false));
        return Ok(new { enabled, configured, queued = _log.QueuedCount(userId) });
    }

    /// <summary>Logs (or updates) one diary entry. Returns { status: posted|queued|error, message }.</summary>
    [HttpPost("Entry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Entry([FromBody] LogEntryRequest request)
    {
        var userId = CallerId();
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
        var userId = CallerId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();
        return Ok(_log.List(userId).OrderByDescending(r => r.UpdatedUtc).Select(r => new
        {
            r.ItemId, r.TmdbId, r.Title, r.WatchedDate, r.Rating, r.Liked, r.Rewatch, r.ContainsSpoilers,
            hasReview = r.Review != null, r.State, r.LastError, r.UpdatedUtc,
        }));
    }
}
