# End Turn Preview v0.15.0

Author: Renard Magique

End Turn Preview simulates the rest of the current turn and displays the result directly on the native health bars of the player and their pets. It requires Slay the Spire 2 v0.107.1 or newer with BaseLib 3.3.8. Windows x64 is tested; native Linux x64 remains experimental.

## Features

- Calculates the forecast on demand by default.
- Can defer calculation until the preview is requested by the selected display mode or shortcut.
- Displays the cached result immediately when the End Turn button is hovered.
- Offers hover, always-visible and disabled automatic-display modes under **Settings > Mod Configuration > EndTurnDamagePreview**.
- Supports a configurable keyboard key or mouse button that reveals the cached preview while held.
- Animates the native HP number from current HP to projected HP.
- Adds a subtle end-turn-blue aura behind the player's health bar while the forecast is visible.
- Uses the game's native red foreground and orange damage-trail layers. No custom rectangle is placed over the health bar.
- Replaces the native Block number with projected Block and uses the native block-break effect when all Block will be lost.
- Applies the same projected HP and Block display to player-owned pets, including Osty, and previews lethal damage even when the pet will be removed from combat.
- Runs the game engine's end-turn and enemy-action routines in an isolated worker process instead of estimating damage from intent icons.
- Refuses to display a numeric forecast when the state cannot be verified safely.
- Accepts newer game builds when the live game and worker use matching engine and model identities.
- Loads additional mod assemblies and initializes mods that declare gameplay changes in the isolated worker instead of rejecting every mod combination.

The forecast ends at the beginning of the next playable player turn, after start-of-turn effects, or when combat ends. Healing can offset damage, and normal Block expiration is included.

## Resolution independence

The mod does not use screen coordinates or assume a specific viewport size. It modifies controls already owned and positioned by each creature's `NHealthBar`. HP widths are obtained through the health bar's native width calculation, and the native middleground offsets are captured from the live control. The result therefore follows the game's UI scaling, aspect-ratio handling and health-bar layout.

## Requirements

- Slay the Spire 2 v0.107.1 or newer
- BaseLib 3.3.8 or newer
- Windows x64
- Native Linux x64 is experimental and not yet certified
- .NET 9 Runtime

The .NET host must be available through `DOTNET_HOST_PATH`, `DOTNET_ROOT`, the standard Windows installation, or `PATH`. Proton is not an officially supported configuration.

## Installation

1. Install and enable BaseLib.
2. Extract the `EndTurnDamagePreview` folder into the game's `mods` directory.
3. Keep the included `simulator` subfolder inside the mod folder.
4. Restart the game.

On Linux, the usual native Steam location is `~/.local/share/Steam/steamapps/common/Slay the Spire 2`. Install the x64 .NET 9 Runtime for your distribution and confirm that `dotnet --list-runtimes` lists `Microsoft.NETCore.App 9.x`.

The final layout should be:

```text
Slay the Spire 2/
└── mods/
    └── EndTurnDamagePreview/
        ├── EndTurnDamagePreview.dll
        ├── EndTurnDamagePreview.json
        ├── README.md
        ├── CHANGELOG.md
        ├── LICENSE.txt
        ├── LINUX_TESTING.md
        ├── Verify-Mod.ps1
        ├── Verify-Mod.sh
        └── simulator/
            ├── EndTurnSimulator.dll
            ├── EndTurnSimulator.deps.json
            └── EndTurnSimulator.runtimeconfig.json
```

## Configuration

Open **Settings > Mod Configuration > EndTurnDamagePreview**.

- **Simulation — Hover**: the preview appears while End Turn is hovered or focused. This is the default.
- **Simulation — Always**: the preview remains visible throughout a valid player turn.
- **Simulation — Disabled**: automatic display is disabled.
- **Continuous background calculation**: disabled by default. When disabled, a new forecast is calculated only while the preview is requested by Hover, Always or the configured shortcut. Enable it to recalculate after every stable combat-state change.
- **Simulation shortcut**: select the button, then press a keyboard key or mouse button. Hold that input during combat to reveal the preview immediately. Press Backspace or Delete while assigning to clear it, or Escape to cancel assignment.

The shortcut can reveal the preview in any of the three modes, including **Disabled**. With continuous calculation enabled, display requests reveal the latest cached result. With it disabled, the request starts any calculation needed for the current state.

## Safety and compatibility

The simulator receives immutable replay bytes and runs with the game's in-memory test save backend. It does not load, replace or write normal save files. Game libraries are loaded from the local installation and are not redistributed.

Multiplayer remains unsupported because the current result protocol represents one player. Additional mods are no longer rejected in advance: their managed assemblies are loaded in the isolated worker, and mods declaring gameplay changes are initialized there on a best-effort basis. A mod that depends on unavailable presentation or native Godot services may still make its forecast unavailable, but it will not disable End Turn Preview merely by being installed.

There is no closed game-version allowlist. The worker instead requires its engine module, model hash and loaded mod identities to match the live game for every request. The forecast is also withheld while actions are still resolving or when the simulation requires a future player choice.

At startup and whenever the loaded mod set changes, the mod logs a compatibility line containing its version, runtime platform, game assembly version, engine module identifier, model hash and loaded mod identities. When a forecast cannot be verified, it writes one rate-limited diagnostic for each distinct combat state and reason. The line also includes the round, side, player HP and Block, and enemy models, moves and HP. Worker startup failures, missing assemblies, module mismatches and third-party initializer exceptions include their available error details.

To report a missing or incorrect forecast, reproduce the problem and close the game without starting it again. Attach the complete `godot.log`, the mod version, game version, BaseLib version, enabled-mod list and a short description to a Nexus bug report or a [GitHub issue](https://github.com/RenardMagique/EndTurnPreview/issues). Useful entries contain `EndTurnDamagePreview: forecast unavailable`.

- Windows: `%APPDATA%\SlayTheSpire2\logs\godot.log`
- Native Linux: `~/.local/share/SlayTheSpire2/logs/godot.log`

The complete file is more useful than a single copied line because nearby messages provide startup and combat context. Logs may contain local paths or system information; review the file before sharing it publicly.

## Uninstallation

Delete `mods/EndTurnDamagePreview`, then restart the game. Save files are not modified by this mod.

## Development

Both projects use `STS2_GAME_DIR`, or an explicit `-p:GameData=PATH_TO_GAME_DATA` build property. No personal or machine-specific path is stored in the source or release binaries. Run the verification suite from the repository root:

```powershell
./Verify-Mod.ps1 -GameDirectory "PATH_TO_SLAY_THE_SPIRE_2"
```

On native Linux, the packaged verifier requires only the runtime. Running it from the source tree also builds the projects and therefore requires the .NET 9 SDK:

```bash
chmod +x ./Verify-Mod.sh
./Verify-Mod.sh "PATH_TO_SLAY_THE_SPIRE_2"
```

The verifier prints a diagnostic containing the runtime platform, resolved game-data path, game-module identifier, model hash, compatibility policy, loaded mods and detected BaseLib assembly version. A different Linux module is accepted when the live game and worker use that same module and the verification suite succeeds.

See `LINUX_TESTING.md` for the complete native Linux release-candidate checklist and the files to return with a test report.

The source inspection used to identify the native health-bar behavior was performed with ILSpy. No game binaries, save data, reverse-engineering output, build intermediates or debug symbols are included in the release archive.
