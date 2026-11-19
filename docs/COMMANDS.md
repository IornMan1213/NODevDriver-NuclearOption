# Command reference

Each line of a scenario (or of `BepInEx/mig29pack_cmd.txt`) is one command: a verb, a space, then its argument text.
Verbs are not case-sensitive. Arguments are split on spaces unless a command says otherwise. Numbers use a dot for
decimals, whatever the system locale.

Conventions used below:

- **The player's jet** is the aircraft the combat HUD is showing. Most flight, damage and store commands do nothing
  (and log `no aircraft`) until you are flying.
- **Aircraft frame**: x is right, y is up, z is forward, in metres, from the jet's root transform.
- **Pictures** are saved in the game's `BepInEx` folder under the file name given.
- A command marked **check** logs `[TEST] PASS ...` or `[TEST] FAIL ...` lines that `run.sh` counts.
- A command marked **coroutine** returns at once and keeps running over the following frames. Follow it with a
  `wait` long enough for it to finish.

The source for each command is the `case` with the same name in `src/NODevDriver/Driver.cs`. The larger groups live in
their own files, as noted.

## Run control

| Command | What it does |
| --- | --- |
| `say <text>` | Logs `[TEST] <text>`. `say START <name>` and `say END` mark a scenario; `run.sh` treats a run without `[TEST] END` as unfinished. |
| `wait <s>` | Pauses the queue for that many seconds of real time. The queue also waits 0.5 s between any two commands. |
| `release` | Gives the stick and throttle back to the player and turns off any attitude hold. `run.sh` adds it to the end of every run. |
| `quit` | Logs `[TEST] quitting` and closes the game. |

## Menus and UI

| Command | What it does |
| --- | --- |
| `buttons` | Lists every active, clickable button: its label, object name and screen position. |
| `click <text>` | Clicks the first active button whose label or object name contains the text (case-insensitive). |
| `clickat <x> <y>` | A full pointer down, up and click at that screen point (pixels, top-left origin), on whatever UI is there. |
| `find <text>` | Active UI objects whose name contains the text, with screen positions and labels. |
| `inspect <name>` | Every field of every component on active objects with that name. |
| `mouse` | Where Unity thinks the cursor is. |
| `base <text>` | On the mission map, clicks the icon of the airbase whose name contains the text. |
| `pickac <name>` | On the aircraft selection screen, clicks the button for that aircraft definition (exact name, for example `MiG29SMT`). |
| `dropdowns` | Lists every TextMeshPro dropdown, with its index and options. |
| `ddopen <index>` / `ddclose` | Opens that dropdown's list / closes every open list. |
| `pick <dropdown> <option>` | Selects an option by indices. |
| `pickin <dropdown> <text>` | In that dropdown, selects the option named exactly that, else the first that contains it. The loadout screen's stations are dropdowns 1 and up. |
| `pickopt <text>` | In whichever active dropdown offers it, selects the option that contains the text. |
| `pickfree <text>` | As `pickopt`, but only in a dropdown no earlier `pickfree` used (for several identical stores on one jet). |
| `type <text>` / `submit` | Types into the active input field named "Search" / presses Enter there. |
| `configmgr true\|false` | Opens or closes BepInEx Configuration Manager's window. |
| `cmsearch <text>` | Types into Configuration Manager's search box. |

## Mod settings

| Command | What it does |
| --- | --- |
| `config <guid>\|<section>\|<key>\|<value>` | Sets any BepInEx config entry of that plugin, converting the text to the entry's type (bool, number, enum, key...). Throws if the plugin or entry is missing. |
| `cfgset <guid>\|<section>\|<key>\|<value>` | Sets a **string** config entry, with a warning instead of an error when the plugin or entry is missing. |

Both change the value live, so the plugin's `SettingChanged` handlers fire, and BepInEx saves it to the plugin's `.cfg`
file. Set it back at the end of the scenario if the test should not leave it changed.

## Quartermaster (loadout mod)

| Command | What it does |
| --- | --- |
| `svinfo [n]` | The station view's texts and dots. **check** with `n`: there are n dots. |
| `svsave <name>` / `svload <name>` | Saves the current loadout / applies a saved one. |
| `svstatus <text>` | **check**: the station view's status line contains the text. |
| `acsearch <text\|->` | Types into the aircraft search (`-` clears it). |
| `acfilter All\|Air\|Ground\|Mods\|Favourites` | Sets the aircraft filter. |
| `acstar <name or key>` | Stars or unstars an aircraft. |
| `acshown <n>` | **check**: n aircraft buttons are shown. |
| `qmrows [n]` | The open list's rows, top to bottom. **check** with `n`: there are n weapons. |

## Liveries and the Workshop

| Command | What it does |
| --- | --- |
| `liveryupload <Skins folder>` | Runs the game's own upload check for that livery (what Workshop > Upload > Aircraft Livery runs) and logs its tags. Uploads nothing. |
| `liveryshots <object> <prefix> [<option>]` | For every option of the livery dropdown (the one offering `<option>`, default "Desert"), pictures of the preview jet from above, the side and the tail. |
| `uploaddump` | Every field of the Workshop upload panel's third page. |
| `uploadprep <preview image> <description file>` | Fills the upload page as a player would (description, Public, Mod Required, preview). Uploads nothing on its own; `click Create New` afterwards does. |
| `uploadstatus` | After **Create New**: the result ("Uploading", "Success", "Failed: ...") and the Workshop item id. |

## Camera and pictures

| Command | What it does |
| --- | --- |
| `shot <file>` | A screenshot of the game window. |
| `view <x y z> <lx ly lz> <fov> <file>` | A picture from a camera at a point in the aircraft frame, looking at another point. A negative FOV gives an orthographic view of that half-height. |
| `camat <target> <dx> <dy> <dz> <fov> <file>` | A picture from an offset (aircraft frame) looking at a target: a transform name, `~object` (the centre of that object's rendered bounds) or `@ac` (the jet). |
| `wview <object> <dx dy dz> <fov> <file>` | A picture from the object's position plus a world offset, looking at it. Works on wrecks after they leave the jet. |
| `camstate orbit\|cockpit` | Switches the game camera as its view keys do, so the cockpit's renderer swap runs. |
| `caminfo` | Every enabled camera: position, clip planes, FOV, depth and culling mask. |
| `sun` | The sun's elevation and azimuth relative to the jet's nose. |
| `fps [s]` | Average and worst frame time over the next s seconds (default 3). **coroutine** |
| `texlimit <n>` | The global texture mip limit (0 full, 1 half, 2 quarter: the game's Texture Quality). |
| `lodbias <v>` | `QualitySettings.lodBias`. Small values force the distant LOD levels up close. |

## Renderers and materials

| Command | What it does |
| --- | --- |
| `renderers <text>` | Renderers whose name or parent's name contains the text: state and bounds. |
| `pos <text>` | Aircraft-frame positions of the jet's transforms whose name contains the text. |
| `rlayer <prefix> <layer>` | Moves the jet's renderers whose name starts with the prefix to that layer. |
| `rvis <prefix> on\|off` | Shows or hides them. |
| `matinfo <prefix>` | Their material, shader, textures and float properties. |
| `rnear <x y z> <radius>` | The jet's renderers whose bounds come within that radius of a point (aircraft frame). |
| `rabove <y>` | The jet's enabled renderers that reach above that height, with their extents. |
| `shaderprops` | The skin shader's property list. |
| `skinprefix <prefix>` | Which renderers the four commands below act on (default `MiG29_skin`). |
| `dmgtex <basecolorDmg png\|-> [normalDmg png\|-]` | Replaces the damage textures on those skin materials (runtime only). |
| `skinhp <v>` | Sets `_HitPoints` on those materials: the damage look, with no damage. |
| `skinprop <name> <float>` | Sets any float on those materials. |
| `skintex <property> <png\|@white\|@flatnormal>` | Sets any texture on those materials. Normal maps load as linear. |
| `skinshadow cast\|receive\|both\|none` | Which shadows the jet's skin casts and receives. |

## Flying

| Command | What it does |
| --- | --- |
| `state` | Speed, radar altitude, throttle and gear of the jet. |
| `runway` | Puts the jet at rest at the start of the nearest takeoff runway, facing down it. |
| `takeoff <throttle> <rotate km/h> [pull]` | A full takeoff run: rotate at that speed (pull 0..1 nose up, default 0.6), 10 s climb-out, then logs the takeoff numbers. **coroutine** |
| `rollout <km/h>` | On the runway, rolls at that speed, then idle and full brakes, and logs the stopping distance. **coroutine** |
| `stick <pitch> <roll> <yaw>` / `stick off` | Holds the game's control inputs there (-1..1; pitch + is nose **down**). |
| `level <pitch> <roll> [sign] [gain] [bias]` / `level off` | Holds that attitude (degrees; + nose up, + right wing down) with the stick. The optional roll sign, gain and bias tune the roll loop for jets that need it. |
| `throttle <0..1>` | Holds the throttle there (1 = full afterburner). The throttle stays there when the hold ends. |
| `assist true\|false` | **check**: the player's flight assist is in that state. |
| `hold <height m>` | Lifts the jet and freezes it there (kinematic). `hold 0` releases it. |
| `move <dy m> [forward m]` | Shifts the held jet up or down and along its heading. |
| `drop <height m> <sink m/s> [forward m/s]` | Lifts the jet and drops it onto the ground, logging the gear for 4 s. **coroutine** |
| `setgear true\|false` | Gear down or up. |
| `gear` | Each landing gear: compression, maximum, hinge angle, enabled. |
| `gtest <pitch deg>` | At 1,500 m and 220 m/s, pulls the nose up that far, and logs g and how far each part moves off the root. **coroutine** |

## Weapons, stores and countermeasures

| Command | What it does |
| --- | --- |
| `weps` | The jet's weapon stations: weapon name and ammo. |
| `firetest <name part>, <name part>, ...` | At 1,500 m and 220 m/s, fires each named weapon in turn (guns for 0.6 s). **coroutine** |
| `mounts` | What hangs under each hardpoint: renderer bounds in the hardpoint's frame. |
| `under <hardpoint>` | Every enabled renderer whose bounds cover that hardpoint, with its bottom height relative to it. |
| `hps` | Every hardpoint's forward direction and angles in the aircraft frame. |
| `stores` | The pitch of each store's mesh relative to the aircraft (+ nose up). |
| `pylons [check]` | Each hardpoint: what it carries and which pylon renderers show. **check** with `check`. |
| `unload <hardpoint set>` | The game's own `RemoveMounts` on that station, which empties it. |
| `tanktest <drain kg> <cruise s> [speed m/s] [nodrop] [snap] [lift=<m>]` | At 300 m (or `lift`) and 220 m/s (or `speed`), empties that much internal fuel, cruises, then drops the PTB-1500 and logs the pylon parts' mass every physics step. `nodrop` skips the drop (a control run); `snap` resets the speed just before the drop. **coroutine** |
| `cm <station> [expected]` | A countermeasure station's ammo (`IR Flares`, `Radar Chaff`, `Radar ECM`...). **check** with `expected`. |
| `podfire <n>` | Fires every countermeasure on the wingtip pods n times. |
| `cmkeytest IR Flares\|Radar Chaff` | Drives the MiG plugin's optional flare and chaff keys as if held. **check** |
| `radarinfo` | The player's radar settings. |

### With NO Tactitools installed (`NottTest.cs`)

| Command | What it does |
| --- | --- |
| `cmpress <station> [s]` | Holds the game's own Countermeasures button on that station for s seconds (default 1.5), the way the game's player controls do every frame. **check**: rounds came out and the trigger is off afterwards. **coroutine** |
| `nottkey <n> [s]` | Holds NO Tactitools' countermeasure key n through its own handlers. With NOTT plus: its "Countermeasure Controls - n" key. With the original NOTT: 0 is its flare key and 1 its jammer key, which only select a station. **check** **coroutine** |
| `nottinfo` | Which NO Tactitools is loaded, how many errors it logged since the last `nottinfo`, and the jet's stations. |

## Damage

| Command | What it does |
| --- | --- |
| `parts` | Every part of the jet: joints intact out of total, hit points, and whether it has its own rigidbody. |
| `hurt <part text\|all> <amount>` | `ApplyDamage` on matching parts: shading and effects, no detaching. |
| `burn <part> <amount>` | Fire damage, as when a leaking fuel tank catches. |
| `partvis <part text> on\|off` | Shows or hides every renderer of the matching parts, to see the seams they leave. |
| `fire <x y z> <x y z> [damage]` | A ray from one aircraft-frame point to another. The first collider hit goes to the MiG plugin's hole system, as a round would, and its part takes the damage. |
| `blastlines <yield> <dx dy dz>` | For each part, the blast's line to its centre and to nine more points on its hull, with where each meets the part and what shields it. |
| `blastrays <yield> <dx dy dz>` | The lines a missile's blast traces to each part, as the game's fragment trace does, with every part each passes through. No damage. |
| `blasttest <yield> <dx dy dz>` | At 1,500 m and 220 m/s, a missile's blast at that aircraft-frame point. Logs every joint that breaks, frame by frame, and each part's health. **coroutine** |
| `partshots <prefix> [damage] [max parts]` | Breaks every part in turn, outermost first, with four pictures each: as it is, damaged, the break left on the jet, and the piece falling away. Hold the jet in the air first (`hold 30`). Ends with `[TEST] partshots done`. (`PartShots.cs`) **coroutine** |
| `partbreak <part>` | Breaks one part, with no pictures. |
| `hld` | Every high-lift device (slats and flaps): its aero part and each moving part's transform. A null transform means a broken entry. |

## AI aircraft and missiles (`ChaffTest.cs`)

| Command | What it does |
| --- | --- |
| `spawnac <jsonKey> <count> <distance m>` | AI aircraft of that type in a ring ahead of the player, at the same height, flying. |
| `foe <jsonKey> <distance m> [loadout]` | One hostile aircraft ahead, flying at the player. |
| `flyvec <deg from the foe> <speed m/s>` / `flyvec off` | Holds the jet on that course relative to the foe (0 = at it, 90 = beam, 180 = away). |
| `shoot <weapon key part>` | The nearest hostile fires that weapon at the player. |
| `chaffat <m>` / `chaffat off` | One chaff press once the nearest incoming radar missile is that close. |
| `mslwatch` / `mslreport` | Starts logging incoming radar missiles / prints how each ended: hit, miss or lock lost. |

## MiG-29 mod checks

These target the MiG-29 mods specifically. On other aircraft they log that there is nothing to check, or fail.

| Command | What it does |
| --- | --- |
| `packcheck` | The MiG-29 Pack's checks (`PackCheck.cs`), with a summary `[TEST] packcheck: N passed, M failed`. Encyclopedia checks run from the main menu; player-jet checks run while flying a pack jet. **check** |
| `modcheck` | The same kind of checks for the released MiG-29 Fulcrum or MiG-29SMT mod, installed without the pack (`ModCheck.cs`). **check** |
| `kcheck` | The MiG-29K airframe jets in the pack: K, KUB and MiG-35UB (`KCheck.cs`). **check** |
| `tvtest` | Selects the Kh-29T station; the TV seeker screen must be active with a picture. **check** |
| `tvpoint` | At 1,500 m, locks a bare ground spot 4 km ahead, fires the Kh-29T and logs where it lands. **check** |
| `probe` | The refuelling probe swings out to its extended pose and folds back. **check** |
| `wingfold` | The carrier jets' wings fold and spread back exactly. **check** |
| `rearseat` | The two-seaters' rear-seat view moves to the instructor's eye and back. **check** |
| `cobra [s]` | Starts the Cobra manoeuvre as its key does. |
| `expect chute\|nochute` | **check**: whether the brake chute is out. |

## NO Gauges

| Command | What it does |
| --- | --- |
| `mkgauge <name>` | Adds a small cube with that name under the cockpit, then runs NO Gauges' `AttachAll`. |
| `gauges` | Every NO Gauges element on the jet: its spec, current value and pose. |
