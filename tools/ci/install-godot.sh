#!/usr/bin/env bash
# CI: downloads Godot (.NET, Linux) and its export templates into ~/.cache/godot-ci (cached between runs) and
# installs the templates where the editor looks for them. Usage: install-godot.sh 4.7.2
set -euo pipefail
version="${1:?Godot version, e.g. 4.7.2}"
cache="$HOME/.cache/godot-ci"
base="https://github.com/godotengine/godot-builds/releases/download/${version}-stable"
editor_zip="Godot_v${version}-stable_mono_linux_x86_64.zip"
templates="Godot_v${version}-stable_mono_export_templates.tpz"
mkdir -p "$cache"

download() {
  if [ ! -s "$cache/$1" ]; then
    echo "Downloading $1"
    curl -fsSL --retry 3 -o "$cache/$1.part" "$base/$1"
    mv "$cache/$1.part" "$cache/$1"
  fi
}
download "$editor_zip"
download "$templates"

if [ ! -d "$cache/editor" ]; then
  unzip -q "$cache/$editor_zip" -d "$cache/editor"
fi
godot="$(find "$cache/editor" -maxdepth 2 -type f -name "Godot_v${version}-stable_mono_linux.x86_64" | head -n1)"
[ -n "$godot" ] || { echo "Godot binary not found in $editor_zip" >&2; exit 1; }
chmod +x "$godot"
ln -sf "$godot" "$cache/godot"

dest="$HOME/.local/share/godot/export_templates/${version}.stable.mono"
if [ ! -f "$dest/linux_release.x86_64" ]; then
  mkdir -p "$dest"
  tmp="$(mktemp -d)"
  unzip -q "$cache/$templates" -d "$tmp"
  mv "$tmp"/templates/* "$dest"/
  rm -rf "$tmp"
fi
"$cache/godot" --version
ls "$dest" | head
