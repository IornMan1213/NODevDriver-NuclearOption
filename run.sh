#!/usr/bin/env bash
# NO Dev Driver test runner. Feeds a scenario (scenarios/*.txt, one driver command per line) to Nuclear Option and prints its
# "[TEST]" lines; exits 1 if any check failed or the scenario did not finish.
#   ./run.sh scenarios/fly_mig29.txt [timeout s]
# Builds the driver (src/NODevDriver) if its source changed, installs it into BepInEx/plugins/NODevDriver, launches the game
# through Steam and waits for the game to finish loading. It will not drive a game it did not start (ATTACH=1 to do so).
# Scenarios end with "say END" (and "quit" to close the game; or QUIT=1 ./run.sh ...). "include <file> [args]" pulls in a
# shared part (spawn.txt, flight.txt). See docs/SCENARIOS.md.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
scenario="${1:?scenario file}"; timeout="${2:-900}"
game="${GameDir:-$HOME/.steam/steam/steamapps/common/Nuclear Option}"
bep="$game/BepInEx"; log="$bep/LogOutput.log"
# Unity's own log has every line; BepInEx's LogOutput.log can miss its last lines when the game quits, so results come from
# whichever of the two holds this run's end marker (Proton's prefix on Linux; set PLAYER_LOG elsewhere)
plog="${PLAYER_LOG:-$HOME/.steam/steam/steamapps/compatdata/2168680/pfx/drive_c/users/steamuser/AppData/LocalLow/Shockfront/NuclearOption/Player.log}"

dll="$here/src/NODevDriver/bin/Release/NODevDriver.dll"
if [ ! -f "$dll" ] || [ -n "$(find "$here/src/NODevDriver" -name '*.cs' -newer "$dll")" ]; then
    (cd "$here/src/NODevDriver" && dotnet build -c Release -nologo -v q "-p:GameDir=$game" >/dev/null)
fi
mkdir -p "$bep/plugins/NODevDriver"
if ! cmp -s "$dll" "$bep/plugins/NODevDriver/NODevDriver.dll"; then
    pgrep -x "NuclearOption.e" >/dev/null && echo "note: the driver changed; restart the game to load it" >&2
    cp "$dll" "$bep/plugins/NODevDriver/"
fi

# A game that is already running may be someone else's (another session's tests, or you playing): it is only driven with ATTACH=1.
if pgrep -x "NuclearOption.e" >/dev/null && [ "${ATTACH:-0}" != 1 ]; then
    echo "Nuclear Option is already running and this run did not start it: not driving it. Close it first, or ATTACH=1 to drive it." >&2
    exit 2
fi
if ! pgrep -x "NuclearOption.e" >/dev/null; then
    rm -f "$bep/mig29pack_cmd.txt"   # a stale scenario would start before the menu exists
    steam -applaunch 2168680 >/dev/null 2>&1 &
    echo "launching Nuclear Option..."
    for _ in $(seq 60); do pgrep -x "NuclearOption.e" >/dev/null && break; sleep 2; done
fi
# BepInEx starts a new log when the game starts: wait for this run's Blueprinter to finish loading
t0=$(date -d "$(ps -o lstart= -p "$(pgrep -x "NuclearOption.e" | head -1)")" +%s)
if ! { [ -f "$log" ] && [ "$(stat -c %Y "$log")" -ge "$t0" ] && grep -q "\[Plugin\] Done in" "$log"; }; then
    echo "waiting for the game to load..."
    until [ -f "$log" ] && [ "$(stat -c %Y "$log")" -ge "$t0" ] && grep -q "\[Plugin\] Done in" "$log"; do
        sleep 5; [ $(( $(date +%s) - t0 )) -gt 600 ] && { echo "the game did not finish loading in 10 min" >&2; exit 1; }
    done
    sleep 10   # the main menu builds after Blueprinter is done
fi

grep -aq "Loading \[NO Dev Driver" "$log" || { echo "the dev driver is not loaded in this game session (BepInEx skipped it, or it was installed after the game started): restart the game" >&2; exit 1; }

# "include <file> [args]" lines pull in another scenario (relative to this one), its $1, $2... replaced by the args
expand() {
    local dir line i; local -a args inc_args; dir="$(dirname "$1")"; args=("${@:2}")
    while IFS= read -r line || [ -n "$line" ]; do
        for i in "${!args[@]}"; do line="${line//\$$((i + 1))/${args[$i]}}"; done   # first, so a nested include gets them too
        if [[ "$line" =~ ^include[[:space:]]+([^[:space:]]+)(.*)$ ]]; then
            read -r -a inc_args <<< "${BASH_REMATCH[2]}"
            expand "$dir/${BASH_REMATCH[1]}" "${inc_args[@]}"
        else
            printf '%s\n' "$line"
        fi
    done < "$1"
}
# the run is fenced by its own markers, so a scenario still running from before (or its log lines) cannot be mistaken for this one
id="run$(date +%s%N)"
body="$(expand "$scenario")"
# a scenario's own "quit" moves after the end marker, so the marker is always logged before the game closes
wantquit="${QUIT:-0}"; printf '%s\n' "$body" | grep -qx 'quit' && wantquit=1
cmds="say RUN $id
$(printf '%s\n' "$body" | grep -vx 'quit')
release
say DONE $id"
[ "$wantquit" = 1 ] && cmds="$cmds
quit"

[ -f "$bep/mig29pack_cmd.txt" ] && { echo "a scenario is still waiting to start: waiting for it" >&2; while [ -f "$bep/mig29pack_cmd.txt" ]; do sleep 2; done; }
printf '%s\n' "$cmds" > "$bep/mig29pack_cmd.txt"
echo "running $(basename "$scenario") ($(printf '%s\n' "$cmds" | grep -cv '^\s*$') commands)..."
t0=$(date +%s)
mine() {
    local f
    for f in "$log" "$plog"; do
        [ -f "$f" ] && grep -aq "\[TEST\] DONE $id" "$f" && { sed -n "/\[TEST\] RUN $id/,\$p" "$f"; return; }
    done
    sed -n "/\[TEST\] RUN $id/,\$p" "$log"
}
until mine | grep -q "\[TEST\] DONE $id"; do
    sleep 3
    if [ $(( $(date +%s) - t0 )) -gt "$timeout" ]; then echo "timed out after ${timeout} s" >&2; break; fi
    if ! pgrep -x "NuclearOption.e" >/dev/null; then sleep 3; mine | grep -q "\[TEST\] DONE $id" || echo "the game closed" >&2; break; fi
done
results=$(mine | grep -a "\[TEST\]\|Exception" | grep -v "\[TEST\] \(RUN\|DONE\) $id" | sed 's/^\[[^]]*\]\s*//')
echo "$results"
fails=$(echo "$results" | grep -c "\[TEST\] FAIL" || true)
ended=$(echo "$results" | grep -c "\[TEST\] END" || true)
echo "---"; echo "failed checks: $fails"
[ "$fails" -eq 0 ] && [ "$ended" -gt 0 ]
