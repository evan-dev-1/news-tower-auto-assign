# News Tower Auto-Assign

A BepInEx 5 plugin for News Tower that automates reporter assignment.

## What it does

- **Auto-assigns reporters** to incoming stories using the path with the strongest goal overlap.
- **Chases weekly goals intelligently**:
  - *Quantity* goals: always chased, reward stacks per story.
  - *Binary* goals: chased while uncovered, deprioritised once covered by an in-progress story.
  - *Scoop* gets highest priority.
- **Discards** risky, weekend, or un-assignable stories that match no active goal.
- **Pays bribes automatically** when affordable.
- **Skips cosmetic popups** (risk spinner, new-object).

See [`NewsTowerAutoAssign/ASSIGNMENT_LOGIC.md`](NewsTowerAutoAssign/ASSIGNMENT_LOGIC.md) for the full decision model and log format.

## Install

1. Install [BepInEx 5.4.x (x64)](https://github.com/BepInEx/BepInEx/releases) into your News Tower folder.
2. Launch News Tower once so BepInEx generates `BepInEx/plugins/`.
3. Drop `NewsTowerAutoAssign.dll` into `<News Tower>/BepInEx/plugins/`.
4. Launch the game.

## License

TBD.
