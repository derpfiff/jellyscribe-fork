# Changes in this fork (Jellyfin 12.4 build)

Based on builtbyproxy/jellyfin-plugin-letterboxd ("Jellyscribe") v2.15.0, commit 7bdd2b61,
MIT licence (see LICENSE, unchanged). Version 2.15.0.124.

- Build: net10.0, compiled against the Jellyfin 12.4 server assemblies in `lib/` instead of the
  10.11 NuGet packages; DataProtection from the ASP.NET Core shared framework. `build.sh` builds
  in the .NET 10 SDK container. `meta.json` targets ABI 12.4.0.0.
- No automatic posting: `PlaybackHandler` and `RatingSyncHandler` are no longer registered, and
  the Letterboxd/Serializd "sync watched" runners return at once (`AutoSyncDisabled.cs`), whether
  started by the scheduled task, the dashboard button or the API. Diary import stays available
  (off per account by default).
- New `LetterboxdLog/` + `Api/LetterboxdLogController.cs`: authenticated endpoints
  `GET /LetterboxdLog/Status`, `POST /LetterboxdLog/Entry`, `GET /LetterboxdLog/Entries`.
  Entries are kept per user in `plugins/configurations/LetterboxdLog/<userId>.json`, keyed by
  item id + watched date; unchanged re-sends post nothing, changed ones PATCH the existing diary
  entry; unreachable Letterboxd => "queued", retried every 30 min (`LetterboxdLogRetryService`)
  and on the user's next Status/Entry call.
- `LetterboxdApiClient`: `FindLogEntryIdOnDateAsync`, `CreateLogEntryAsync`,
  `UpdateLogEntryAsync` (review + spoiler flag, half-star rating, like, rewatch, date).
- Not registered: `RepositoryMigrationService` (it added the upstream plugin repository, whose
  builds would replace this fork as an "update").
- Test hooks: `JELLYSCRIBE_LETTERBOXD_API_URL` points the API client at another server (and then
  disables the letterboxd.com website fallback); `JELLYSCRIBE_LOG_RETRY_MINUTES` changes the
  retry interval.
