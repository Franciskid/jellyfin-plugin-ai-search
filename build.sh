#!/usr/bin/env bash
# Build the AiSearch Jellyfin plugin DLL reproducibly using the .NET SDK inside
# Docker — no local dotnet install needed. Produces dist/AiSearch/ containing the
# plugin DLL + meta.json, ready to drop into Jellyfin's plugins directory:
#
#   ./build.sh
#   # then copy dist/AiSearch/ into <jellyfin-config>/plugins/AiSearch_<version>/
#   # (or the plugins path your install uses), and restart Jellyfin.
#
# After installing, configure it in the Jellyfin dashboard (Plugins > AI Search):
#   Platform mode: Platform API URL + application API key + model alias, or
#   Direct mode:   any OpenAI-compatible endpoint URL + API key + model.
set -euo pipefail

cd "$(dirname "$0")"

IMAGE="mcr.microsoft.com/dotnet/sdk:9.0"
PROJECT="Jellyfin.Plugin.AiSearch/Jellyfin.Plugin.AiSearch.csproj"
BUILD_DIR="dist/build"
OUT="dist/AiSearch"

if ! command -v docker >/dev/null 2>&1; then
  echo "build.sh: docker is required (or install the .NET 9 SDK and run 'dotnet build $PROJECT -c Release')." >&2
  exit 1
fi

echo "build.sh: building $PROJECT with $IMAGE ..."
# Build + package inside the container (which runs as root over the bind mount),
# then hand ownership of dist/ back to the invoking user so host-side copies work.
docker run --rm -v "$PWD":/src -w /src "$IMAGE" sh -lc "
  set -e
  dotnet build '$PROJECT' -c Release -o '/src/$BUILD_DIR'
  rm -rf '/src/$OUT' && mkdir -p '/src/$OUT'
  cp '/src/$BUILD_DIR/Jellyfin.Plugin.AiSearch.dll' '/src/$OUT/'
  cp '/src/Jellyfin.Plugin.AiSearch/meta.json' '/src/$OUT/'
  chown -R $(id -u):$(id -g) '/src/dist'
"

echo "build.sh: done -> $OUT"
ls -la "$OUT"
