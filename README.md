# NO Dev Driver

A developer-only BepInEx 5 plugin that drives **Nuclear Option** unattended, so a mod can be tested in the real game the
same way every time: click through the menus, spawn a jet, fly it, fire weapons, break parts, take pictures, and log
`[TEST] PASS` / `[TEST] FAIL` lines that a script collects.

It was built to test the MiG-29 mods (Fulcrum, SMT and the MiG-29 Pack), but most of its commands work on any aircraft,
stock or modded.

It is a test tool, not a mod for players. Never ship it in a mod's release, and don't leave it installed while you play.
It lets a text file take over the stick, the throttle and the menus.

## How it works, in one paragraph

The plugin polls `BepInEx/mig29pack_cmd.txt` twice a second. When the file appears, it reads every line into a queue,
deletes the file, and runs one command each tick (`wait <s>` pauses the queue). Each command logs `> command` and its
results to BepInEx's log. Checks log `[TEST] PASS ...` or `[TEST] FAIL ...`. [`run.sh`](run.sh) writes a scenario into
that file, fences it with unique start and end markers, waits for the end marker, and prints the `[TEST]` lines. It exits
non-zero if any check failed or the scenario did not finish. See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md).

## Requirements

- Nuclear Option (Steam app 2168680) with BepInEx 5.
- .NET SDK 8 or newer to build. The project targets `net472` and references the game's own assemblies, so the game must
  be installed where the build can find it.
- For `run.sh`: bash, and the game launched through Steam (`steam -applaunch`). It is written for Linux/Proton but also
  works in Git Bash or WSL on Windows if you set the paths (below).
- `run.sh` waits for Blueprinter's `[Plugin] Done in` log line before it starts, so it needs a Blueprinter-based mod
  installed (any of the MiG-29 mods qualifies). With no such mod, start the game yourself and use `ATTACH=1`.
- Optional: the mods that the mod-specific commands target. See the groups in [docs/COMMANDS.md](docs/COMMANDS.md).

## Build

```bash
cd src/NODevDriver && dotnet build -c Release
```

The output is `src/NODevDriver/bin/Release/NODevDriver.dll`. The game folder defaults to
`~/.steam/steam/steamapps/common/Nuclear Option` on Linux and
`C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option` on Windows. Override it with:

```bash
dotnet build -c Release -p:GameDir="/path/to/Nuclear Option"
```

`run.sh` builds the driver for you whenever a `.cs` file is newer than the DLL.

## Install

`run.sh` installs the DLL to `BepInEx/plugins/NODevDriver/NODevDriver.dll` itself. To install by hand, copy it there.
BepInEx loads plugins only at startup, so restart the game after installing or updating it.

To remove it, delete `BepInEx/plugins/NODevDriver/`.

## Run a scenario

```bash
./run.sh scenarios/menu.txt
```

```bash
./run.sh scenarios/fly_mig29.txt 1200
```

The second argument is the timeout in seconds (default 900). What `run.sh` does:

1. Builds and installs the driver if needed.
2. Refuses to drive a game it did not start. If Nuclear Option is already running, it stops with exit code 2. That game
   may be someone playing, or another test run. `ATTACH=1 ./run.sh ...` drives it anyway.
3. Otherwise launches the game through Steam and waits up to 10 minutes for it to finish loading.
4. Checks that the driver was loaded in this game session.
5. Expands `include` lines, writes the scenario to the command file, and waits for it to finish.
6. Prints every `[TEST]` line and any exception, then `failed checks: N`.

Exit status: 0 when no check failed and the scenario logged `say END`; 1 otherwise; 2 when the game was already running.

Environment variables:

| Variable | Meaning |
| --- | --- |
| `GameDir` | The game folder, if it is not in the default Steam library. |
| `PLAYER_LOG` | Unity's `Player.log`. The default is the Proton prefix path. On Windows it is `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`. |
| `ATTACH=1` | Drive a game that is already running. |
| `QUIT=1` | Close the game when the scenario ends, even if the scenario has no `quit` line. |

Results come from whichever of BepInEx's `LogOutput.log` and Unity's `Player.log` holds the run's end marker. BepInEx's
log can lose its last lines when the game quits.

### Driving it without run.sh

Write commands to `BepInEx/mig29pack_cmd.txt` while the game is running, one per line:

```bash
printf 'state\nshot now.png\n' > "$HOME/.steam/steam/steamapps/common/Nuclear Option/BepInEx/mig29pack_cmd.txt"
```

Results appear in `BepInEx/LogOutput.log`. Screenshots and other pictures are saved in the `BepInEx` folder.

## Write a scenario

A scenario is a text file with one command per line:

```text
say START MiG-29 flight
include spawn.txt MiG29
packcheck
include flight.txt mig29
say END
quit
```

`include <file> [args]` pulls in another file, with `$1`, `$2` and so on replaced by the arguments. `say END` marks a
finished run. A check is any command that logs `[TEST] PASS` or `[TEST] FAIL`. See [docs/SCENARIOS.md](docs/SCENARIOS.md)
for the format, the shared parts, and how to add a new check.

## Documentation

- [docs/COMMANDS.md](docs/COMMANDS.md): every command, grouped by what it is for.
- [docs/SCENARIOS.md](docs/SCENARIOS.md): the scenario format, the shared parts, the scenarios in this repo, and how to add commands.
- [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md): the command loop, how the stick hold works, the run fencing, and its limits.
- [CHANGELOG.md](CHANGELOG.md)

## Layout

```text
run.sh                     the test runner
src/NODevDriver/           the plugin (Driver.cs holds the command switch; the other files are larger test groups)
scenarios/                 scenario files; include paths are relative to the including file
docs/                      documentation
```

## Safety

- `run.sh` never drives a game it did not start, unless you pass `ATTACH=1`.
- Scenarios that end in `quit` close the game. Before a run, check that nobody is playing.
- Before launching the game, check that no other test run is using it.
- `scenarios/workshop_up1.txt`, `workshop_up_rest.txt` and `smt_workshop_upload.txt` press the game's real **Create New**
  button and publish liveries to the Steam Workshop. `workshop_dry.txt` fills the same page without uploading. Read any
  workshop scenario before you run it.
- The driver holds the stick and throttle while a flight command runs. `release` (which `run.sh` adds at the end of
  every run) gives them back.

## Interoperating with other mods

Some commands read other mods' state by reflection (Quartermaster, NO Gauges, BepInEx Configuration Manager, NO
Tactitools). They only call those mods' public entry points or press their keys the way a player would. No code or
assets from other authors are copied into this repository.

## Licence

Private repository. All rights reserved.
