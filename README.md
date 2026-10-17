# Stacklands Co-op

Online co-op for [Stacklands](https://store.steampowered.com/app/1948280/Stacklands/), built on the game's official mod loader.
Everyone plays the same board at the same time. Each player can drag, stack, click, sell and buy cards, and each player sees the other players' cursors.

## How it works

- The **host's game is the real game**. Their simulation runs as normal and saves to their own save slot.
- **Joiners mirror the host.** A joiner's own simulation is frozen. The host streams every card's state (position, stack, timers, damage, status effects) about 15 times a second. Each joiner action (drag, drop, click, sell, speed change, cutscene "Continue") is sent to the host, and the host carries it out.
- Joiners' own save files are never touched. Leaving the session reloads the joiner's own save.

## Install (every player)

1. Build the mod or download a release. You need `stacklands_coop.dll` and `manifest.json`.
2. Put both files in a folder called `stacklands_coop` inside the Stacklands Mods folder:
   - Windows: `%AppData%\..\LocalLow\sokpop\Stacklands\Mods\stacklands_coop\`
   - Linux (Proton): `<steam library>/steamapps/compatdata/1948280/pfx/drive_c/users/steamuser/AppData/LocalLow/sokpop/Stacklands/Mods/stacklands_coop/`
3. Start the game. Mods are listed in the game's Mods menu.

**All players should run the same set of mods.** The co-op window warns you if your mods don't match the host's.

## Play

Press **F8** in game to open the co-op window.

**Host:**
1. Start a new run or continue one.
2. Press F8, then choose **Host (Steam friends)** and **Invite Steam friends**. Or choose **Host (LAN/IP)**; it uses port 27777, which must be forwarded for internet play.

**Join:**
- **Steam:** stay on the main menu, then accept the invite or use *Join game* from the Steam friends list.
- **IP:** stay on the main menu, press F8, type the host's IP and press **Join IP**.

If the host goes back to the main menu, joiners wait and rejoin automatically when the host continues a run.

## Known issues

- **The Steam transport is untested** (it needs two Steam accounts). LAN/IP play is tested by the automated two-instance test.
- Combat animations and some visual effects (screen shake, card wobble, particles) only play on the host. Joiners see the results: cards moving, health changing and cards dying.
- Cities DLC: the electricity and sewer connectors drawn between cards are not synced yet.
- Only the host can travel between boards (island, forest, spirit worlds). Joiners follow automatically.
- Joiners don't get Steam achievements.
- If two players grab the same card at the same moment, the host decides who gets it, and the other player's card snaps back.

## Build

Requires the .NET SDK (any version that can target `netstandard2.1`).

```bash
dotnet build src -c Release -p:GameDir="/path/to/steamapps/common/Stacklands"
```

The output is `src/bin/Release/netstandard2.1/stacklands_coop.dll`.

## Logs

The game turns off Unity logging, so the mod writes its own log to `coop_log.txt` in the save folder (`LocalLow/sokpop/Stacklands/`).

## Test

`test/run.sh` (Linux + Proton) builds the mod, makes two private copies of the game with separate Proton prefixes, and runs a host bot and a client bot against each other over LAN. Results are written to `test/results.txt`. Set `STACKLANDS_DIR` and `DOTNET` if your paths differ.

## License

MIT
