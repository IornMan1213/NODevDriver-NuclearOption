# How it works

## The plugin

`Driver` is a BepInEx 5 plugin (GUID `iornman.nodevdriver`, name "NO Dev Driver"). On `Awake` it:

1. Applies its one Harmony patch, `StickHold` (Harmony id `nodev.driver`).
2. Creates a hidden, scene-persistent GameObject with a small `Runner` component whose `Update` calls `Driver.Tick`.
   The runner lives on its own object so that loading a mission does not destroy it.

Every 0.5 s of real time, `Tick`:

- If the queue is empty and `BepInEx/mig29pack_cmd.txt` exists, reads its non-blank lines into the queue and deletes the
  file.
- Takes one command off the queue and runs it. An exception is logged as an error with the command, and the queue moves
  on.

`wait <s>` moves the next tick that far ahead. Nothing else blocks the queue: commands that take time start a coroutine
and return.

The command file is named `mig29pack_cmd.txt`, not something generic, so that other dev drivers that read their own
file (for example `nodev_cmd.txt`) can be installed at the same time without taking each other's commands.

## Holding the controls

The game's player pilot (`PilotPlayerState`) writes the real stick or mouse into the aircraft's control inputs every
physics step, and its own throttle every frame. To fly the jet, `StickHold` adds a postfix to
`PilotPlayerState.PlayerControls` and `PlayerAxisControls` that overwrites them after the game has:

| Field | Set by | Effect |
| --- | --- | --- |
| `Held` | `stick` | Fixed pitch, roll and yaw inputs. |
| `Level` | `level` and the flight commands | A pitch and roll attitude hold. Pitch is proportional to the error and damped by the pitch rate; roll likewise, with a sign, gain and bias that can be tuned per jet. |
| `Throttle` | `throttle` and the flight commands | The player's simulated throttle. |

`release` clears all three. Because the patch writes after the game's own input code, real stick input is ignored while
a hold is active. Nothing is held when no command asked for it.

Commands that fly a profile (`takeoff`, `gtest`, `blasttest`, `firetest`, `tanktest`) set up the jet themselves: they
place it at a height and speed, then use these holds. `tanktest`'s cruise hold scales its gains by (220 / speed)² so the
same loop stays stable from 220 to 420 m/s.

## Finding things

- **The player's jet** is `SceneSingleton<CombatHUD>.i.aircraft`.
- **The game's own types** (`Aircraft`, `UnitPart`, `CountermeasureManager`, `MountedMissile`...) are used directly from
  `Assembly-CSharp.dll`. Private fields are read with reflection.
- **Other mods** are found only by reflection: by assembly name, type name or BepInEx plugin GUID. The driver therefore
  loads and runs without them, and the commands that need them say so.
- **UI** is found through Unity's `Button`, TextMeshPro `TMP_Dropdown` and `TMP_InputField` components, matched by
  their visible label or object name.

## run.sh

`run.sh` turns a scenario file into a reliable pass or fail:

1. **Build and install.** It rebuilds the DLL when any `.cs` file is newer, and copies it into `BepInEx/plugins/NODevDriver`
   when it differs from the installed one. If the game is already running, it warns that a restart is needed to load
   the new DLL.
2. **Ownership.** If `NuclearOption.exe` is already running, the script stops (exit 2) unless `ATTACH=1`. A game it did
   not start may be someone playing, or another test run.
3. **Launch and load.** It deletes any stale command file, launches the game through Steam, and waits until BepInEx's
   log is newer than the game process and contains Blueprinter's `[Plugin] Done in` line. Then it waits 10 s more for
   the main menu.
4. **Driver check.** It confirms that `Loading [NO Dev Driver` appears in this session's log.
5. **Fencing.** It wraps the expanded scenario in `say RUN run<id>` and `say DONE run<id>`, where the id is the current
   time in nanoseconds, and moves any `quit` after the `DONE` marker. Results are read only between this run's own
   markers, so an earlier run's lines can never be mistaken for this one's.
6. **Waiting.** It polls every 3 s until the `DONE` marker appears in BepInEx's `LogOutput.log` or Unity's
   `Player.log`, the timeout passes, or the game closes.
7. **Results.** It prints every `[TEST]` and `Exception` line from the fenced section and counts the `FAIL` lines.

If a scenario is still waiting in the command file when `run.sh` starts, it waits for the driver to pick that one up
before writing its own.

## Limits

- **One game at a time.** The command file and the logs are shared. Two runs against one game would mix commands.
- **Real time.** Scenarios run at the game's speed. A spawn takes about 90 s; a full flight scenario takes several
  minutes.
- **Single player.** The commands move, damage and drain the fuel of the player's jet directly. In a multiplayer session that would
  desync or be refused.
- **Game updates.** A renamed field breaks the build or a reflection lookup. The fix is usually one name.
- **Menu text.** `click` and `pickin` match the visible labels, so a renamed button or mission breaks the scenarios that
  use it. `buttons` and `dropdowns` show what is there now.
- **The screen must stay awake.** On some desktops, a screensaver or a locked screen stalls the game's rendering, and
  with it every command. Keep the display awake during unattended runs.
