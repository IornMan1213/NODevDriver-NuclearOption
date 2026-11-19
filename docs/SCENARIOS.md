# Scenarios

A scenario is a plain text file in `scenarios/`, one driver command per line. Blank lines are skipped. There are no
comments: every non-blank line is run as a command (an unknown verb only logs its `> line` and does nothing).

## Structure

```text
say START drop tank MiG29        a name for the run, shown in the results
include spawn.txt MiG29          shared parts: menus, spawn, takeoff...
weps
tanktest 1845 2 420              the measurement or checks
wait 50                          long enough for a coroutine command to finish
parts
say END                          the run finished
quit                             optional: close the game
```

- **`say END` is required.** `run.sh` reports a run without it as unfinished and exits 1.
- **`quit` is optional.** `run.sh` always moves it after its own end marker, so the results are logged before the game
  closes. `QUIT=1 ./run.sh ...` adds it to any scenario.
- **`release` is added for you** at the end of every run, so a scenario that held the stick or throttle hands them back.

## include

`include <file> [arg1 arg2 ...]` replaces the line with that file's lines. The file name is relative to the including
file, and includes nest. In the included file, `$1`, `$2` and so on are replaced by the arguments, before nested includes
are expanded, so an include can pass its own arguments on.

The main shared parts:

| File | What it does | Arguments |
| --- | --- | --- |
| `spawn.txt` | Main menu > Singleplayer > Free Flight - Heartland > Boscali Defense Force at boscali_north > selects the aircraft > Purchase > Fly. Ends in the jet on the ramp. | `$1` aircraft definition name (`MiG29`, `MiG29SMT`, a stock jet's name) |
| `flight.txt` | Runway, takeoff, a climb picture, `parts`, a 10-degree pull, gear down, runway, rollout and brake chute checks, `fps`. | `$1` a name for the pictures |
| `menu.txt` | `packcheck` from the main menu, the button list and a screenshot. | none |

Scenario names follow a pattern: a base file holds the steps (`intake_door.txt`), and a small per-aircraft file includes
it with the aircraft as the argument (`intake_door_mig29.txt`, `intake_door_smt.txt`). Run the small file.

## Timing

- Commands run 0.5 s apart. `wait <s>` adds a pause in real time.
- Menu clicks need a `wait` after them for the next screen to build. The values in `spawn.txt` are known to work:
  40 s after **START MISSION** for the map to load, 25 s after **FLY**.
- Commands marked **coroutine** in [COMMANDS.md](COMMANDS.md) return at once. Put a `wait` after them that covers the
  whole measurement, or the next command will start while they run.
- `run.sh` gives up after its timeout (default 900 s); pass a larger one for long scenarios.

## Results

`run.sh` prints every line that contains `[TEST]` or `Exception`, between its own start and end markers, then
`failed checks: N`. Commands that only measure (`state`, `parts`, `weps`...) log indented lines without `[TEST]`, so
they are not in that summary. To read them, open `BepInEx/LogOutput.log` (or Unity's `Player.log`) and find the
`[TEST] RUN run<id>` line where the run starts.

A good scenario logs enough to explain a failure: a `state`, `parts` or `weps` before and after the step under test, and
a `shot` at the moment that matters.

## Adding a check

1. Add a `case "<verb>":` to the switch in `Driver.cs` `Run`, with a one-line comment giving the syntax and what it
   does. That comment is the command's documentation; also add a row to [COMMANDS.md](COMMANDS.md).
2. Log measurements with `Logger.LogInfo("  ...")` (two spaces, so they read as the command's output).
3. Log pass or fail with `Test(bool ok, string what)`. `what` should say what was expected and what was found, so a
   failing line explains itself.
4. Anything that takes more than one frame goes in a coroutine (`StartCoroutine(...)`), and the command returns at once.
5. A larger group of commands goes in its own file with a static `Command(...)` entry point, as `NottTest.cs` and
   `ChaffTest.cs` do, and a single `case` line in `Run` sends its verbs there.
6. Read other mods' state by reflection, so the driver still loads when they are not installed. Look them up by
   type name or by `Chainloader.PluginInfos[guid]`, and handle "not loaded" with a clear message or FAIL.

The game's own classes are referenced directly (the project references `Assembly-CSharp.dll`). When a game update
renames a field, the build fails, or a reflection lookup returns null and the command logs it.

## The scenarios in this repository

They were written to test the MiG-29 mods and most of them fly a MiG. The ones that work on any aircraft, given its
definition name as the argument, are a good starting point:

| Scenario | Use |
| --- | --- |
| `menu.txt` | The smallest scenario: the button list and a screenshot. Its `packcheck` fails without the MiG-29 Pack; remove that line for other setups. |
| `spawn.txt` + `flight.txt` | Spawn and a full circuit: takeoff, pull, landing, braking. |
| `partshots_<jet>.txt` | Break-every-part pictures for that jet (stock jets included). |
| `blast_<jet>_<where>.txt`, `rays_<jet>.txt`, `lines_<jet>.txt` | Missile blast damage and the lines it traces, for comparing jets. |
| `intake_stock_<jet>.txt` | Intake pictures of stock jets. |
| `tank_*.txt` | Drop-tank release with drained tanks (`tanktest`). |
| `nott_MiG29.txt` | Countermeasures with NO Tactitools installed. |
| `hld_MiG29.txt` | Slat and flap moving parts. |
| `chaff_*.txt` | Chaff against real radar missiles fired by an AI foe. |

The workshop upload scenarios publish to Steam: see Safety in the [README](../README.md).
