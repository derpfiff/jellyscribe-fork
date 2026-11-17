# Changes in this fork (Jellyfin 12.4 build)

Based on builtbyproxy/jellyfin-plugin-letterboxd ("Jellyscribe") v2.15.0, commit 7bdd2b61,
MIT licence (see LICENSE, unchanged). Version 2.15.0.127.

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
- 2.15.0.125: no Jellyscribe entry in the web client any more (`SidebarScriptStartupFilter` not
  registered, `SidebarInjection.cs` not built). New account API on `LetterboxdLog` for Cinema Glass:
  `GET /LetterboxdLog/Account` -> `{ linked, username, enabled, loginFailing, queued }`;
  `POST /LetterboxdLog/Account` `{ username, password, enabled? }` signs in once through the
  official API before storing (400 `{ error }` on a bad login, 429 when rate-limited, 502 when
  Letterboxd is unreachable); `{ enabled }` alone switches sending; `DELETE /LetterboxdLog/Account`
  unlinks. Admin-only `GET /LetterboxdLog/Overview` lists linked users with queued/posted/error
  counts. `configPage.html` (admin) and `userPage.html` (unlinked fallback) rewritten as minimal
  pages in the Cinema Glass look; sync, watchlist, Seerr, Serializd, telemetry, import, cookie and
  log screens are gone from the UI, their endpoints are unchanged.
- 2.15.0.127: settings page and account page follow Jellyfin's display language (German for de-*, English otherwise).
- 2.15.0.126: `GET /LetterboxdLog/Ratings[?refresh=1]` (caller's own token) returns every film the
  linked member rated, liked or marked watched, also without a diary entry:
  `{ linked, username, fetchedAt, stale, error, truncated, count, items: [{ tmdbId, title, year,
  rating, liked, watched }] }`. Read through the official API (`GET /films?member=<id>&
  memberRelationship=Watched&include=MemberRelationship`, 100 per page, cursor paging, pages one
  after another 400 ms apart, at most 5000 films; `JELLYSCRIBE_RATINGS_PAGE_DELAY_MS` changes the
  pause). Cached per user for 6 h in `plugins/configurations/LetterboxdLog/ratings/<userId>.json`;
  `refresh=1` reads again at most every 10 minutes; after a failed read Letterboxd is not asked
  again for that user for 2 minutes. Not linked: `{ linked: false, items: [] }`. Letterboxd
  unreachable: the cached copy with `stale: true`, otherwise 502 `{ error }`. Unlinking deletes
  the cache.
