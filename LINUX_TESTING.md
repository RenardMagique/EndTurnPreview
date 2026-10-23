# Native Linux release-candidate test

This checklist is for Slay the Spire 2 running as a native Linux x64 game. Proton is outside the supported test scope.

## Prerequisites

1. Install the target game build and BaseLib 3.3.8 or newer.
2. Install the x64 .NET 9 Runtime.
3. Confirm that `dotnet --list-runtimes` lists `Microsoft.NETCore.App 9.x`.
4. Extract the universal archive so that the final directory is `Slay the Spire 2/mods/EndTurnDamagePreview`.

## Automated verification

From the installed mod directory, run:

```bash
chmod +x ./Verify-Mod.sh
./Verify-Mod.sh "PATH_TO_SLAY_THE_SPIRE_2"
```

Keep the complete output. The diagnostic must report `linux-x64`, the `data_sts2_linuxbsd_x86_64` directory, BaseLib 3.3.8 or newer, and `GameModuleSupported: true`. If the game-module identifier is different, stop: that build must be reviewed and added to the closed allowlist before forecasts are enabled.

## In-game smoke test

- Confirm that **Hover** reveals the preview only while End Turn is hovered or focused.
- Confirm that **Always** keeps the preview visible during a valid player turn.
- Confirm that **Disabled** hides the automatic preview.
- Assign one keyboard key and one mouse button in turn; verify that holding each reveals the cached preview in every mode.
- Test the player with no Block, with partially consumed Block, and with fully consumed Block.
- Test Osty surviving predicted damage and Osty receiving lethal predicted damage.
- Move the pointer away or release the shortcut and confirm that the native HP and Block bars return to their current values.
- End the real turn and compare the resulting HP and Block with the preview.
- Close the game and confirm that `pgrep -af EndTurnSimulator` reports no remaining simulator process.

## Report

Return the verification output, the game's `godot.log`, the exact game and BaseLib versions, the Linux distribution, and any mismatch observed during the smoke test. Do not qualify native Linux as fully certified until both the automated verification and this checklist pass.
