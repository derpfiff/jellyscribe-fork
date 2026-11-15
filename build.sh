#!/bin/sh
# Jellyfin 12.4 fork: builds the plugin in a .NET 10 SDK container against the server
# assemblies in ./lib (copy MediaBrowser.*, Jellyfin.Data, Jellyfin.Database.Implementations,
# Jellyfin.Extensions and Newtonsoft.Json from your server's /jellyfin directory first).
set -e
cd "$(dirname "$0")"
docker run --rm -v "$PWD":/w -w /w/LetterboxdSync -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 sh -c "dotnet build LetterboxdSync.csproj -c Release -o /w/out ; rc=\$?; chown -R ${SUDO_UID:-$(id -u)}:${SUDO_GID:-$(id -g)} /w/out /w/LetterboxdSync/obj /w/LetterboxdSync/bin 2>/dev/null; exit \$rc"
