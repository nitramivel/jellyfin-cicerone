#!/usr/bin/env bash
# Builds a release zip for the Jellyfin plugin catalogue and updates
# manifest.json with the new version entry (including its MD5 checksum).
#
# Usage:   VERSION=0.1.0.0 CHANGELOG="What changed" ./build/release.sh
#
# Afterwards: create a GitHub release with tag v<VERSION> and upload the
# generated artifacts/cicerone_<VERSION>.zip as an asset — the manifest's
# sourceUrl points at exactly that location. Rebuilding or re-zipping changes
# the checksum and breaks catalogue installs.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0.0}"
TARGET_ABI="${TARGET_ABI:-10.11.0.0}"
CHANGELOG="${CHANGELOG:-}"
REPO_URL="https://github.com/nitramivel/jellyfin-cicerone"

VERSION="$VERSION" TARGET_ABI="$TARGET_ABI" ./build/package.sh

VERSION="$VERSION" TARGET_ABI="$TARGET_ABI" CHANGELOG="$CHANGELOG" REPO_URL="$REPO_URL" \
python3 - <<'PY'
import hashlib
import json
import os
import zipfile
from datetime import datetime, timezone

version = os.environ["VERSION"]
target_abi = os.environ["TARGET_ABI"]
changelog = os.environ["CHANGELOG"]
repo_url = os.environ["REPO_URL"]

folder = f"artifacts/Cicerone_{version}"
zip_path = f"artifacts/cicerone_{version}.zip"

# Jellyfin expects the plugin files at the ROOT of the zip; the server
# creates the plugins/<Name>_<version> folder itself on install.
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for name in sorted(os.listdir(folder)):
        zf.write(os.path.join(folder, name), arcname=name)

with open(zip_path, "rb") as f:
    checksum = hashlib.md5(f.read()).hexdigest()

entry = {
    "version": version,
    "changelog": changelog,
    "targetAbi": target_abi,
    "sourceUrl": f"{repo_url}/releases/download/v{version}/cicerone_{version}.zip",
    "checksum": checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

manifest_path = "manifest.json"
if os.path.exists(manifest_path):
    with open(manifest_path) as f:
        manifest = json.load(f)
else:
    manifest = [
        {
            "guid": "83004bd9-e2d6-4e13-9f3a-73bb12ef1f96",
            "name": "Cicerone",
            "description": "Checks subtitle tracks against the dialogue in the file.",
            "overview": "Subtitles that actually match the dialogue.",
            "owner": "nitramivel",
            "category": "General",
            "imageUrl": "",
            "versions": [],
        }
    ]

versions = [v for v in manifest[0]["versions"] if v["version"] != version]
versions.insert(0, entry)
manifest[0]["versions"] = versions

with open(manifest_path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print(f"Zip:      {zip_path}")
print(f"MD5:      {checksum}")
print(f"Manifest: {manifest_path} updated — upload the zip to release tag v{version}")
PY
