# Changelog

## 0.12.1

- Added rate-limited diagnostics when a forecast cannot be verified.
- Added combat context to diagnostics: round, side, player HP and Block, and enemy model, move and HP.
- Included simulator startup and runtime error details when available.
- Prevented repeated `Local player not found in combat` messages during combat transitions.

## 0.12.0

- Added one universal package for Windows x64 and native Linux x64.
- Added cross-platform .NET host discovery through `DOTNET_HOST_PATH`, `DOTNET_ROOT`, the standard Windows installation and `PATH`.
- Added OS-aware game-data discovery for both build projects.
- Added simulator diagnostics for platform, game data, engine module, model hash and BaseLib version.
- Replaced the single engine identifier check with a closed compatibility allowlist.
- Added a native Linux verification script and kept unknown engine builds safely disabled.
- Documented the .NET 9 requirement and the current lack of official Proton support.

## 0.11.1

- Reduced the projected HP countdown animation from 650 ms to 325 ms.

## 0.11.0

- Replaced the old always-visible toggle with a Simulation dropdown: Hover, Always or Disabled.
- Added a configurable keyboard-key or mouse-button shortcut that reveals the cached preview while held.
- Kept hidden background recalculation active in every display mode for immediate presentation.
- Added automatic migration from the previous always-visible preference.

## 0.10.0

- Added the native end-turn HP and Block preview to player-owned pets, including Osty.
- Preserved projected results for pets that die and are removed from combat during the simulated turn.
- Added regression tests for both surviving and lethal end-turn damage on Osty.

## 0.9.0

- Kept the native HP-loss trail, projected HP animation and projected Block display.
- Made all source comments, build messages and distributed documentation English-only.
- Removed machine-specific and user-specific information from distributable artifacts.
- Replaced remaining health-bar geometry constants with measurements from the live native controls.
- Added Nexus Mods-ready installation, requirements, compatibility, configuration and uninstallation documentation.
- Added a distributable license and a clean release layout.

## 0.8.2

- Replaced the custom threatened-HP overlay with the game's native HP foreground and middleground layers.
