# Changelog

## 0.18.3-ts (talentshards branch)

- Fixed the simulator crashing against the Kaiser Crab boss (Crusher/Rocket).
- Fixed a "Combat state mismatch" with Tuning Fork: relic activation animations are no longer compared.

## 0.18.2-ts (talentshards branch)

- Card choices during the forecast (e.g. the Knowledge Demon's curse) are simulated option by option and shown as a range instead of blocking it.
- Fixed a "Combat state mismatch" when the forecast was requested while a turn was still starting.

## 0.18.1-ts (talentshards branch)

- Fixed the simulator failing to start on the stable game after a public-beta build: one build now runs on both.

## 0.18.0-ts (talentshards branch)

- Coop support: every player's health bar shows its forecast; the turn is simulated as if all players ended it now.
- Public beta: fixed the simulator crashing when a card is drawn (new debug sound effects need Godot).

## 0.17.9-ts (talentshards branch)

- A player choice at the start of the next turn (e.g. an optional exhaust) no longer blocks the forecast: it stops at that choice.

## 0.17.8-ts (talentshards branch)

- Fixed "Combat state mismatch" against The Insatiable: Sandpit animation fields are no longer compared.

## 0.17.7-ts (talentshards branch)

- Fixed the Shard Pouch being replaced by a deprecated relic in the simulator: it now applies BaseLib's mod ID prefix (e.g. RELIC.TALENTSHARDS-SHARD_POUCH).

## 0.17.6-ts (talentshards branch)

- Fixed the simulator misreading Talent Shards runs: with a gameplay mod, BaseLib adds extended save data to every card, relic and player. The simulator now applies the same BaseLib save patches.

## 0.17.5-ts (talentshards branch)

- The simulator now uses all of the live net-ID tables (categories, entries, epochs, saved properties): with Talent Shards the live game adds entries after startup.

## 0.17.4-ts (talentshards branch)

- The mod sends the live saved-property ID table to the simulator, which uses it instead of its own. With Talent Shards the two tables differed, so the Shard Pouch failed to load in the simulator.

## 0.17.3-ts (talentshards branch)

- Fixed forecasts staying "pending": on game v0.111.1 each simulated combat waited 10 s for the previous one's turn loop. The simulator no longer waits.

## 0.17.2-ts (talentshards branch)

- Forecasts work with Talent Shards: the model hash check is skipped when a gameplay mod is loaded (mod assemblies and the replayed combat state are still verified).

## 0.17.2

- Fixed the preview worker crashing at startup when another mod (e.g. Talent Shards) prints through Godot in its initializer: the worker now ignores Godot print calls.

## 0.17.1

- Fixed the live preview worker crashing at startup on game v0.111.1: it inherited the game's Sentry library path and tried to load Sentry's native library without the engine.

## 0.17.0

- Updated for game v0.111.1 (requires BaseLib 3.4.8+):
  - the simulator follows the new combat turn loop when it plays the end of the turn;
  - mod assemblies, the 64-bit run seed and the new RNG save format are handled;
  - the simulator stubs the new native-only startup paths (save migrations, command line, platform info, hover tips);
  - BaseLib's companion assembly is resolved from its mod folder;
  - the event-combat snapshot hook no longer depends on a renamed parameter.
- The choice test now uses Tools of the Trade (Well-Laid Plans no longer asks for a choice).

## 0.16.6

- Fixed forecasts for combats started from an event option (e.g. Dense Vegetation): the mod now snapshots the run when the event starts the fight, and the simulator enters that encounter directly instead of replaying the event.
- Added an event-combat replay regression test.

## 0.16.5

- Fixed the simulator crashing on combats started from an event (e.g. Dense Vegetation): event option text is no longer resolved in the worker.
- Requests that crash the simulator are now saved for offline replay as well.

## 0.16.4

- Fixed forecasts being rejected against the Waterfall Giant: the simulator now advances its pressure counter like the live game, without the animation side effects.
- Combat state mismatches are now saved for offline replay as well.

## 0.16.3

- Fixed forecasts failing for the rest of a combat after Neutralize (and other cards reading player preferences) was played: the simulator now initializes default preferences.

## 0.16.2

- Corrected the version string reported in the game log.
- Saved the last failing simulation request and full engine error to %TEMP%/EndTurnDamagePreview for offline replay (`--replay-file=`).

## 0.16.1

- Fixed a simulator crash (0xC0000005) when a game action failed: the worker no longer touches the game's Sentry reporter, which requires the native Godot engine.
- Simulation errors now include the first engine error line in the diagnostic log.

## 0.16.0

- Prevented forecasts from revealing one seeded outcome for damage-relevant hidden combat randomness.
- Added isolated alternate RNG branches for random combat targets and other relevant combat rolls.
- Displayed the observed best-to-worst HP interval as a purple segment using the game's native health-bar geometry.
- Changed the player health-bar aura from blue to purple whenever the visible forecast contains random variance.
- Added range-aware HP and Block labels for uncertain outcomes.
- Added hidden-randomness sources, branch counts, powers and hand contents to diagnostic logging.
- Added a regression test for safe random-range aggregation.

## 0.15.0

- Removed the closed game-build allowlist; newer builds are accepted when the live game and isolated worker report matching engine and model identities.
- Removed the blanket rejection of additional mods.
- Added best-effort loading of third-party assemblies and initialization of mods that declare gameplay changes inside the isolated simulator.
- Kept multiplayer disabled because the current result protocol represents one player only.
- Added rate-limited compatibility diagnostics containing runtime, game assembly, module, model hash and loaded mod identities.
- Added explicit worker errors for missing mod assemblies, module mismatches and failing third-party initializers.
- Added regression coverage for an additional mod and mismatched live/worker engine identities.

## 0.14.2

- Fixed an intermittent missing preview when the End Turn button acquired focus before the combat binding completed.
- Synchronized the native button-focus state on every preview tick, so leaving and re-entering the button is no longer required.
- Added diagnostics for simulations that were never started, faulted, canceled or remained pending for more than one second.

## 0.14.1

- Fixed the preview aura being rendered behind the combat scene.
- Moved the aura into the health bar's local drawing order and kept it behind the native bar elements.
- Slightly increased the aura brightness while retaining a subtle appearance.

## 0.14.0

- Added a subtle blue aura behind the player's health bar while the forecast is visible.
- Attached the aura to the native health-bar container so it follows resolution, UI scale and layout changes.
- Kept the aura hidden whenever the simulation is hidden, unavailable or unverified.

## 0.13.2

- Changed Continuous background calculation to be disabled by default.
- Existing saved preferences remain unchanged.

## 0.13.1

- Fixed false combat-state mismatches while Happy Flower's one-second activation animation is running.
- Kept Happy Flower's saved turn counter in the strict state verification.
- Added a regression test that distinguishes the visual activation flag from the gameplay counter.

## 0.13.0

- Added a Continuous background calculation checkbox, enabled by default.
- When disabled, forecasts are calculated only while requested by Hover, Always or the configured shortcut.
- Kept the display mode and calculation policy independent so Disabled can still be used with a hold shortcut.

## 0.12.2

- Prevented the isolated simulator from calling Decimillipede's presentation-only native texture loader.
- Added automatic simulator restart and one retry after an unexpected worker exit.
- Replaced the misleading generic .NET error with the actual captured worker failure.
- Added section fingerprints to state-mismatch diagnostics to identify whether the network state, round, side, moves or model fields diverged.
- Avoided local-player lookups after combat has already begun shutting down.

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
