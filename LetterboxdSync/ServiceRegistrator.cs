using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace LetterboxdSync;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Capture the Jellyfin version at startup so it's available immediately, not
        // only after the daily telemetry task first runs. A diagnostic bundle sent in
        // the first minutes after install (exactly when first-run problems surface)
        // must still know the Jellyfin version.
        var version = applicationHost?.ApplicationVersionString;
        if (!string.IsNullOrEmpty(version))
            TelemetryService.JellyfinVersion = version;

        serviceCollection.AddSingleton<LetterboxdSyncRunner>();
        serviceCollection.AddSingleton<WatchlistSyncRunner>();
        serviceCollection.AddSingleton<Serializd.SerializdSyncRunner>();
        serviceCollection.AddSingleton<Serializd.SerializdWatchlistSyncRunner>();
        serviceCollection.AddSingleton<Serializd.SerializdDiaryImportRunner>();
        // Fork: no automatic posting. PlaybackHandler (logs films/episodes when playback ends) and
        // RatingSyncHandler (pushes rating changes) are not registered; the user logs each film
        // explicitly through LetterboxdLogController, and queued entries are retried by
        // LetterboxdLogRetryService.
        // serviceCollection.AddHostedService<PlaybackHandler>();
        // serviceCollection.AddHostedService<RatingSyncHandler>();
        serviceCollection.AddSingleton<LetterboxdLog.LetterboxdLogService>();
        serviceCollection.AddHostedService<LetterboxdLog.LetterboxdLogRetryService>();
        // Fork: not registered. Upstream adds its own plugin repository to the server's catalog,
        // which would offer upstream builds as "updates" that replace this Jellyfin 12.4 fork.
        // serviceCollection.AddHostedService<RepositoryMigrationService>();
        serviceCollection.AddHostedService<UserIdentityService>();

        // Fork: no Jellyscribe entry in the web client's sidebar or user menu. The account is linked
        // in Cinema Glass through LetterboxdLogController, so sidebar.js is no longer injected
        // (SidebarScriptStartupFilter is not registered, SidebarInjection.cs is not compiled).
        // serviceCollection.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, SidebarScriptStartupFilter>();
    }
}
