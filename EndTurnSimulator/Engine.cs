using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using EndTurnDamagePreview;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace EndTurnSimulator;

internal static class Engine
{
    private sealed record ForecastAttempt(
        SimulationResult Result, RunRngType[] UsedRngs, bool HasStampede, ulong RunSeed);

    private static bool initialized;
    private static bool loadedBaseLib;
    private static string loadedModSignature = "";
    private static bool forecasting;
    private static bool futureChoice;
    private static bool engineError;
    private static string? engineErrorText;
    private static string? engineErrorFull;
    private static readonly Harmony patches = new("EndTurnSimulator.IsolatedWorker");
    private static readonly MethodInfo enterRoom = AccessTools.Method(typeof(RunManager), "EnterRoomInternal");

    private static void Initialize(string? data = null, bool baseLib = false, SimulationMod[]? requestedMods = null)
    {
        requestedMods ??= [];
        string requestedSignature = SimulationProtocol.ModSignature(requestedMods);
        if (initialized)
        {
            if (baseLib != loadedBaseLib) throw new InvalidOperationException("BaseLib configuration changed");
            if (requestedSignature != loadedModSignature) throw new InvalidOperationException("Loaded mod set changed");
            return;
        }
        // This executable never runs inside the live game. TestMode uses an in-memory save store.
        TestMode.IsOn = true;
        AccessTools.PropertySetter(typeof(ModManager), nameof(ModManager.State)).Invoke(null, [ModManagerState.Initialized]);
        Patch("MegaCrit.Sts2.Core.Logging.Logger:GetIsRunningFromGodotEditor", nameof(ReturnFalse));
        Patch("MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter:Print", nameof(Skip));
        MegaCrit.Sts2.Core.Logging.Log.LogCallback += (level, text, _) => { if (level == MegaCrit.Sts2.Core.Logging.LogLevel.Error) { engineError = true; engineErrorText ??= FirstLine(text); engineErrorFull ??= text; } };
        // SentryService's static constructor allocates Godot StringNames, which crashes a
        // standalone worker without the native engine (0xC0000005). Never touch it here.
        foreach (var sentry in AccessTools.GetDeclaredMethods(AccessTools.TypeByName("MegaCrit.Sts2.Core.Debug.SentryService"))
                     .Where(m => m.IsStatic && m.Name is "CaptureException" or "CaptureMessage" or "AddBreadcrumb" or "SetTag" or "SetExtra"))
            patches.Patch(sentry, prefix: new HarmonyMethod(typeof(Engine), nameof(Skip)));
        // Other mods' initializers print through Godot (GD.Print), which is native-only here.
        foreach (var gd in AccessTools.GetDeclaredMethods(typeof(Godot.GD))
                     .Where(m => m.IsStatic && m.ReturnType == typeof(void) && (m.Name.StartsWith("Print") || m.Name.StartsWith("Push"))))
            patches.Patch(gd, prefix: new HarmonyMethod(typeof(Engine), nameof(Skip)));
        patches.Patch(AccessTools.Method("MegaCrit.Sts2.Core.Localization.LocString:GetFormattedText", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Engine), nameof(EmptyText)));
        // Event rooms log their options' raw text on entry; the worker has no localization tables.
        patches.Patch(AccessTools.Method("MegaCrit.Sts2.Core.Localization.LocString:GetRawText", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Engine), nameof(EmptyText)));
        patches.Patch(AccessTools.Method("MegaCrit.Sts2.Core.Localization.LocString:Exists", [typeof(string), typeof(string)]), prefix: new HarmonyMethod(typeof(Engine), nameof(ReturnTrue)));
        Patch("MegaCrit.Sts2.Core.Commands.TalkCmd:Play", nameof(Skip));
        // Save migrations (v0.111+) touch Controller's static constructor, which needs native Godot.
        // The worker only uses the in-memory TestMode store, so it has nothing to migrate.
        Patch("MegaCrit.Sts2.Core.Saves.Migrations.MigrationRegistry:RegisterAllMigrations", nameof(Skip));
        // CommandLineHelper's static constructor builds a Godot Dictionary (native). The worker has no game arguments.
        Patch("MegaCrit.Sts2.Core.Helpers.CommandLineHelper:HasArg", nameof(ReturnFalse));
        Patch("MegaCrit.Sts2.Core.Helpers.CommandLineHelper:TryGetValue", nameof(NoCommandLineValue));
        Patch("MegaCrit.Sts2.Core.Helpers.CommandLineHelper:GetValue", nameof(NullString));
        // PlatformUtil's static constructor creates a GodotFileIo (native DirAccess). Only the branch is needed, for the peer version.
        Patch("MegaCrit.Sts2.Core.Platform.PlatformUtil:GetPlatformBranch", nameof(DefaultBranch));
        Patch("MegaCrit.Sts2.Core.Debug.ReleaseInfoManager:LoadConfig", nameof(Skip));
        // Replay export bypasses SaveManager's in-memory store: explicitly disable disk export.
        Patch("MegaCrit.Sts2.Core.Runs.RunManager:WriteReplay", nameof(Skip));
        var shuffle = AccessTools.Method("MegaCrit.Sts2.Core.Commands.CardPileCmd:Shuffle");
        var machine = shuffle.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        patches.Patch(AccessTools.Method(machine, "MoveNext"), transpiler: new HarmonyMethod(typeof(Engine), nameof(RemoveShuffleFrameTiming)));
        var play = AccessTools.Method(typeof(CardModel), "OnPlayWrapper").GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        patches.Patch(AccessTools.Method(play, "MoveNext"), transpiler: new HarmonyMethod(typeof(Engine), nameof(ReplaceAnimationClock)));
        var end = AccessTools.Method(typeof(CombatManager), "EndCombatInternal").GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        patches.Patch(AccessTools.Method(end, "MoveNext"), transpiler: new HarmonyMethod(typeof(Engine), nameof(RemoveTooltipCleanup)));
        // v0.111 moved the combat-end body into an EndCombatInternal(CombatTurnState) overload; drop the
        // tooltip cleanup at its source instead (NHoverTipSet's static constructor needs native Godot).
        Patch("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet:Clear", nameof(Skip));
        // DecimillipedeSegment.ReattachMove performs a presentation-only texture load even
        // in TestMode. A standalone worker has no native Godot ResourceLoader, so replace
        // that one load while preserving the move's gameplay effects.
        var reattach = AccessTools.Method("MegaCrit.Sts2.Core.Models.Monsters.DecimillipedeSegment:ReattachMove")
            .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        patches.Patch(AccessTools.Method(reattach, "MoveNext"), transpiler: new HarmonyMethod(typeof(Engine), nameof(RemoveReattachTextureLoad)));
        Patch("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer:ReserveChoiceId", nameof(ObserveChoice));
        // WaterfallGiant only advances its pressure counter outside TestMode (with Spine/music
        // side effects). Keep the gameplay-visible counter, drop the presentation.
        Patch("MegaCrit.Sts2.Core.Models.Monsters.WaterfallGiant:IncrementBuildUpAnimationTrack", nameof(IncrementPressureOnly));
        // Each replay starts a fresh combat. v0.111 waits up to 10s for the previous combat's turn loop, which in
        // the worker stays suspended forever after teardown; that wait made every forecast miss its deadline.
        Patch("MegaCrit.Sts2.Core.Combat.CombatManager:WaitForPreviousTurnLoopToFinish", nameof(NoTurnLoopWait));
        // Public beta: temporary sound effects go through a Godot node whose static setup needs native Godot.
        if (AccessTools.TypeByName("MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager") is { } debugAudio)
            patches.Patch(AccessTools.PropertyGetter(debugAudio, "Instance"), prefix: new HarmonyMethod(typeof(Engine), nameof(ReturnNullObject)));
        var registeredMods = (List<Mod>)AccessTools.Field(typeof(ModManager), "_mods").GetValue(null)!;
        // Mods may ship companion assemblies next to them (BaseLib 3.4+ needs BaseLibCompatibilityHelper).
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var mod in registeredMods)
            {
                string candidate = Path.Combine(mod.path, name.Name + ".dll");
                if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
        if (baseLib)
        {
            var descriptor = requestedMods.FirstOrDefault(mod => mod.Id == "BaseLib");
            string path = descriptor?.AssemblyPath ?? Path.Combine(Directory.GetParent(data!)!.FullName, "mods", "BaseLib", "BaseLib.dll");
            RegisterModAssembly(registeredMods, descriptor ?? new("BaseLib", "unknown", true, path, null), initialize: false);
        }
        var externalMods = requestedMods.Where(mod => mod.Id is not ("BaseLib" or "EndTurnDamagePreview")
            && !string.IsNullOrWhiteSpace(mod.AssemblyPath)).ToArray();
        foreach (var mod in externalMods) RegisterModAssembly(registeredMods, mod, initialize: false);
        foreach (var mod in externalMods.Where(mod => mod.AffectsGameplay))
            InitializeModAssembly(registeredMods.Single(loaded => loaded.manifest?.id == mod.Id));
        if (baseLib && externalMods.Any(mod => mod.AffectsGameplay)) EnableBaseLibExtendedSaves();
        // Same order as OneTimeInitialization.ExecuteEssential (game v0.111+).
        AssemblyInfo.Init();
        ModelDb.Init();
        ModelIdSerializationCache.Init();
        ModelDb.InitIds();
        ActionTypes.Initialize();
        AccessTools.Field(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "_currentProfileId").SetValue(MegaCrit.Sts2.Core.Saves.SaveManager.Instance, (int?)1);
        MegaCrit.Sts2.Core.Saves.SaveManager.Instance.InitProgressData();
        // Cards such as Neutralize read PrefsSave (animation timing only); without it they throw.
        MegaCrit.Sts2.Core.Saves.SaveManager.Instance.InitPrefsDataForTest();
        loadedBaseLib = baseLib;
        loadedModSignature = requestedSignature;
        initialized = true;
    }

    private static void RegisterModAssembly(List<Mod> registeredMods, SimulationMod descriptor, bool initialize)
    {
        if (string.IsNullOrWhiteSpace(descriptor.AssemblyPath) || !File.Exists(descriptor.AssemblyPath))
            throw new FileNotFoundException($"Assembly for mod {descriptor.Id} was not found", descriptor.AssemblyPath);
        string path = Path.GetFullPath(descriptor.AssemblyPath);
        var assembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.Location) && Path.GetFullPath(candidate.Location).Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        string module = assembly.ManifestModule.ModuleVersionId.ToString();
        if (descriptor.ModuleVersionId != null && !module.Equals(descriptor.ModuleVersionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Mod assembly identity mismatch for {descriptor.Id}");
        registeredMods.Add(new Mod
        {
            path = Path.GetDirectoryName(path)!, assemblies = [assembly], state = ModLoadState.Loaded,
            manifest = new ModManifest { id = descriptor.Id, version = descriptor.Version, affectsGameplay = descriptor.AffectsGameplay }
        });
        if (initialize) InitializeModAssembly(registeredMods[^1]);
    }

    private static void InitializeModAssembly(Mod mod)
    {
        var assembly = mod.assemblies[0];
        var initializers = assembly.GetTypes().Where(type => type.GetCustomAttribute<ModInitializerAttribute>() != null).ToArray();
        if (initializers.Length == 0)
        {
            new Harmony("EndTurnSimulator.External." + mod.manifest!.id).PatchAll(assembly);
            return;
        }
        foreach (var type in initializers)
        {
            var attribute = type.GetCustomAttribute<ModInitializerAttribute>()!;
            var method = type.GetMethod(attribute.initializerMethod, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(type.FullName, attribute.initializerMethod);
            try { method.Invoke(null, null); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new InvalidOperationException($"Mod initializer failed for {mod.manifest!.id}: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}", ex.InnerException);
            }
        }
    }

    internal static async Task Warm(string data, bool baseLib, SimulationMod[]? mods = null)
    {
        Initialize(data, baseLib, mods);
        // Exercise the replay and forecast paths now, while the game itself is starting.
        // This moves both model initialization and tiered JIT compilation off the first preview.
        string request = await Fixture(data, baseLib, logExpected: false, mods: mods);
        _ = await Simulate(request, data);
    }

    private static void Patch(string target, string prefix) => patches.Patch(AccessTools.Method(target), prefix: new HarmonyMethod(typeof(Engine), prefix));
    private static bool ReturnFalse(ref bool __result) { __result = false; return false; }
    private static bool ReturnTrue(ref bool __result) { __result = true; return false; }
    private static bool Skip() => false;

    // RunState.CreateForTest gains parameters between game builds (stable vs public beta): call it with
    // defaults for everything but players and seed, so one worker build runs on both.
    private static RunState CreateTestRun(IReadOnlyList<MegaCrit.Sts2.Core.Entities.Players.Player>? players)
    {
        var method = typeof(RunState).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == nameof(RunState.CreateForTest));
        var args = method.GetParameters().Select(p => p.Name switch
        {
            "players" => (object?)players,
            "seed" => "SIMULATIONTEST",
            _ => p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
        }).ToArray();
        return (RunState)method.Invoke(null, args)!;
    }
    private static bool ReturnNullObject(ref object? __result) { __result = null; return false; }

    // With a gameplay mod that depends on BaseLib, the live game prefixes mod model IDs and runs BaseLib's extended saves: every saved card,
    // relic, potion, reward, player and run carries extra BaseLib data (e.g. card modifiers). Without the same
    // patches the worker misreads the replay after the first card. BaseLib's own initializer needs native Godot,
    // so apply just these pieces.
    private static void EnableBaseLibExtendedSaves()
    {
        var harmony = new Harmony("EndTurnSimulator.BaseLibExtendedSaves");
        var patches = AccessTools.TypeByName("BaseLib.Patches.Saves.ExtendedSavePatches")
            ?? throw new InvalidOperationException("BaseLib extended saves not found");
        foreach (var nested in patches.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                     .Where(type => type.GetCustomAttributes<HarmonyPatch>().Any()))
            harmony.CreateClassProcessor(nested).Patch();
        AccessTools.Method(patches, "Patch", [typeof(Harmony)]).Invoke(null, [harmony]);
        // BaseLib prefixes custom model IDs with their mod (e.g. RELIC.TALENTSHARDS-SHARD_POUCH); without it the
        // worker cannot find saved mod content and replaces it with a deprecated relic.
        harmony.CreateClassProcessor(AccessTools.TypeByName("BaseLib.Patches.Content.PrefixIdPatch")
            ?? throw new InvalidOperationException("BaseLib ID prefix patch not found")).Patch();
        AccessTools.PropertySetter(AccessTools.TypeByName("BaseLib.Patches.PostModInitPatch"), "CanModifyGameplay").Invoke(null, [true]);
        AccessTools.Method(AccessTools.TypeByName("BaseLib.Abstracts.CardModifier"), "RegisterSave").Invoke(null, null);
    }

    private static void UseLiveIdTables(string[][] tables)
    {
        for (int i = 0; i < tables.Length; i++)
        {
            string name = SimulationProtocol.IdTableNames[i];
            var toName = AccessTools.StaticFieldRefAccess<List<string>>(typeof(ModelIdSerializationCache), $"_netIdTo{name}NameMap");
            if (tables[i].Length == 0 || toName.SequenceEqual(tables[i])) continue;
            var toId = AccessTools.StaticFieldRefAccess<Dictionary<string, int>>(typeof(ModelIdSerializationCache), $"_{name.ToLowerInvariant()}NameToNetIdMap");
            toName.Clear();
            toId.Clear();
            foreach (string entry in tables[i]) { toId[entry] = toName.Count; toName.Add(entry); }
            AccessTools.PropertySetter(typeof(ModelIdSerializationCache), $"{name}IdBitSize")
                .Invoke(null, [(int)Math.Ceiling(Math.Log2(toName.Count))]);
        }
    }
    private static bool NoTurnLoopWait(ref Task __result) { __result = Task.CompletedTask; return false; }

    private static bool NoCommandLineValue(ref string? __1, ref bool __result)
    {
        __1 = null;
        __result = false;
        return false;
    }

    private static bool DefaultBranch(ref MegaCrit.Sts2.Core.Platform.PlatformBranch __result)
    {
        __result = default;
        return false;
    }

    private static bool NullString(ref string? __result)
    {
        __result = null;
        return false;
    }
    private static bool IncrementPressureOnly(object __instance)
    {
        var property = Traverse.Create(__instance).Property("PressureBuildupIdx");
        property.SetValue(property.GetValue<int>() + 1);
        return false;
    }
    // Keeps the last failing request so it can be replayed offline with --replay-file=<path>.
    private static void DumpFailure(string json, string error)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "EndTurnDamagePreview");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "last-failed-request.json"), json);
            File.WriteAllText(Path.Combine(dir, "last-failed-error.txt"), error);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static string? FirstLine(string? text) => text?.Split('\n', 2)[0].Trim();
    private static string ErrorSuffix() => string.IsNullOrEmpty(engineErrorText) ? "" : ": " + engineErrorText;
    private static bool EmptyText(ref string __result) { __result = ""; return false; }
    private static void ObserveChoice()
    {
        if (!forecasting) return;
        // Once the enemy turn is over, a choice at the start of the next player turn (e.g. an optional
        // exhaust) belongs to the player: the forecast stops there with the state reached so far.
        if (enemyTurnDone && choiceSnapshot == null && CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player)
            choiceSnapshot = Snapshot();
        else futureChoice = true;
        throw new InvalidOperationException("Future player choice required");
    }
    private static bool enemyTurnDone;
    private static SimulationResult? choiceSnapshot;
    private static Dictionary<ulong, MegaCrit.Sts2.Core.Entities.Creatures.Creature[]> forecastPets = [];
    private static RunState? forecastState;
    private static ulong forecastLocalId;
    private static SimulationResult Snapshot()
    {
        var manager = CombatManager.Instance;
        var players = forecastState!.Players.Select(p =>
        {
            var pets = forecastPets.GetValueOrDefault(p.NetId, []).Select((pet, index) => new SimulatedPetState(index,
                pet.Monster?.Id.ToString() ?? "", pet.CurrentHp, pet.Block, pet.IsAlive,
                pet.CurrentHp, pet.CurrentHp, pet.Block, pet.Block)).ToArray();
            return new SimulatedPlayerState(p.NetId, p.Creature.CurrentHp, p.Creature.CurrentHp, p.Creature.Block, p.Creature.Block, pets);
        }).ToArray();
        var local = players.FirstOrDefault(p => p.NetId == forecastLocalId) ?? players[0];
        return new(true, local.MinHp, local.MinBlock, local.Pets, manager.IsInProgress ? "NEXT TURN" : "COMBAT ENDS",
            false, local.MinHp, local.MaxHp, local.MinBlock, local.MaxBlock, [], 1, players);
    }

    private static IEnumerable<CodeInstruction> RemoveShuffleFrameTiming(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        int i = list.FindIndex(c => c.operand is MethodInfo m && m.DeclaringType == typeof(Godot.Engine) && m.Name == "GetMainLoop");
        if (i < 0 || list[i + 1].opcode != OpCodes.Castclass
            || list[i + 2].operand is not MethodInfo root || root.Name != "get_Root"
            || list[i + 3].operand is not MethodInfo delta || delta.Name != "GetProcessDeltaTime")
            throw new InvalidOperationException("Unsupported shuffle animation layout");
        // Remove only the visual frame-duration query, not the shuffle, RNG or hooks.
        list[i].opcode = OpCodes.Ldc_R8;
        list[i].operand = double.PositiveInfinity;
        list.RemoveRange(i + 1, 3);
        return list;
    }

    private static ulong AnimationTicks() => (ulong)System.Environment.TickCount64;
    private static IEnumerable<CodeInstruction> RemoveTooltipCleanup(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo m && m.DeclaringType?.FullName == "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet" && m.Name == "Clear")
            { instruction.opcode = OpCodes.Nop; instruction.operand = null; }
            yield return instruction;
        }
    }
    private static IEnumerable<CodeInstruction> ReplaceAnimationClock(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo m && m.DeclaringType == typeof(Godot.Time) && m.Name == "GetTicksMsec")
                instruction.operand = AccessTools.Method(typeof(Engine), nameof(AnimationTicks));
            yield return instruction;
        }
    }

    private static Texture2D? SkipTextureLoad(string path, string? typeHint, ResourceLoader.CacheMode cacheMode) => null;
    private static IEnumerable<CodeInstruction> RemoveReattachTextureLoad(IEnumerable<CodeInstruction> instructions)
    {
        bool replaced = false;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(ResourceLoader)
                && method.Name == nameof(ResourceLoader.Load) && method.ReturnType == typeof(Texture2D))
            {
                instruction.operand = AccessTools.Method(typeof(Engine), nameof(SkipTextureLoad));
                replaced = true;
            }
            yield return instruction;
        }
        if (!replaced) throw new InvalidOperationException("Unsupported Decimillipede reattach layout");
    }

    internal static async Task<SimulationResult> Simulate(string json, string data)
    {
        // Kept before simulating, so a request that kills the worker can still be replayed offline.
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "EndTurnDamagePreview");
            Directory.CreateDirectory(dir);
            if (json.Contains("\"IdTables\":[[")) File.WriteAllText(Path.Combine(dir, "last-request.json"), json);
        }
        catch { }
        var request = JsonSerializer.Deserialize<SimulationRequest>(json) ?? throw new InvalidDataException();
        Initialize(data, request.BaseLib, request.Mods);
        if (typeof(CombatState).Module.ModuleVersionId.ToString() != request.GameModule)
            return Unknown("Engine version mismatch");
        // Gameplay mods (e.g. Talent Shards) make the live model hash depend on when BaseLib registers their
        // saved properties, so it can differ from the worker's although the same assemblies are loaded (their
        // module identities are checked when they are registered). The replayed state stamp still guards the forecast.
        if (request.IdTables is { Length: 4 } tables) UseLiveIdTables(tables);
        bool gameplayMods = request.Mods.Any(mod => mod.AffectsGameplay && mod.Id is not ("BaseLib" or "EndTurnDamagePreview"));
        if (!gameplayMods && ModelIdSerializationCache.Hash != request.ModelHash)
            return Unknown($"Engine version mismatch (model hash {ModelIdSerializationCache.Hash} vs {request.ModelHash})");
        var reader = new PacketReader();
        reader.Reset(Convert.FromBase64String(request.Replay));
        var replay = reader.Read<CombatReplay>();

        ForecastAttempt exact;
        try { exact = await RunForecastAttempt(request, replay, null); }
        catch (Exception ex) { DumpFailure(json, ex.ToString()); throw; }
        if (engineErrorFull != null) DumpFailure(json, engineErrorFull);
        else if (exact.Result.Status.StartsWith("Combat state mismatch", StringComparison.Ordinal)) DumpFailure(json, exact.Result.Status);
        if (!exact.Result.Verified) return exact.Result;

        // MonsterAi rolls select a later visible intent after the current enemy turn,
        // so they cannot change damage in the interval being previewed. Shuffle is
        // relevant here only when Stampede performs its hidden random card choice.
        var sensitive = exact.UsedRngs.Where(type => type switch
        {
            RunRngType.CombatTargets or RunRngType.CombatCardSelection
                or RunRngType.CombatCardGeneration or RunRngType.CombatPotionGeneration
                or RunRngType.CombatEnergyCosts or RunRngType.CombatOrbs => true,
            RunRngType.Shuffle => exact.HasStampede,
            _ => false
        }).Distinct().ToArray();
        if (sensitive.Length == 0) return exact.Result;

        var outcomes = new List<SimulationResult> { exact.Result };
        const int alternateBranches = 12;
        for (int branch = 1; branch <= alternateBranches; branch++)
        {
            var overrides = sensitive.ToDictionary(type => type,
                type => MixSeed(exact.RunSeed, type, branch));
            var alternate = await RunForecastAttempt(request, replay, overrides);
            if (alternate.Result.Verified) outcomes.Add(alternate.Result);
        }
        return CombineRandomOutcomes(outcomes, sensitive);
    }

    private static async Task<ForecastAttempt> RunForecastAttempt(
        SimulationRequest request,
        CombatReplay replay,
        IReadOnlyDictionary<RunRngType, ulong>? rngOverrides)
    {
        engineError = false;
        engineErrorText = null;
        engineErrorFull = null;
        futureChoice = false;
        forecasting = false;
        try
        {
            var state = RunState.FromSerializable(replay.serializableRun);
            var run = RunManager.Instance;
            forecastLocalId = request.LocalNetId is { } localId && state.Players.Any(p => p.NetId == localId) ? localId : state.Players[0].NetId;
            run.SetUpReplay(state, replay, forecastLocalId);
            AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(run, [false]);
            run.CombatStateSynchronizer.IsDisabled = true;
            run.Launch();
            await run.GenerateMap();
            run.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
            run.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
            run.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
            run.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
            if (request.EventEncounter == null)
                await run.LoadIntoLatestMapCoord(replay.serializableRun.PreFinishedRoom == null ? null : AbstractRoom.FromSerializable(replay.serializableRun.PreFinishedRoom, state));
            else
            {
                // Event combat: the snapshot was taken as the event option started the fight,
                // so enter that encounter directly instead of replaying the event.
                if (state.VisitedMapCoords.Count > 0) state.ActFloor = state.VisitedMapCoords[^1].row + 1;
                var encounter = ModelDb.GetById<EncounterModel>(ModelId.Deserialize(request.EventEncounter)).ToMutable();
                await (Task)enterRoom.Invoke(run, [new CombatRoom(encounter, state), false])!;
            }
            foreach (var item in replay.events)
            {
                switch (item.eventType)
                {
                    case CombatReplayEventType.GameAction:
                        while (CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo) await Task.Delay(1);
                        var action = item.action!.ToGameAction(state.GetPlayer(item.playerId!.Value)!);
                        if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                            while (CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy) await Task.Delay(1);
                        run.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                        if (action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction) await run.ActionExecutor.FinishedExecutingActions();
                        break;
                    case CombatReplayEventType.HookAction:
                        run.ActionQueueSet.EnqueueWithoutSynchronizing(run.ActionQueueSynchronizer.GetHookActionForId(item.hookId!.Value, item.playerId!.Value, item.gameActionType!.Value));
                        break;
                    case CombatReplayEventType.ResumeAction:
                        run.ActionQueueSet.ResumeActionWithoutSynchronizing(item.actionId!.Value);
                        break;
                    case CombatReplayEventType.PlayerChoice:
                        run.PlayerChoiceSynchronizer.ReceiveReplayChoice(state.GetPlayer(item.playerId!.Value)!, item.choiceId!.Value, item.playerChoiceResult!.Value);
                        break;
                    default: return new(Unknown("Unsupported replay event"), [], false, state.Rng.Seed);
                }
                await Task.Yield();
            }
            await run.ActionExecutor.FinishedExecutingActions();
            while (CombatManager.Instance.IsStarting || CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo) await Task.Delay(1);
            var combat = CombatManager.Instance.DebugOnlyGetState();
            if (engineError) return new(Unknown("Replay produced an engine error" + ErrorSuffix()), [], false, state.Rng.Seed);
            if (combat == null) return new(Unknown("Replay did not restore combat"), [], false, state.Rng.Seed);
            string actual = SimulationProtocol.Stamp(state, combat);
            if (actual != request.Baseline)
                return new(Unknown("Combat state mismatch: " + SimulationProtocol.DescribeStampMismatch(request.Baseline, actual)), [], false, state.Rng.Seed);

            bool hasStampede = combat.Players.Any(player => player.Creature.Powers.Any(power =>
                power.Id.ToString().Contains("STAMPEDE", StringComparison.OrdinalIgnoreCase)));
            if (rngOverrides != null)
                foreach (var (type, seed) in rngOverrides) state.Rng.MockRng(type, seed);
            var before = state.Rng.ToSerializable().Rngs;
            var result = await Predict(state);
            var after = state.Rng.ToSerializable().Rngs;
            var used = before.Keys.Concat(after.Keys).Distinct()
                .Where(type => before.GetValueOrDefault(type) != after.GetValueOrDefault(type))
                .ToArray();
            return new(result, used, hasStampede, state.Rng.Seed);
        }
        finally
        {
            forecasting = false;
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp(graceful: true);
            // The forecast drives the turn by hand, so the combat's own turn loop stays parked on a signal that never
            // comes. Forget it, or the next request's combat waits 10 s for it to finish and then logs an error.
            turnLoopTaskField.SetValue(CombatManager.Instance, null);
        }
    }

    private static ulong MixSeed(ulong seed, RunRngType type, int branch)
    {
        unchecked
        {
            ulong value = seed ^ ((ulong)type + 1ul) * 0x9E3779B97F4A7C15ul ^ (ulong)branch * 0xBF58476D1CE4E5B9ul;
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9ul;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBul;
            value ^= value >> 31;
            return value;
        }
    }

    private static SimulationResult CombineRandomOutcomes(
        IReadOnlyList<SimulationResult> outcomes,
        RunRngType[] sources)
    {
        var first = outcomes[0];
        // Each outcome lists every player; older/test outcomes only carry the top-level player.
        static SimulatedPlayerState[] PlayersOf(SimulationResult r) => r.Players
            ?? [new SimulatedPlayerState(0, r.Hp, r.Hp, r.Block, r.Block, r.Pets)];
        var players = PlayersOf(first).Select(player =>
        {
            var variants = outcomes.Select(r => PlayersOf(r).FirstOrDefault(c => c.NetId == player.NetId)).OfType<SimulatedPlayerState>().ToArray();
            var pets = player.Pets.Select(pet =>
            {
                var petVariants = variants.SelectMany(v => v.Pets)
                    .Where(candidate => candidate.Index == pet.Index && candidate.ModelId == pet.ModelId).ToArray();
                int petMinHp = petVariants.Min(c => c.MinHp), petMaxHp = petVariants.Max(c => c.MaxHp);
                int petMinBlock = petVariants.Min(c => c.MinBlock), petMaxBlock = petVariants.Max(c => c.MaxBlock);
                return new SimulatedPetState(pet.Index, pet.ModelId, petMinHp, petMinBlock,
                    petVariants.All(c => c.Alive), petMinHp, petMaxHp, petMinBlock, petMaxBlock);
            }).ToArray();
            return new SimulatedPlayerState(player.NetId, variants.Min(v => v.MinHp), variants.Max(v => v.MaxHp),
                variants.Min(v => v.MinBlock), variants.Max(v => v.MaxBlock), pets);
        }).ToArray();
        int localIndex = Math.Max(0, Array.FindIndex(players, p => p.NetId == forecastLocalId));
        var local = players[localIndex];
        bool randomized = players.Any(p => p.MinHp != p.MaxHp || p.MinBlock != p.MaxBlock
            || p.Pets.Any(pet => pet.MinHp != pet.MaxHp || pet.MinBlock != pet.MaxBlock));
        string[] randomSources = randomized ? sources.Select(source => source.ToString()).ToArray() : [];
        return new(true, local.MinHp, local.MinBlock, local.Pets,
            randomized ? first.Status + " (HIDDEN RANDOMNESS)" : first.Status,
            randomized, local.MinHp, local.MaxHp, local.MinBlock, local.MaxBlock, randomSources, outcomes.Count,
            first.Players == null ? null : players);
    }

    private static async Task<SimulationResult> Predict(RunState state)
    {
        forecasting = true;
        enemyTurnDone = false;
        choiceSnapshot = null;
        forecastState = state;
        try
        {
            var manager = CombatManager.Instance;
            forecastPets = state.Players.ToDictionary(p => p.NetId, p => p.PlayerCombatState?.Pets.ToArray() ?? []);
            // Same sequence the turn loop runs once every player is ready: end the player turn, play the
            // enemy turn, then start the next player turn. Driven directly so the live turn loop stays parked.
            object? turnState = turnStateField.GetValue(manager);
            if (turnState == null) return Unknown("Combat is not running" + ErrorSuffix());
            await TurnStep("EndPlayerTurnPhaseOneInternal", turnState);
            if (manager.IsInProgress) await TurnStep("EndPlayerTurnPhaseTwoInternal", turnState);
            if (manager.IsInProgress) await TurnStep("SwitchFromPlayerToEnemySide", turnState);
            bool enemySide = manager.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy;
            enemyTurnDone = !enemySide;
            if (manager.IsInProgress) await TurnStep("StartTurn", turnState);
            enemyTurnDone = true;
            if (enemySide && manager.IsInProgress && manager.DebugOnlyGetState()?.CurrentSide == CombatSide.Player)
                await TurnStep("StartTurn", turnState);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            return futureChoice ? Unknown("Player choice required") : engineError && choiceSnapshot == null ? Unknown("Simulation error" + ErrorSuffix())
                : choiceSnapshot ?? Snapshot();
        }
        catch (Exception) when (futureChoice) { return Unknown("Player choice required"); }
        catch (Exception) when (choiceSnapshot != null) { return choiceSnapshot; }
        finally { forecasting = false; }
    }

    private static readonly FieldInfo turnStateField = AccessTools.Field(typeof(CombatManager), "_turnState");
    private static readonly FieldInfo turnLoopTaskField = AccessTools.Field(typeof(CombatManager), "_turnLoopTask");

    /// <summary>Launches the combat turn loop and waits for the first player turn to be playable.</summary>
    private static async Task StartFixtureCombat()
    {
        var manager = CombatManager.Instance;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTurnStarted(CombatState _) => started.TrySetResult();
        manager.TurnStarted += OnTurnStarted;
        try
        {
            manager.AfterCombatRoomLoaded();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        finally { manager.TurnStarted -= OnTurnStarted; }
    }

    private static Task TurnStep(string method, object turnState)
    {
        // StartTurn also takes an optional enemy-turn test hook; pass none.
        bool startTurn = method == "StartTurn";
        Type[] types = startTurn ? [turnState.GetType(), typeof(Func<Task>)] : [turnState.GetType()];
        var info = AccessTools.Method(typeof(CombatManager), method, types)
            ?? throw new MissingMethodException(nameof(CombatManager), method);
        return (Task)info.Invoke(CombatManager.Instance, startTurn ? [turnState, null] : [turnState])!;
    }

    private static SimulationResult Unknown(string reason) =>
        new(false, 0, 0, [], reason, false, 0, 0, 0, 0, [], 0);

    internal static string Diagnostic(string data, bool baseLib, SimulationMod[]? mods = null)
    {
        Initialize(data, baseLib, mods);
        string module = typeof(CombatState).Module.ModuleVersionId.ToString();
        string? baseLibVersion = null;
        if (baseLib)
        {
            string path = Path.Combine(Directory.GetParent(data)!.FullName, "mods", "BaseLib", "BaseLib.dll");
            if (File.Exists(path)) baseLibVersion = AssemblyName.GetAssemblyName(path).Version?.ToString();
        }
        return JsonSerializer.Serialize(new
        {
            Platform = RuntimeInformation.RuntimeIdentifier,
            OperatingSystem = RuntimeInformation.OSDescription,
            DataPath = data,
            GameModule = module,
            CompatibilityPolicy = "matching live and worker engine, model and mod identities",
            ModelHash = ModelIdSerializationCache.Hash,
            DotnetHost = SimulationProtocol.ResolveDotnetHost(),
            BaseLibVersion = baseLibVersion ?? (baseLib ? "unknown" : "not loaded"),
            LoadedMods = mods ?? []
        });
    }

    internal static async Task SelfTest(string scenario)
    {
        Initialize();
        if (scenario == "random-range")
        {
            static SimulationResult Outcome(int hp) => new(true, hp, 0, [], "NEXT TURN",
                false, hp, hp, 0, 0, [], 1);
            var range = CombineRandomOutcomes([Outcome(82), Outcome(71), Outcome(76)],
                [RunRngType.CombatTargets]);
            if (!range.Verified || !range.Randomized || range.MinHp != 71 || range.MaxHp != 82
                || range.Hp != 71 || range.RandomSources is not [nameof(RunRngType.CombatTargets)])
                throw new InvalidOperationException("Hidden-randomness range was not aggregated safely");
            Console.WriteLine("PASS random-range: 71-82 HP, CombatTargets marked hidden");
            return;
        }
        if (scenario == "happy-flower-visual")
        {
            var flower = ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.HappyFlower>().ToMutable();
            AccessTools.Field(flower.GetType(), "_isActivating").SetValue(flower, true);
            string scalars = JsonSerializer.Serialize(AccessTools.Method(typeof(SimulationProtocol), "ModelScalars").Invoke(null, [flower]));
            if (scalars.Contains("_isActivating", StringComparison.Ordinal) || !scalars.Contains("_turnsSeen", StringComparison.Ordinal))
                throw new InvalidOperationException("Happy Flower scalar filter rejected gameplay state or retained visual state");
            Console.WriteLine("PASS happy-flower-visual: animation flag ignored, turn counter retained");
            return;
        }
        var state = CreateTestRun(null);
        LocalContext.NetId = 1;
        RunManager.Instance.SetUpTest(state, new NetSingleplayerGameService());
        RunManager.Instance.GenerateRooms();
        await RunManager.Instance.GenerateMap();
        var encounter = ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.CultistsNormal>().ToMutable();
        var combat = new CombatState(encounter, state);
        foreach (var p in state.Players) combat.AddPlayer(p);
        encounter.GenerateMonstersWithSlots(state);
        foreach (var (monster, slot) in encounter.MonstersWithSlots) combat.AddCreature(combat.CreateCreature(monster, CombatSide.Enemy, slot));
        state.PushRoom(new CombatRoom(combat));
        CombatManager.Instance.SetUpCombat(combat);
        await StartFixtureCombat();
        if (scenario != "turns")
        {
            var player = state.Players[0];
            var context = new ThrowingPlayerChoiceContext();
            int expectedHp = 1000;
            int expectedBlock = 0;
            int? expectedPetHp = null;
            bool? expectedPetAlive = null;
            switch (scenario)
            {
                case "burn":
                    await CardPileCmd.Add(combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Burn>(player), MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
                    expectedHp = 998;
                    break;
                case "block-expiry": player.Creature.GainBlockInternal(20); break;
                case "barricade":
                    player.Creature.GainBlockInternal(20);
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.BarricadePower>(context, player.Creature, 1, player.Creature, null);
                    expectedBlock = 20;
                    break;
                case "regen":
                    player.Creature.SetCurrentHpInternal(900);
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.RegenPower>(context, player.Creature, 5, player.Creature, null);
                    expectedHp = 905;
                    break;
                case "poison":
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.PoisonPower>(context, player.Creature, 3, null, null);
                    expectedHp = 997;
                    break;
                case "choice":
                    await CardPileCmd.Add(combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(player), MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
                    await CardPileCmd.Add(combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.DefendIronclad>(player), MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
                    // Tools of the Trade asks which card to discard when the next turn starts (Well-Laid Plans no longer asks since v0.111).
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.ToolsOfTheTradePower>(context, player.Creature, 1, player.Creature, null);
                    break;
                case "lethal-burn":
                    player.Creature.SetCurrentHpInternal(1);
                    await CardPileCmd.Add(combat.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Burn>(player), MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand);
                    expectedHp = 0;
                    break;
                case "poison-victory":
                    foreach (var enemy in combat.Enemies.ToArray())
                        await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.PoisonPower>(context, enemy, 10000, player.Creature, null);
                    break;
                case "osty":
                    var osty = (await OstyCmd.Summon(context, player, 7, null)).Creature!;
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.PoisonPower>(context, osty, 2, player.Creature, null);
                    expectedPetHp = 5;
                    expectedPetAlive = true;
                    break;
                case "osty-lethal":
                    var doomedOsty = (await OstyCmd.Summon(context, player, 7, null)).Creature!;
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.PoisonPower>(context, doomedOsty, 7, player.Creature, null);
                    expectedPetHp = 0;
                    expectedPetAlive = false;
                    break;
                default: throw new ArgumentException("Unknown test");
            }
            var outcome = await Predict(state);
            if (scenario == "choice")
            {
                // The choice comes after the enemy turn: the forecast stops there instead of failing.
                if (!outcome.Verified || futureChoice || choiceSnapshot == null) throw new InvalidOperationException("Next-turn choice did not stop the forecast");
                Console.WriteLine($"PASS choice: stopped at next-turn choice, {outcome.Hp} HP");
                return;
            }
            if (!outcome.Verified || outcome.Hp != expectedHp || outcome.Block != expectedBlock)
                throw new InvalidOperationException($"{scenario}: expected HP {expectedHp}/block {expectedBlock}, got {outcome}");
            if (expectedPetHp.HasValue && (outcome.Pets.Length != 1 || outcome.Pets[0].Hp != expectedPetHp
                || outcome.Pets[0].Alive != expectedPetAlive))
                throw new InvalidOperationException($"{scenario}: expected pet HP {expectedPetHp}/alive {expectedPetAlive}, got {JsonSerializer.Serialize(outcome.Pets)}");
            Console.WriteLine(expectedPetHp.HasValue
                ? $"PASS {scenario}: player {outcome.Hp} HP, pet {outcome.Pets[0].Hp} HP, alive {outcome.Pets[0].Alive}"
                : $"PASS {scenario}: {outcome.Hp} HP, {outcome.Block} block");
            return;
        }
        int[] expected = [1000, 990, 973];
        foreach (var hp in expected)
        {
            var result = await Predict(state);
            if (!result.Verified || result.Hp != hp) throw new InvalidOperationException($"Unexpected result: {result}");
            Console.WriteLine($"PASS round {combat.RoundNumber}: {result.Hp} HP");
        }
    }

    internal static async Task<string> Fixture(string data, bool baseLib, bool logExpected = true, SimulationMod[]? mods = null, bool eventCombat = false)
    {
        mods ??= [];
        Initialize(data, baseLib, mods);
        var run = RunManager.Instance;
        try
        {
            var player = MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(MegaCrit.Sts2.Core.Unlocks.UnlockState.all, 1);
            var state = CreateTestRun([player]);
            run.SetUpTest(state, new NetSingleplayerGameService());
            run.Launch();
            run.GenerateRooms();
            await run.GenerateMap();
            run.CombatReplayWriter.IsEnabled = true;
            var point = state.Map.GetAllMapPoints().First(p => p.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Monster);
            state.AddVisitedMapCoord(point.coord);
            string? eventEncounter = null;
            if (!eventCombat) await run.LoadIntoLatestMapCoord(null);
            else
            {
                // Mirrors an event option starting a fight: the run changed after the map point was
                // entered (HP lost in the event), then the client snapshot is taken at combat entry.
                state.ActFloor = point.coord.row + 1;
                player.Creature.SetCurrentHpInternal(player.Creature.CurrentHp - 7m);
                run.CombatReplayWriter.IsEnabled = true;
                run.CombatReplayWriter.RecordInitialState(run.ToSave(null));
                var encounter = ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.DenseVegetationEventEncounter>();
                eventEncounter = encounter.Id.ToString();
                await (Task)enterRoom.Invoke(run, [new CombatRoom(encounter.ToMutable(), state), false])!;
            }
            while (CombatManager.Instance.IsStarting) await Task.Delay(1);
            await run.ActionExecutor.FinishedExecutingActions();
            var combat = CombatManager.Instance.DebugOnlyGetState()!;
            for (int turn = 0; turn < 2; turn++)
            {
                int round = combat.RoundNumber;
                run.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
                while (combat.RoundNumber == round || CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo) await Task.Delay(1);
                await run.ActionExecutor.FinishedExecutingActions();
            }
            var defend = player.PlayerCombatState!.Hand.Cards.FirstOrDefault(c => c.Id.Entry.StartsWith("DEFEND", StringComparison.Ordinal));
            if (defend != null)
            {
                run.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(defend, null));
                await run.ActionExecutor.FinishedExecutingActions();
            }
            var replay = (CombatReplay)AccessTools.Field(typeof(CombatReplayWriter), "_replay").GetValue(run.CombatReplayWriter)!;
            var request = new SimulationRequest(SimulationProtocol.Pack(replay), SimulationProtocol.Stamp(state, combat), ModelIdSerializationCache.Hash,
                typeof(CombatState).Module.ModuleVersionId.ToString(), baseLib, mods, eventEncounter);
            var expected = await Predict(state);
            if (logExpected) Console.Error.WriteLine("Expected " + JsonSerializer.Serialize(expected));
            return JsonSerializer.Serialize(request);
        }
        finally
        {
            forecasting = false;
            if (run.IsInProgress) run.CleanUp(graceful: true);
        }
    }
}
