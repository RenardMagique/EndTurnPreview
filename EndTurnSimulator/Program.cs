using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Loader;
using System.Text.Json;
using EndTurnDamagePreview;

namespace EndTurnSimulator;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !Directory.Exists(args[0])) return 2;
        string data = Path.GetFullPath(args[0]);
        SimulationMod[] mods = ReadMods(args);
        string[] modDirectories = mods.Select(mod => mod.AssemblyPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetDirectoryName)
            .Where(path => path != null).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray();
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(data, name.Name + ".dll");
            if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            path = modDirectories.Select(directory => Path.Combine(directory, name.Name + ".dll")).FirstOrDefault(File.Exists);
            return path == null ? null : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        };
        try
        {
            Pump.Run(async () =>
            {
                if (args.Contains("--diagnostic")) Console.WriteLine(Engine.Diagnostic(data, args.Contains("--baselib"), mods));
                else if (args.Contains("--selftest")) await Engine.SelfTest(args.FirstOrDefault(a => a.StartsWith("--case="))?[7..] ?? "turns");
                else if (args.Contains("--fixture")) Console.WriteLine(await Engine.Fixture(data, args.Contains("--baselib"), mods: mods));
                else if (args.Contains("--server"))
                {
                    await Engine.Warm(data, args.Contains("--baselib"), mods);
                    Console.WriteLine("READY");
                    string? input;
                    while ((input = Console.ReadLine()) != null)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var result = await Engine.Simulate(input, data);
                        Console.WriteLine(JsonSerializer.Serialize(new ServerResponse(result,
                            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds)));
                    }
                }
                else
                {
                    var input = Console.ReadLine() ?? throw new InvalidDataException("Missing request");
                    var result = await Engine.Simulate(input, data);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                }
            });
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static SimulationMod[] ReadMods(string[] args)
    {
        string? encoded = args.FirstOrDefault(argument => argument.StartsWith("--mods=", StringComparison.Ordinal))?[7..];
        if (string.IsNullOrWhiteSpace(encoded)) return [];
        return JsonSerializer.Deserialize<SimulationMod[]>(Convert.FromBase64String(encoded)) ?? [];
    }

    private sealed record ServerResponse(EndTurnDamagePreview.SimulationResult Result, long ElapsedMs);
}

// Game continuations run on one thread, as they do on Godot's main thread.
internal sealed class Pump : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
    internal static void Run(Func<Task> action)
    {
        using var pump = new PumpScope();
        Exception? error = null;
        pump.Context.Post(async _ =>
        {
            try { await action(); } catch (Exception ex) { error = ex; }
            finally { pump.Context.queue.CompleteAdding(); }
        }, null);
        foreach (var (callback, state) in pump.Context.queue.GetConsumingEnumerable()) callback(state);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class PumpScope : IDisposable
    {
        internal readonly Pump Context = new();
        private readonly SynchronizationContext? previous = Current;
        internal PumpScope() => SetSynchronizationContext(Context);
        public void Dispose() { SetSynchronizationContext(previous); Context.queue.Dispose(); }
    }
}
