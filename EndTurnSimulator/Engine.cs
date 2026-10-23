using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using EndTurnDamagePreview;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
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
    private static bool initialized;
    private static bool loadedBaseLib;
    private static bool forecasting;
    private static bool futureChoice;
    private static bool engineError;
    private static readonly Harmony patches = new("EndTurnSimulator.IsolatedWorker");

    private static void Initialize(string? data = null, bool baseLib = false)
    {
        if (initialized)
        {
            if (baseLib != loadedBaseLib) throw new InvalidOperationException("BaseLib configuration changed");
            return;
        }
        // This executable never runs inside the live game. TestMode uses an in-memory save store.
        TestMode.IsOn = true;
        AccessTools.PropertySetter(typeof(ModManager), nameof(ModManager.State)).Invoke(null, [ModManagerState.Initialized]);
        Patch("MegaCrit.Sts2.Core.Logging.Logger:GetIsRunningFromGodotEditor", nameof(ReturnFalse));
        Patch("MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter:Print", nameof(Skip));
        MegaCrit.Sts2.Core.Logging.Log.LogCallback += (level, _, _) => { if (level == MegaCrit.Sts2.Core.Logging.LogLevel.Error) engineError = true; };
        patches.Patch(AccessTools.Method("MegaCrit.Sts2.Core.Localization.LocString:GetFormattedText", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Engine), nameof(EmptyText)));
        patches.Patch(AccessTools.Method("MegaCrit.Sts2.Core.Localization.LocString:Exists", [typeof(string), typeof(string)]), prefix: new HarmonyMethod(typeof(Engine), nameof(ReturnTrue)));
        Patch("MegaCrit.Sts2.Core.Commands.TalkCmd:Play", nameof(Skip));
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
        Patch("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer:ReserveChoiceId", nameof(ObserveChoice));
        if (baseLib)
        {
            string path = Path.Combine(Directory.GetParent(data!)!.FullName, "mods", "BaseLib", "BaseLib.dll");
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            var mods = (List<Mod>)AccessTools.Field(typeof(ModManager), "_mods").GetValue(null)!;
            mods.Add(new Mod { path = Path.GetDirectoryName(path)!, assembly = assembly, state = ModLoadState.Loaded, manifest = new ModManifest { id = "BaseLib" } });
        }
        ModelIdSerializationCache.Init();
        ActionTypes.Initialize();
        ModelDb.Init();
        ModelDb.InitIds();
        AccessTools.Field(typeof(MegaCrit.Sts2.Core.Saves.SaveManager), "_currentProfileId").SetValue(MegaCrit.Sts2.Core.Saves.SaveManager.Instance, (int?)1);
        MegaCrit.Sts2.Core.Saves.SaveManager.Instance.InitProgressData();
        loadedBaseLib = baseLib;
        initialized = true;
    }

    internal static async Task Warm(string data, bool baseLib)
    {
        Initialize(data, baseLib);
        // Exercise the replay and forecast paths now, while the game itself is starting.
        // This moves both model initialization and tiered JIT compilation off the first preview.
        string request = await Fixture(data, baseLib, logExpected: false);
        _ = await Simulate(request, data);
    }

    private static void Patch(string target, string prefix) => patches.Patch(AccessTools.Method(target), prefix: new HarmonyMethod(typeof(Engine), prefix));
    private static bool ReturnFalse(ref bool __result) { __result = false; return false; }
    private static bool ReturnTrue(ref bool __result) { __result = true; return false; }
    private static bool Skip() => false;
    private static bool EmptyText(ref string __result) { __result = ""; return false; }
    private static void ObserveChoice() { if (forecasting) { futureChoice = true; throw new InvalidOperationException("Future player choice required"); } }

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

    private static ulong AnimationTicks() => (ulong)Environment.TickCount64;
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

    internal static async Task<SimulationResult> Simulate(string json, string data)
    {
        var request = JsonSerializer.Deserialize<SimulationRequest>(json) ?? throw new InvalidDataException();
        if (!SimulationProtocol.SupportsGameModule(request.GameModule)) return Unknown("Unsupported game version");
        Initialize(data, request.BaseLib);
        if (typeof(CombatState).Module.ModuleVersionId.ToString() != request.GameModule || ModelIdSerializationCache.Hash != request.ModelHash)
            return Unknown("Engine version mismatch");
        engineError = false;
        futureChoice = false;
        forecasting = false;
        try
        {
            var reader = new PacketReader();
            reader.Reset(Convert.FromBase64String(request.Replay));
            var replay = reader.Read<CombatReplay>();
            if (replay.serializableRun.Players.Count != 1) return Unknown("Multiplayer not supported");
            var state = RunState.FromSerializable(replay.serializableRun);
            var run = RunManager.Instance;
            run.SetUpReplay(state, replay);
            AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(run, [false]);
            run.CombatStateSynchronizer.IsDisabled = true;
            run.Launch();
            await run.GenerateMap();
            run.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
            run.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
            run.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
            run.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
            await run.LoadIntoLatestMapCoord(replay.serializableRun.PreFinishedRoom == null ? null : AbstractRoom.FromSerializable(replay.serializableRun.PreFinishedRoom, state));
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
                    default: return Unknown("Unsupported replay event");
                }
                await Task.Yield();
            }
            await run.ActionExecutor.FinishedExecutingActions();
            while (CombatManager.Instance.IsStarting || CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo) await Task.Delay(1);
            var combat = CombatManager.Instance.DebugOnlyGetState();
            if (engineError || combat == null || SimulationProtocol.Stamp(state, combat) != request.Baseline)
                return Unknown("Combat state mismatch");
            return await Predict(state);
        }
        finally
        {
            forecasting = false;
            if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp(graceful: true);
        }
    }

    private static async Task<SimulationResult> Predict(RunState state)
    {
        forecasting = true;
        try
        {
            var manager = CombatManager.Instance;
            var petReferences = state.Players[0].PlayerCombatState?.Pets.ToArray() ?? [];
            await manager.EndPlayerTurnPhaseOneInternal();
            if (manager.IsInProgress) await manager.EndPlayerTurnPhaseTwoInternal();
            if (manager.IsInProgress) await manager.SwitchFromPlayerToEnemySide();
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            var player = state.Players[0].Creature;
            var pets = petReferences.Select((pet, index) => new SimulatedPetState(index,
                pet.Monster?.Id.ToString() ?? "", pet.CurrentHp, pet.Block, pet.IsAlive)).ToArray();
            return futureChoice ? Unknown("Player choice required") : engineError ? Unknown("Simulation error")
                : new(true, player.CurrentHp, player.Block, pets, manager.IsInProgress ? "NEXT TURN" : "COMBAT ENDS");
        }
        catch (Exception) when (futureChoice) { return Unknown("Player choice required"); }
        finally { forecasting = false; }
    }

    private static SimulationResult Unknown(string reason) => new(false, 0, 0, [], reason);

    internal static string Diagnostic(string data, bool baseLib)
    {
        Initialize(data, baseLib);
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
            GameModuleSupported = SimulationProtocol.SupportsGameModule(module),
            SupportedGameModules = SimulationProtocol.SupportedGameModules,
            ModelHash = ModelIdSerializationCache.Hash,
            DotnetHost = SimulationProtocol.ResolveDotnetHost(),
            BaseLibVersion = baseLibVersion ?? (baseLib ? "unknown" : "not loaded")
        });
    }

    internal static async Task SelfTest(string scenario)
    {
        Initialize();
        var state = RunState.CreateForTest(seed: "SIMULATIONTEST");
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
        await CombatManager.Instance.StartCombatInternal();
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
                    await PowerCmd.Apply<MegaCrit.Sts2.Core.Models.Powers.WellLaidPlansPower>(context, player.Creature, 1, player.Creature, null);
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
                if (outcome.Verified || !futureChoice) throw new InvalidOperationException("Future choice was not rejected");
                Console.WriteLine("PASS choice: no unverified number");
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

    internal static async Task<string> Fixture(string data, bool baseLib, bool logExpected = true)
    {
        Initialize(data, baseLib);
        var run = RunManager.Instance;
        try
        {
            var player = MegaCrit.Sts2.Core.Entities.Players.Player.CreateForNewRun<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(MegaCrit.Sts2.Core.Unlocks.UnlockState.all, 1);
            var state = RunState.CreateForTest(players: [player], seed: "SIMULATIONTEST");
            run.SetUpTest(state, new NetSingleplayerGameService());
            run.Launch();
            run.GenerateRooms();
            await run.GenerateMap();
            run.CombatReplayWriter.IsEnabled = true;
            var point = state.Map.GetAllMapPoints().First(p => p.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Monster);
            state.AddVisitedMapCoord(point.coord);
            await run.LoadIntoLatestMapCoord(null);
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
            var request = new SimulationRequest(SimulationProtocol.Pack(replay), SimulationProtocol.Stamp(state, combat), ModelIdSerializationCache.Hash, typeof(CombatState).Module.ModuleVersionId.ToString(), baseLib);
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
