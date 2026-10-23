# End Turn Preview

See the consequences before ending your turn.

End Turn Preview runs the game's own end-turn and enemy-action logic in an isolated worker, then shows the verified result directly on the native health bars of the player and their pets. The red bar moves to projected HP, the native orange damage trail shows threatened HP, the HP number animates to its projected value, and Block displays its projected remainder.

## Main features

- Near-instant forecasts after the initial background warm-up
- Hidden recalculation after every stable combat-state change
- Hover-only display by default
- Hover, always-visible and disabled automatic-display modes
- Configurable press-and-hold keyboard or mouse shortcut
- Native health-bar visuals that follow UI scaling and screen resolution
- Player-pet support, including Osty survival and death previews
- End-of-turn effects, enemy actions, Block expiration, healing, poison and combat-ending effects are simulated
- No save-file modifications
- Safe refusal instead of an unverified number
- Rate-limited `godot.log` diagnostics when a forecast cannot be verified

## Requirements

- Slay the Spire 2 v0.107.1
- BaseLib 3.3.8+
- Windows x64 or native Linux x64
- .NET 9 Runtime

The .NET host must be available through `DOTNET_HOST_PATH`, `DOTNET_ROOT`, the standard Windows installation, or `PATH`. Proton is not officially supported.

## Installation

Extract the `EndTurnDamagePreview` folder into `Slay the Spire 2/mods/`, enable BaseLib, and restart the game. Do not move or remove the included `simulator` subfolder.

Linux users must use the native Linux build of the game and have `Microsoft.NETCore.App 9.x` installed. The same mod archive is used on Windows and Linux.

## Configuration

Open **Settings > Mod Configuration > EndTurnDamagePreview**. Choose **Hover**, **Always** or **Disabled** under **Simulation**. Select **Simulation shortcut**, then press a keyboard key or mouse button; hold that input during combat to reveal the cached preview in any mode.

## Compatibility

This release supports the game build listed above and intentionally disables forecasts on unknown builds. The Windows build is fully regression-tested; native Linux support uses the same managed assemblies and should be treated as a release candidate until the Linux verification script and in-game checklist have passed on a native installation. Multiplayer and additional gameplay mods are not currently supported because their state cannot yet be verified safely.

## Uninstallation

Delete the `mods/EndTurnDamagePreview` folder. The mod does not alter save files.
