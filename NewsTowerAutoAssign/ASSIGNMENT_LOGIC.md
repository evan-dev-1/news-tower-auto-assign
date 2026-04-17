# Auto-Assign Logic Rules

## Quick reference

### A story is kept and worked on if:

- It has no risks, or
- It's risky but helps one of your active weekly goals that isn't already covered by another story you're working on, or
- You've already started on it, or
- It matches a weekly goal - the mod will wait as long as needed for the right reporter, even many in-game hours.

### A story is thrown away (discarded) if:

- It's risky and doesn't match any current weekly goal you still care about, or
- It needs a skill or building you don't/can't have, or
- It just showed up on Saturday or Sunday, you haven't started it, and it doesn't match an open goal, or
- Everyone who could do it is busy for longer than your "discard if nobody's free" threshold (default: 4h), the story doesn't match a goal, and you haven't invested yet.

### A bribe gets paid automatically if:

- The setting is on (default: yes), and
- You can afford it.
- If you can't afford it, the mod leaves the bribe for you.

### How weekly goals change behavior

- A story that matches a goal is preferred over one that doesn't when everything else is equal.
- Scoop goals get the strongest tie-break.
- Goals never cause a safe story to be skipped; they only break ties between options.

### Ads

Toggle: `AutoAssignAds` in `newstower.autoassign.cfg` (`[Dev]`). When on, open slots on the Ads board are filled with the best available staff who are eligible: correct skill, building unlocked, not busy, and the ad isn't under a boycott. If nobody is free, the ad stays for the next scan - there is no "discard" logic for ads.

---
