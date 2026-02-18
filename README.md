# CommandChatLogger

**CommandChatLogger** is a lightweight Rust uMod/Oxide plugin that logs
player chat `/commands` to a structured JSON data file.

It supports flexible logging modes, command exclusions, and automatic
wipe cleanup --- making it ideal for staff auditing or player
monitoring.

------------------------------------------------------------------------

## Features

-   Logs chat messages that start with `/`
-   Flexible logging modes:
    -   `PlayersOnly` -- log only regular players
    -   `AdminsOnly` -- log only staff (OwnerID / ModeratorID)
    -   `Everyone` -- log all players
-   Ignore specific commands to prevent log clutter
-   Optional automatic log wipe on server wipe (`OnNewSave`)
-   Structured JSON data storage
-   Low performance impact
-   Safe and production-ready

------------------------------------------------------------------------

## Requirements

-   Rust dedicated server
-   uMod / Oxide installed
-   C# plugin support (standard with Oxide)

------------------------------------------------------------------------

## Installation

1.  Download `CommandChatLogger.cs`

2.  Place it inside:

    oxide/plugins/

3.  Reload the plugin:

    oxide.reload CommandChatLogger

4.  Configuration file will be generated at:

    oxide/config/CommandChatLogger.json

5.  Log data will be stored at:

    oxide/data/CommandChatLogger.json

------------------------------------------------------------------------

## Configuration

After first load:

``` json
{
  "Plugin Enabled": true,
  "Log Mode (PlayersOnly/AdminsOnly/Everyone)": "PlayersOnly",
  "Wipe Data File On Server Wipe": true,
  "Ignored Commands (without /)": [
    "mymini",
    "nomini",
    "fmini"
  ]
}
```

### Plugin Enabled

-   `true` → plugin active\
-   `false` → plugin disabled

------------------------------------------------------------------------

### Log Mode (PlayersOnly/AdminsOnly/Everyone)

Controls who gets logged.

-   `PlayersOnly` -- only normal players\
-   `AdminsOnly` -- only staff (OwnerID / ModeratorID)\
-   `Everyone` -- both players and staff

Staff detection uses Rust's native admin system (`authLevel >= 1`).

------------------------------------------------------------------------

### Wipe Data File On Server Wipe

-   `true` → clears log file automatically on map wipe\
-   `false` → keeps log history across wipes

Triggered via Rust's `OnNewSave()` hook.

------------------------------------------------------------------------

### Ignored Commands (without /)

List of command names that should not be logged.

Do not include `/`.

Example:

``` json
"Ignored Commands (without /)": [
  "mymini",
  "nomini",
  "fmini",
  "kit",
  "skin"
]
```

Matching is case-insensitive and based only on the command name before
the first space.

------------------------------------------------------------------------

## Data Format

Logs are stored in:

    oxide/data/CommandChatLogger.json

Each entry:

``` json
{
  "Timestamp": "2026-02-18 18:42:31 UTC",
  "PlayerName": "SomePlayer",
  "SteamId": 7656119XXXXXXXXXX,
  "Command": "vanish",
  "FullMessage": "/vanish"
}
```

Fields:

-   `Timestamp` -- UTC time of usage\
-   `PlayerName` -- Display name\
-   `SteamId` -- Steam64 ID\
-   `Command` -- Command name (without `/`)\
-   `FullMessage` -- Entire chat message

------------------------------------------------------------------------

## Performance

-   Hooks into `OnPlayerChat`
-   Only processes messages starting with `/`
-   Minimal memory usage
-   Optional wipe prevents unbounded file growth

Designed to have negligible performance impact on production servers.

------------------------------------------------------------------------

## Version

Current Version: **1.3.0**

------------------------------------------------------------------------

## License

MIT License

------------------------------------------------------------------------

## Author

SeesAll
