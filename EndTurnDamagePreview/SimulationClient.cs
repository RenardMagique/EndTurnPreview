using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private static string? lastRandomDiagnosticKey;
    private static long computationStartedAt;
    private static bool slowComputationLogged;
    private static SimulationMod[] loadedMods = [];
    private static string compatibilityContext = "compatibility state unavailable";
    private static string workerPath = "";
    private static string dataPath = "";

    internal static void StartWarm()
    {
        dataPath = Path.GetDirectoryName(typeof(CombatState).Assembly.Location)!;
        workerPath = Path.Combine(Path.GetDirectoryName(typeof(MainFile).Assembly.Location)!, "simulator", "EndTurnSimulator.dll");
        RefreshSession(force: true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => session?.Dispose();
    }

    private static void RefreshSession(bool force = false)
    {
        var currentMods = ModManager.GetLoadedMods().Where(mod => mod.manifest?.id != MainFile.ModId).Select(DescribeMod).ToArray();
        if (!force && SimulationProtocol.ModSignature(currentMods) == SimulationProtocol.ModSignature(loadedMods)) return;
        session?.Dispose();
        loadedMods = currentMods;
        bool baseLib = loadedMods.Any(mod => mod.Id == "BaseLib");
        compatibilityContext = $"runtime={RuntimeInformation.RuntimeIdentifier}, "
            + $"gameAssembly={typeof(CombatState).Assembly.GetName().Version}, "
            + $"gameModule={typeof(CombatState).Module.ModuleVersionId}, models={ModelIdSerializationCache.Hash}, "
            + $"mods=[{SimulationProtocol.ModSignature(loadedMods)}]";
        GD.Print($"{MainFile.ModId} {MainFile.Version} compatibility: {compatibilityContext}");
        session = new WorkerSession(workerPath, dataPath, baseLib, loadedMods);
        Invalidate();
    }

    private static SimulationMod DescribeMod(Mod mod)
    {
        string id = mod.manifest?.id ?? mod.assembly?.GetName().Name ?? "unknown";
        string version = mod.manifest?.version ?? mod.assembly?.GetName().Version?.ToString() ?? "unknown";
        string? path = null;
        string? module = null;
        try
        {
            if (mod.assembly != null)
            {
                path = mod.assembly.Location;
                module = mod.assembly.ManifestModule.ModuleVersionId.ToString();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{MainFile.ModId}: could not inspect mod assembly {id}: {ex.GetType().Name}: {ex.Message}");
        }
        return new(id, version, mod.manifest?.affectsGameplay ?? false, path, module);
    }

    internal static void Invalidate()
    {
        computation = null;
        dirty = true;
        pending = Forecast.Pending("Calculating…");
        computationStartedAt = 0;
        slowComputationLogged = false;
    }

    internal static void Reset() => Invalidate();

    // The timer invokes this for every settled combat state, independently of hover.
    internal static void Prepare(CombatState combat)
    {
        RefreshSession();
        if (!dirty) return;
        pending = PrepareCore(combat);
    }

    // Hover only reveals the latest cached result; it never starts simulation work.
    internal static Forecast Get(Creature creature)
    {
        if (dirty) return pending;
        if (computation == null)
        {
            LogFailure("Simulation was not started");
            return pending;
        }
        if (computation.IsFaulted || computation.IsCanceled)
        {
            string failure = computation.Exception?.GetBaseException().Message
                ?? (computation.IsCanceled ? "Simulation task was canceled" : "Simulation task failed");
            LogFailure(failure);
            Invalidate();
            return pending;
        }
        if (!computation.IsCompletedSuccessfully)
        {
            if (!slowComputationLogged && computationStartedAt != 0
                && Stopwatch.GetElapsedTime(computationStartedAt) >= TimeSpan.FromSeconds(1))
            {
                slowComputationLogged = true;
                LogFailure("Simulation still pending after 1 second");
            }
            return pending;
        }
        var result = computation.Result;
        LogResult(result);
        return Forecast.FromResult(result, creature);
    }

    private static Forecast PrepareCore(CombatState combat)
    {
        var run = RunManager.Instance;
        diagnosticContext = Describe(combat);
        if (combat.Players.Count != 1)
        {
            dirty = false;
            return DiagnosticPending("Multiplayer not supported");
        }
        if (run.ActionExecutor.IsRunning || CombatManager.Instance.IsStarting)
            return Forecast.Pending("Waiting for action…");

        string stamp = SimulationProtocol.Stamp(combat.RunState, combat);
        dirty = false;
        if (replayField.GetValue(run.CombatReplayWriter) is not CombatReplay replay)
            return DiagnosticPending("Combat replay unavailable");

        // Only immutable bytes cross the process boundary; the live combat is never mutated.
        var request = new SimulationRequest(SimulationProtocol.Pack(replay), stamp, ModelIdSerializationCache.Hash,
            typeof(CombatState).Module.ModuleVersionId.ToString(), loadedMods.Any(mod => mod.Id == "BaseLib"), loadedMods);
        computation = session == null
            ? Task.FromResult(Unavailable("Simulator unavailable"))
            : session.Simulate(JsonSerializer.Serialize(request));
        computationStartedAt = Stopwatch.GetTimestamp();
        slowComputationLogged = false;
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
            if (result.Randomized)
            {
                string sources = string.Join(",", result.RandomSources);
                string key = $"{result.MinHp}-{result.MaxHp}|{result.MinBlock}-{result.MaxBlock}|{sources}|{diagnosticContext}";
                if (key != lastRandomDiagnosticKey)
                {
                    lastRandomDiagnosticKey = key;
                    GD.Print($"{MainFile.ModId}: hidden-randomness forecast: "
                        + $"playerHp={result.MinHp}-{result.MaxHp}, block={result.MinBlock}-{result.MaxBlock}, "
                        + $"sources=[{sources}], branches={result.Branches} | {diagnosticContext}");
                }
            }
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
            $"{player.Creature.CurrentHp}/{player.Creature.MaxHp} HP, {player.Creature.Block} block, "
            + $"powers=[{DescribePowers(player.Creature)}], "
            + $"hand=[{string.Join(",", player.PlayerCombatState?.Hand.Cards.Select(card => card.Id.ToString()) ?? [])}]"));
        string enemies = string.Join(", ", combat.Enemies.Select(enemy =>
            $"{enemy.Monster?.Id.ToString() ?? "unknown"}:{enemy.Monster?.NextMove.Id ?? "unknown"}:"
            + $"{enemy.CurrentHp}/{enemy.MaxHp} HP:powers=[{DescribePowers(enemy)}]"));
        return $"{compatibilityContext}, round={combat.RoundNumber}, side={combat.CurrentSide}, "
            + $"players=[{players}], enemies=[{enemies}]";
    }

    private static string DescribePowers(Creature creature) => string.Join(",",
        creature.Powers.Select(power => $"{power.Id}:{power.Amount}"));

    private sealed class WorkerSession : IDisposable
    {
        private readonly string worker;
        private readonly string data;
        private readonly bool baseLib;
        private readonly SimulationMod[] mods;
        private readonly SemaphoreSlim requestGate = new(1, 1);
        private Task<bool>? ready;
        private Process? process;
        private Task<string>? errorDrain;
        private string? startupError;
        private string? lastWorkerError;
        private bool disposed;

        internal WorkerSession(string worker, string data, bool baseLib, SimulationMod[] mods)
        {
            this.worker = worker;
            this.data = data;
            this.baseLib = baseLib;
            this.mods = mods;
            // Model loading and JIT priming happen while the game is still opening.
            ready = Task.Run(Start);
        }

        private async Task<bool> Start()
        {
            if (!File.Exists(worker)) return false;
            try
            {
                startupError = null;
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
                info.ArgumentList.Add("--mods=" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(mods)));
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
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (disposed || !await EnsureStarted())
                        return Unavailable(WithWorkerError("Simulator could not start"));
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await process!.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
                        string? json = await process.StandardOutput.ReadLineAsync(timeout.Token);
                        if (json != null)
                        {
                            var response = JsonSerializer.Deserialize<SimulationServerResponse>(json);
                            return response?.Result ?? Unavailable("Invalid simulator response");
                        }
                        await Restart();
                    }
                    catch (OperationCanceledException)
                    {
                        await Restart();
                        return Unavailable("Simulation timed out; simulator restarted");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                    {
                        lastWorkerError = ex.GetType().Name + ": " + ex.Message;
                        await Restart();
                    }
                }
                return Unavailable(WithWorkerError("Simulator stopped twice while processing this state"));
            }
            catch (Exception ex) { return Unavailable($"Simulator unavailable: {ex.GetType().Name}: {ex.Message}"); }
            finally { requestGate.Release(); }
        }

        private async Task<bool> EnsureStarted()
        {
            if (ready != null)
            {
                bool started = await ready;
                if (started && process is { HasExited: false }) return true;
            }
            await Restart();
            return ready != null && await ready && process is { HasExited: false };
        }

        private async Task Restart()
        {
            await StopAndCaptureError();
            if (!disposed) ready = Task.Run(Start);
        }

        private async Task StopAndCaptureError()
        {
            try { process?.StandardInput.Close(); } catch (Exception) { }
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception) { }
            if (errorDrain != null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    string error = await errorDrain.WaitAsync(timeout.Token);
                    if (!string.IsNullOrWhiteSpace(error)) lastWorkerError = error;
                }
                catch (Exception) { }
            }
            process?.Dispose();
            process = null;
            errorDrain = null;
            ready = null;
        }

        private string WithWorkerError(string status)
        {
            string? details = startupError ?? lastWorkerError;
            if (string.IsNullOrWhiteSpace(details) && errorDrain?.IsCompletedSuccessfully == true) details = errorDrain.Result;
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
            _ = errorDrain;
        }
    }

    private static SimulationResult Unavailable(string status) =>
        new(false, 0, 0, [], status, false, 0, 0, 0, 0, [], 0);
}
