#!/bin/bash
# usage: launch.sh A|B [extra env...]  -- runs a test copy of Stacklands under its own Proton prefix
T="$(cd "$(dirname "$0")" && pwd)"; X="$1"; shift
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$T/pfx$X"
export SteamAppId=1948280 SteamGameId=1948280
export PROTON_LOG=0 WINEDEBUG=-all
cd "$T/$X"
exec env "$@" "$HOME/.local/share/Steam/steamapps/common/SteamLinuxRuntime_sniper/_v2-entry-point" --verb=waitforexitandrun -- "$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton" waitforexitandrun "$T/$X/Stacklands.exe" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile "$T/$X.log"
