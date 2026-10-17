#!/bin/bash
# Build, deploy to both test prefixes, run the host+client bot test (Linux + Proton).
# Results land in test/results.txt as [COOPTEST] lines.
T="$(cd "$(dirname "$0")" && pwd)"; R="$T/.."
GAME="${STACKLANDS_DIR:-$HOME/.local/share/Steam/steamapps/common/Stacklands}"
DOTNET="${DOTNET:-dotnet}"
pkill -f "Stacklands[.]exe"; sleep 2
for X in A B; do [ -d "$T/$X" ] || cp -r "$GAME" "$T/$X"; done   # private copies of the game
"$DOTNET" build "$R/src" -c Release -p:GameDir="$GAME" 2>&1 | grep -E " error |Build succeeded" | sort -u
[ ${PIPESTATUS[0]} -eq 0 ] || exit 1
for X in A B; do
  M="$T/pfx$X/pfx/drive_c/users/steamuser/AppData/LocalLow/sokpop/Stacklands/Mods/stacklands_coop"
  mkdir -p "$M"; cp "$R/src/bin/Release/netstandard2.1/stacklands_coop.dll" "$R/manifest.json" "$M/"
done
rm -f "$T/results.txt"
(setsid "$T/launch.sh" A COOP_TEST=host "COOP_TEST_OUT=Z:$T/results.txt" > "$T/A.stdout" 2>&1 &)
sleep 1
(setsid "$T/launch.sh" B COOP_TEST=client "COOP_TEST_OUT=Z:$T/results.txt" > "$T/B.stdout" 2>&1 &)
echo launched
