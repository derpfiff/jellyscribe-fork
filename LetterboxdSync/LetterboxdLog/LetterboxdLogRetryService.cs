using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.LetterboxdLog;

/// <summary>Fork: sends queued diary entries again every <see cref="LetterboxdLogService.RetryInterval"/>.</summary>
public sealed class LetterboxdLogRetryService : BackgroundService
{
    private readonly LetterboxdLogService _log;
    private readonly ILogger<LetterboxdLogRetryService> _logger;

    public LetterboxdLogRetryService(LetterboxdLogService log, ILogger<LetterboxdLogRetryService> logger)
    {
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(LetterboxdLogService.RetryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _log.RetryAllAsync(dueOnly: true, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "LetterboxdLog: retry round failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
