# Changelog

## [1.0.1] - 2026-10-21

### Fixed
- Edits to the `.cfg` file are now applied live, and the current weather is re-rolled immediately so the change is visible without waiting for the next weather period. The plugin checks its config file twice a second and reloads it shortly after it is saved. Previously only changes made through an in-game config manager were picked up.
- Servers no longer keep handshake state for players who have left. Peers are now forgotten as soon as they disconnect, instead of only when the whole network session is destroyed.

### Changed
- A config reload that changes several settings is now applied and sent to clients once, instead of once per changed setting.
- Version number set to 1.0.1. The handshake compares Major.Minor, so server and clients must all run 1.0.x.
- Weather weight calculation moved into a single `Multiplier` method. Rain, storm and mist behavior is unchanged.
- Version rejection logic (server error / client disconnect) consolidated into one `Reject` helper.
- Settings serialization moved into `WeatherSettings` (`Write` / `Read`), keeping the network format next to the fields it describes.
- The local plugin version is parsed once instead of on every handshake.
- Config pushes to clients now build a fresh package per peer.

### Removed
- Unused `using System;` and a redundant null check when resetting synced settings on shutdown.
