# Custom Weather

Tune how often rain, storms and mist occur in Valheim. By default each is **half as likely** as vanilla.

## Features

- Separate multipliers for **Rain**, **Storm** and **Mist** (range 0–5; 1 = vanilla, 0 = never).
- Optional toggle for whether storms in the **Ocean** biome are affected (off by default, so ocean storms stay vanilla).
- **Server-authoritative:** the server's settings are sent to every connecting client and override their local config.
- **Live updates:** edit the server's `.cfg` while running and connected clients are updated immediately.
- Lightweight, no extra dependencies beyond BepInEx (Jotunn is *not* required).

## Installation

Install with a mod manager (r2modman / Thunderstore Mod Manager), or drop `CustomWeather.dll` into `BepInEx/plugins`.

**Must be installed on both server and clients.** Clients without a matching version (same Major.Minor) are rejected, and so is a server running an incompatible version.

## Configuration

Config file: `BepInEx/config/local.CustomWeather.cfg` (generated on first run).

| Section | Setting | Default | Description |
|---|---|---|---|
| General | Enabled | true | Master switch. If false, vanilla weights are used. |
| Weights | RainMultiplier | 0.5 | Applied to every environment whose name contains "Rain". |
| Weights | StormMultiplier | 0.5 | Applied to every environment whose name contains "Storm". |
| Weights | MistMultiplier | 0.5 | Applied to the "Misty" environment. |
| Weights | StormAffectsOcean | false | If false, Ocean-biome storms keep vanilla weight. |

## Compatibility

Other mods that alter environment weights may conflict. Changing the server config takes effect live; clients disconnected from a server revert to their local config.

## Credits

Created by [Dwarf_Haven_Games](https://valheim.hexium.gg/teams/Dwarf_Haven_Games).

This mod is based on the [FreyrWeather](https://valheim.hexium.gg/mods/Dwarf_Haven_Games/FreyrWeather) mod by Dwarf_Haven_Games. The main purpose of this version is to make the weather **fully configurable** and to **remove the Jotunn dependency**.

## Changelog

See CHANGELOG.md.
