# Changelog

## 0.1.0 (2026-11-19)

The driver moves out of the MiG-29 Pack's `tests/ingame` folder into its own repository, with its scenarios, the
`run.sh` runner and full documentation.

- New commands since the last copy in the pack: `cmpress`, `nottkey` and `nottinfo` (countermeasures with NO Tactitools
  installed), `hld` (slat and flap moving parts), and `tanktest` options `speed`, `nodrop`, `snap` and `lift=`.
- `tanktest`'s cruise hold scales its gains with speed, so it stays stable up to 420 m/s.
- `run.sh` builds from `src/NODevDriver` and reads scenarios from `scenarios/`.
