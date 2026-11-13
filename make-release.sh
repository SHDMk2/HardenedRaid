#!/usr/bin/env bash
# Builds a clean, upload-ready copy of the mod (default: ~/HardenedRaidsRelease)
# (no .git, bin, obj or .pdb). Keeps About/PublishedFileId.txt if it already exists there.
# Do not put the copy inside RimWorld/Mods: two mods with the same packageId would be loaded.
set -euo pipefail
SRC="$(cd "$(dirname "$0")" && pwd)"
DST="${1:-$HOME/HardenedRaidsRelease}"

[ -d "$HOME/.dotnet" ] && export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
dotnet build "$SRC/Source/HardenedRaids/HardenedRaids.csproj" -c Release -v q -nologo

PFID=""
[ -f "$DST/About/PublishedFileId.txt" ] && PFID="$(cat "$DST/About/PublishedFileId.txt")"
[ -z "$PFID" ] && [ -f "$SRC/About/PublishedFileId.txt" ] && PFID="$(cat "$SRC/About/PublishedFileId.txt")"

rm -rf "$DST"
mkdir -p "$DST/About" "$DST/Assemblies" "$DST/Defs" "$DST/Source/HardenedRaids"
cp "$SRC"/About/*.xml "$DST/About/"
[ -f "$SRC/About/Preview.png" ] && cp "$SRC/About/Preview.png" "$DST/About/"
cp "$SRC/Assemblies/HardenedRaids.dll" "$DST/Assemblies/"
cp "$SRC"/Defs/*.xml "$DST/Defs/"
cp "$SRC/LICENSE" "$SRC/README.md" "$DST/"
cp "$SRC"/Source/HardenedRaids/*.cs "$SRC/Source/HardenedRaids/HardenedRaids.csproj" "$DST/Source/HardenedRaids/"
[ -n "$PFID" ] && echo -n "$PFID" > "$DST/About/PublishedFileId.txt"

echo "Release ready: $DST"
find "$DST" -type f | sort
