#!/usr/bin/env bash
# CI: exports the Linux dedicated server and the Windows client (presets in export_presets.cfg).
# Usage: export.sh 4.7.2 <commit sha>. Output: builds/server/ (with VERSION) and builds/ExploreTogether-client-<sha>.zip
set -euo pipefail
version="${1:?Godot version}"
sha="${2:?commit sha}"
short="${sha:0:7}"
godot="$HOME/.cache/godot-ci/godot"

rm -rf builds
mkdir -p builds/server builds/client

# Import assets first (a fresh checkout has no .godot folder); Godot exits when done.
"$godot" --headless --path . --import || true

"$godot" --headless --path . --export-release "Linux Server" builds/server/ExploreTogetherServer.x86_64
"$godot" --headless --path . --export-release "Windows Client" builds/client/ExploreTogether.exe

# Godot can exit 0 after a failed export: check the files are there.
test -x builds/server/ExploreTogetherServer.x86_64 || { echo "server export failed" >&2; exit 1; }
test -f builds/server/ExploreTogetherServer.pck || { echo "server export has no .pck" >&2; exit 1; }
test -f builds/client/ExploreTogether.exe || { echo "client export failed" >&2; exit 1; }

echo "$sha" > builds/server/VERSION
echo "$sha" > builds/client/VERSION
(cd builds/client && zip -qr "../ExploreTogether-client-${short}.zip" .)
ls -la builds builds/server
