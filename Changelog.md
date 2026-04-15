# Blueprint Share - Full Changelog

## v1.4.5 (Latest)

### New

-   Added debounced/batched data saving system to reduce disk I/O under
    heavy player activity.

### Improvements

-   Server save now forces immediate data flush.
-   Server shutdown now forces immediate data flush and clears pending
    save timers.

### Previous Improvements Included

-   Updated Tech Tree hook for compatibility:
    -   OnTechTreeNodeUnlocked(Workbench, ItemDefinition, BasePlayer)
-   Preserved correct clan disband behavior (properly removes shared
    blueprints from players).
-   Added share-target cache invalidation on:
    -   Team leave / kick
    -   Friend add / remove
    -   Clan join / leave
    -   Player toggle
-   Added null-safety for blueprint additional unlock handling.
-   Optimized blueprint-learning checks with early return logic.
-   Preserved robust mutual-friend removal logic.

------------------------------------------------------------------------

## v1.4.4 (Nomad Warrior)

### Changes

-   Updated Tech Tree hook signature for compatibility with newer Rust
    versions.
-   Simplified clan disband handling (NOTE: introduced regression where
    player blueprints may not be removed correctly).

------------------------------------------------------------------------

## v1.4.3 (c_creep - Base Version)

### Features

-   Blueprint sharing via:
    -   Teams
    -   Friends
    -   Clans
-   Supports:
    -   Item research sharing
    -   Tech tree sharing
-   Optional:
    -   Share to existing members
    -   Share to new members
    -   Lose blueprints on leave
-   Chat commands:
    -   /bs toggle
    -   /bs share
    -   /bs show
-   Permission system for usage, sharing, toggling, and bypass.
-   Blueprint tracking and removal system for social relationships.
-   Data persistence via Oxide data files.
-   Basic caching system for share targets.

### Notes

-   Stable core functionality.
-   Clan disband behavior correctly removes blueprints.
-   Cache relied solely on time-based expiration (no event
    invalidation).

------------------------------------------------------------------------

## Summary of Evolution

v1.4.3 → v1.4.4 - Compatibility update (Tech Tree hook) - Regression
introduced in clan disband logic

v1.4.4 → v1.4.5 - Restored correct clan behavior - Added cache
invalidation safety - Improved stability and performance - Added
debounced saving system

------------------------------------------------------------------------

Generated on: 2026-04-15T04:00:43.961288 UTC
