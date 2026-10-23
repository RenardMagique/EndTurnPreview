using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;

namespace EndTurnDamagePreview;

internal static class SimulationClient
{
    private static readonly FieldInfo replayField = AccessTools.Field(typeof(CombatReplayWriter), "_replay");
    private static Task<SimulationResult>? computation;
    private static bool dirty = true;
    private static Forecast pending = Forecast.Pending("Calculating…");
    private static WorkerSession? session;
    private static string diagnosticContext = "combat state unavailable";
    private static string? lastDiagnosticKey;

    internal static void StartWarm()
    {
        string data = Path.GetDirectoryName(typeof(CombatState).Assembly.Location)!;
        string worker = Path.Combine(Path.GetDirectoryName(typeof(MainFile).Assembly.Location)!, "simulator", "EndTurnSimulator.dll");
        bool baseLib = ModManager.GetLoadedMods().Any(m => m.manifest?.id == "BaseLib");
        session = new WorkerSession(worker, data, baseLib);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => session?.Dispose();
    }

    internal static void Invalidate()
    {
        computation = null;
        dirty = true;
        pending = Forecast.Pending("Calculating…");
    }

    internal static void Reset() => Invalidate();

    // The timer invokes this for every settled combat state, independently of hover.
    internal static void Prepare(CombatState combat)
    {
        if (!dirty) return;
        pending = PrepareCore(combat);
    }

    // Hover only reveals the latest cached result; it never starts simulation work.
    internal static Forecast Get(Creature creature)
    {
        if (dirty) return pending;
        if (computation?.IsCompletedSuccessfully != true) return pending;
        var result = computation.Result;
        LogResult(result);
        return Forecast.FromResult(result, creature);
    }

    private static Forecast PrepareCore(CombatState combat)
    {
        var run = RunManager.Instance;
        diagnosticContext = Describe(combat);
        if (!SimulationProtocol.SupportsGameModule(typeof(CombatState).Module.ModuleVersionId.ToString()))
        {
            dirty = false;
            return DiagnosticPending("Unsupported game version");
        }
        if (combat.Players.Count != 1)
        {
            dirty = false;
            return DiagnosticPending("Multiplayer not supported");
        }
        if (run.ActionExecutor.IsRunning || CombatManager.Instance.IsStarting)
            return Forecast.Pending("Waiting for action…");

        var mods = ModManager.GetLoadedMods().ToArray();
        if (mods.Any(m => m.manifest?.id is not ("BaseLib" or MainFile.ModId)))
        {
            dirty = false;
            return DiagnosticPending("Unsupported mod combination");
        }

        string stamp = SimulationProtocol.Stamp(combat.RunState, combat);
        dirty = false;
        if (replayField.GetValue(run.CombatReplayWriter) is not CombatReplay replay)
            return DiagnosticPending("Combat replay unavailable");

        // Only immutable bytes cross the process boundary; the live combat is never mutated.
        var request = new SimulationRequest(SimulationProtocol.Pack(replay), stamp, ModelIdSerializationCache.Hash,
            typeof(CombatState).Module.ModuleVersionId.ToString(), mods.Any(m => m.manifest?.id == "BaseLib"));
        computation = session == null
            ? Task.FromResult(Unavailable("Simulator unavailable"))
            : session.Simulate(JsonSerializer.Serialize(request));
        return Forecast.Pending("Calculating…");
    }

    private static Forecast DiagnosticPending(string status)
    {
        LogFailure(status);
        return Forecast.Pending(status);
    }

    private static void LogResult(SimulationResult result)
    {
        if (result.Verified)
        {
            lastDiagnosticKey = null;
            return;
        }
        LogFailure(result.Status);
    }

    private static void LogFailure(string status)
    {
        string key = status + "|" + diagnosticContext;
        if (key == lastDiagnosticKey) return;
        lastDiagnosticKey = key;
        GD.PrintErr($"{MainFile.ModId}: forecast unavailable: {status} | {diagnosticContext}");
    }

    private static string Describe(CombatState combat)
    {
        string players = string.Join(", ", combat.Players.Select(player =>
            $"{player.Creature.CurrentHp}/{player.Creature.MaxHp} HP, {player.Creature.Block} block"));
        string enemies = string.Join(", ", combat.Enemies.Select(enemy =>
            $"{enemy.Monster?.Id.ToString() ?? "unknown"}:{enemy.Monster?.NextMove.Id ?? "unknown"}:"
            + $"{enemy.CurrentHp}/{enemy.MaxHp} HP"));
        return $"round={combat.RoundNumber}, side={combat.CurrentSide}, players=[{players}], enemies=[{enemies}]";
    }

    private sealed class WorkerSession : IDisposable
    {
        private readonly string worker;
        private readonly string data;
        private readonly bool baseLib;
        private readonly SemaphoreSlim requestGate = new(1, 1);
        private readonly Task<bool> ready;
        private Process? process;
        private Task<string>? errorDrain;
        private string? startupError;
        private bool disposed;

        internal WorkerSession(string worker, string data, bool baseLib)
        {
            this.worker = worker;
            this.data = data;
            this.baseLib = baseLib;
            // Model loading and JIT priming happen while the game is still opening.
            ready = Task.Run(Start);
        }

        private async Task<bool> Start()
        {
            if (!File.Exists(worker)) return false;
            try
            {
                string dotnet = SimulationProtocol.ResolveDotnetHost();
                var info = new ProcessStartInfo(dotnet)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                info.ArgumentList.Add(worker);
                info.ArgumentList.Add(data);
                info.ArgumentList.Add("--server");
                if (baseLib) info.ArgumentList.Add("--baselib");
                process = Process.Start(info);
                if (process == null) return false;
                errorDrain = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                string? response = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (response == "READY") return true;
                startupError = response == null ? await ReadWorkerError() : $"Unexpected startup response: {response}";
                return false;
            }
            catch (Exception ex) { startupError = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        internal async Task<SimulationResult> Simulate(string request)
        {
            await requestGate.WaitAsync();
            try
            {
                if (disposed || !await ready || process == null || process.HasExited)
                    return Unavailable(WithWorkerError("Simulator unavailable (.NET 9 required)"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
                string? json = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (json == null) return Unavailable("Simulator stopped");
                var response = JsonSerializer.Deserialize<SimulationServerResponse>(json);
                return response?.Result ?? Unavailable("Invalid simulator response");
            }
            catch (OperationCanceledException) { return Unavailable("Simulation timed out"); }
            catch (Exception ex) { return Unavailable($"Simulator unavailable: {ex.GetType().Name}: {ex.Message}"); }
            finally { requestGate.Release(); }
        }

        private string WithWorkerError(string status)
        {
            string? details = startupError;
            if (string.IsNullOrWhiteSpace(details) && errorDrain?.IsCompletedSuccessfully == true)
                details = errorDrain.Result;
            if (string.IsNullOrWhiteSpace(details)) return status;
            details = details.Trim();
            if (details.Length > 2000) details = details[..2000] + "…";
            return status + " | worker: " + details;
        }

        private async Task<string?> ReadWorkerError()
        {
            if (errorDrain == null) return null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                return await errorDrain.WaitAsync(timeout.Token);
            }
            catch (Exception) { return null; }
        }

        public void Dispose()
        {
            disposed = true;
            try { process?.StandardInput.Close(); } catch (Exception) { }
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception) { }
            process?.Dispose();
            requestGate.Dispose();
            _ = errorDrain;
        }
    }

    private static SimulationResult Unavailable(string status) => new(false, 0, 0, [], status);
}
