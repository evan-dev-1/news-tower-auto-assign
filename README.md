# News Tower Auto-Assign

A plugin for News Tower that automates reporter assignment.

## What it does

- **Auto-assigns reporters** to incoming stories.
- **Auto-fills open ad slots** on the Ads tab.
- **Chases weekly objectives intelligently** favoring paths that line up with your current weekly goals.
- **Discards** some stories automatically (e.g. risky, weekend, dead end chains).
- **Pays bribes and opens suitcases automatically** in the background so story chains don't stall.
- **Skips cosmetic popups** where safe (e.g. risk spinner), matching manual outcomes.

See `NewsTowerAutoAssign/ASSIGNMENT_LOGIC.md` for detailed logic rules.

## How to use

1. Install [BepInEx](https://github.com/BepInEx/BepInEx/releases) into your News Tower folder (commonly at `/Steam/steamapps/common/News Tower/`)
2. Launch the game once so BepInEx creates `/News Tower/BepInEx/plugins/`.
3. Copy `NewsTowerAutoAssign.dll` into `/News Tower/BepInEx/plugins/`.
4. Launch the game again.

## Optional Settings

Configurations live in `/Steam/steamapps/common/News Tower/BepInEx/config/newstower.autoassign.cfg` under the `[Dev]` section.

- **AutoAssignAds:** Automatically assign idle staff to ads on the Ads tab. Uses the same skill-matching logic as the news automation—whoever has the right skill and is free gets the work. Boycotted ads are skipped. The **MinReportersToActivate** gate does not apply to ads.
- **AutoAssignOnlyObviousPath:** Skip auto-assign for multi-path stories so you assign manually. If **ChaseGoals** is on, auto-assign only when the goal priority yields exactly one winning assignable path.
- **AutoResolveBribes:** Automatically pay bribes when affordable. Cost matches manual play. Left for manual handling if not affordable. When false, any story that has an incomplete bribe is fully manual.
- **AutoSkipRiskPopups:** Automatically dismiss risk spinner popups. Outcome is identical; the popup is cosmetic.
- **AutoSkipSuitcasePopups:** Automatically handle new-item suitcase rewards: pre-resolves unlocked suitcases so the chain never stalls waiting for the player to view the story, and auto-skips the popup if it still manages to open. Unlock side-effect is identical to manual play.
- **AvoidRisks:** Skip risky news items (Injury, Lawsuit, etc.) unless they also match a weekly goal. If **ChaseGoals** is off, risky news items are always skipped (no goal exception).
- **ChaseGoals:** Prefer story file paths whose skill matches a current weekly goal tag.
- **DiscardFreshStoriesOnWeekend:** Discard fresh (unstarted) stories that arrive on Saturday or Sunday. If **ChaseGoals** is on, fresh stories that match an uncovered weekly goal are kept even on weekends.
- **DiscardIfNoReporterForHours:** Discard a news item if no reporter with the right skill will be free within this many in-game hours (0 disables the check). Fractional values are accepted.
- **Enabled:** Automatically assign reporters to news items when they appear. The mod does nothing when set to false.
- **GlobePinOwnershipEnabled:** Globe pins are tinted green when all stories at the pin are mod-tracked, white when none, amber when mixed.
- **MinReportersToActivate:** Below this many reporters the mod will not auto-assign news stories. The default is 3 and cannot be lowered: any value below 3 is clamped back to 3. Values above 3 are accepted.

### Presets

1. **Full automation (default):** Do nothing; the plugin ships with these defaults. Delete `newstower.autoassign.cfg` if you want BepInEx to regenerate it from the .dll defaults.
2. **Partial, safer automation:** Replace your config file with [`NewsTowerAutoAssign/presets/newstower.autoassign.partial-safe.cfg`](NewsTowerAutoAssign/presets/newstower.autoassign.partial-safe.cfg).
3. **Most control, safest news:** Replace your config file with [`NewsTowerAutoAssign/presets/newstower.autoassign.control-focused.cfg`](NewsTowerAutoAssign/presets/newstower.autoassign.control-focused.cfg).

## License

[MIT](LICENSE)
