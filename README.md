# BlueprintShare-Fixed
This updated version of BlueprintShare fixes two real bugs that could cause incorrect or permanent blueprint sharing, made blueprint removal fully reliable, and tightened safety — without changing how the plugin works or what it does. 
I take no credit for it, the original Version can be found here on umod https://umod.org/plugins/blueprint-share and all credit goes to NomadWarrior (c_creep).

✅ FIX

1. Data file load/save bug (CRITICAL FIX)
❌ Problem (original plugin)
- The plugin saved data using the plugin Name
- But loaded data using a hardcoded string "BlueprintShare"
That meant:
- Data appeared to save
- But on restart, wipe, or reload:
  - The plugin could fail to load the same data
  - Clan/team/friend blueprint tracking could be lost
  - Lose Blueprints on Leave could behave incorrectly or inconsistently

✅ Fix

- Data is now saved and loaded using the exact same key
- Guarantees:
  - Shared-blueprint tracking survives restarts
  - /bs show works reliably
  - Blueprint removal on leave is accurate
📌 This fix was necessary — without it, the plugin could not be trusted long-term.

2. Clan disband blueprint removal (LOGIC FIX)
❌ Problem (original plugin)
When a clan disbanded:
- The plugin deleted its database records
- BUT did NOT remove the actual blueprints from players
So:
- Players kept clan-shared blueprints permanently
- Plugin “forgot” how they got them
- This broke the promise of Lose Blueprints on Leave

✅ Fix

When a clan disbands and Lose Blueprints on Leave = true:
- The plugin now:
  1. Identifies which blueprints were clan-shared
  2. Removes ONLY those blueprints from each player
  3. Leaves personal / earned blueprints untouched
  4. Cleans the database correctly afterward
📌 This makes clan leave and clan disband behave consistently.

⚙️ IMPROVEMENTS (Safe Optimizations)

3. Blueprint removal safety
- Removal logic now:
  - Verifies blueprint ownership source
  - Ensures blueprints from other sources (self-learned, team, friend) are not removed
- Prevents accidental blueprint loss
No gameplay change — just safer execution.

4️. Better internal consistency
- Minor logic cleanup to ensure:
  - Cached targets are invalidated correctly
  - No unnecessary work is done when no one can learn a blueprint
- No performance regression
- No feature removed

❌ What DID NOT change
To be very clear — all of this stayed the same:
- Team sharing behavior
- Clan sharing behavior
- Friend sharing behavior
- Item blueprint vs tech tree logic
- /bs toggle, /bs share, /bs show commands
- Permissions
- Messages
- Blocked items list
- Debug mode
- Blueprint learning rules
- Player progression logic

No balance changes.
No permission changes.
No behavior removed.
