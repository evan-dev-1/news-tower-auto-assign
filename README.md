# News Tower Auto-Assign

A plugin for News Tower that automates reporter assignment.

## What it does

- **Auto-assigns reporters** to incoming stories.
- **Chases weekly objectives intelligently** favoring paths that line up with your current weekly goals.
- **Discards** some stories automatically (e.g. risky, weekend, dead end chains).
- **Pays bribes and opens new objects automatically** in the background so story chains don't stall.
- **Skips cosmetic popups** where safe (e.g. risk spinner), matching manual outcomes.
- **Fills open ad slots** on the Ads tab.

See `NewsTowerAutoAssign/ASSIGNMENT_LOGIC.md` for detailed logic rules.

## How to use

1. Install [BepInEx](https://github.com/BepInEx/BepInEx/releases) into your News Tower folder (commonly at `/Steam/steamapps/common/News Tower/`)
2. Launch the game once so BepInEx creates `/News Tower/BepInEx/plugins/`.
3. Copy `NewsTowerAutoAssign.dll` into `/News Tower/BepInEx/plugins/`.
4. Launch the game again.

**Optional settings** live in `/Steam/steamapps/common/News Tower/BepInEx/config/newstower.autoassign.cfg` under the `[Dev]` section.

## License

[MIT](LICENSE)
