# End Turn Preview

See what the enemy turn will do before you commit.

End Turn Preview simulates the rest of the current turn with Slay the Spire 2's own combat logic. It then previews the verified result directly on the player's native HP and Block displays. It also supports player-owned pets, including Osty.

## At a glance

- Projected HP and Block appear on the native health bars
- End-of-turn effects, enemy actions, poison, healing and Block expiration are included
- The HP number animates down to its projected value
- A subtle blue aura makes it clear when a forecast is being displayed
- Display the forecast by hovering End Turn, keeping it always visible, or holding a custom keyboard or mouse shortcut
- Optional continuous background calculation, disabled by default
- No save-file changes
- Unsafe or unverifiable results are hidden instead of showing a misleading number

## Installation

1. Install and enable **BaseLib 3.3.8 or newer**.
2. Install the **.NET 9 Runtime**. The SDK is not required.
3. Extract the archive into the game's `mods` folder.
4. Confirm that the resulting folder is `Slay the Spire 2/mods/EndTurnDamagePreview/`.
5. Keep the included `simulator` subfolder in place, then restart the game.

Do not place a second `EndTurnDamagePreview` folder inside the first one.

## Configuration

Open **Settings > Mod Configuration > EndTurnDamagePreview**.

- **Hover** — shows the forecast while the End Turn button is hovered or focused. This is the default.
- **Always** — keeps the forecast visible during a valid player turn.
- **Disabled** — disables automatic display. The configured shortcut can still reveal it.
- **Continuous background calculation** — recalculates after every stable combat-state change. It is disabled by default.
- **Simulation shortcut** — assign a keyboard key or mouse button, then hold it during combat to show the forecast.

## What is simulated?

The forecast runs until the next playable player turn or until combat ends. It includes normal end-of-turn and start-of-turn effects, enemy actions, Block expiration, poison, healing and lethal outcomes.

This is not an estimate based on intent icons. The calculation runs in an isolated worker using the game's own combat routines. A result is displayed only when the simulated starting state and result can be verified safely.

## Requirements and compatibility

- Slay the Spire 2 **v0.107.1**
- BaseLib **3.3.8+**
- **.NET 9 Runtime** (`Microsoft.NETCore.App 9.x`)
- Windows x64, fully tested
- Native Linux x64, experimental

The same archive is used on Windows and native Linux. Proton is not officially supported. Unknown game builds, multiplayer and combinations with additional gameplay mods are deliberately rejected until they can be verified safely.

Some situations require a future player choice. In those cases, showing no forecast is intentional because there is no single reliable outcome to display.

## Something went wrong? Your log can help

If the preview is missing or incorrect, please send me the `godot.log` produced during that combat. End Turn Preview records a rate-limited diagnostic when a forecast cannot be completed or verified.

1. Reproduce the problem.
2. Close the game without starting it again, because a later session can replace the current log.
3. Attach `godot.log` to a Nexus bug report or a [GitHub issue](https://github.com/RenardMagique/EndTurnPreview/issues).
4. Include the mod version, game version, BaseLib version, the other enabled mods and a short description of what happened.

Log locations:

- **Windows:** `%APPDATA%\SlayTheSpire2\logs\godot.log`
- **Native Linux:** `~/.local/share/SlayTheSpire2/logs/godot.log`

Useful entries contain `EndTurnDamagePreview: forecast unavailable`. The complete log is preferable because nearby lines provide the context needed to diagnose worker startup and combat-state problems. Logs may contain local file paths or system information, so you may review the file before posting it publicly.

## Uninstallation

Delete `Slay the Spire 2/mods/EndTurnDamagePreview/`, then restart the game. The mod does not modify save files.

## Credits

Created by **Renard Magique**. Built with BaseLib and informed by the Slay the Spire 2 modding community.
