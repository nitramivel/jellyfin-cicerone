#!/usr/bin/env bash
# Builds the plugin and assembles a deployable folder for a Jellyfin 10.11.x
# plugin directory (e.g. /config/plugins/Cicerone_<version> in the container).
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0.0}"
TARGET_ABI="${TARGET_ABI:-10.11.0.0}"
OUT="artifacts/Cicerone_${VERSION}"

dotnet build Jellyfin.Plugin.Cicerone/Jellyfin.Plugin.Cicerone.csproj -c Release -p:Version="${VERSION%.*}"

rm -rf "$OUT"
mkdir -p "$OUT"
cp Jellyfin.Plugin.Cicerone/bin/Release/net9.0/Jellyfin.Plugin.Cicerone.dll "$OUT/"

cat > "$OUT/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "Checks subtitle tracks against the dialogue in the file — the right language, the right cut, and timed to the audio rather than merely present.",
  "guid": "83004bd9-e2d6-4e13-9f3a-73bb12ef1f96",
  "name": "Cicerone",
  "overview": "Subtitles that actually match the dialogue.",
  "owner": "nitramivel",
  "targetAbi": "${TARGET_ABI}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": ""
}
EOF

echo "Packaged: $OUT"
