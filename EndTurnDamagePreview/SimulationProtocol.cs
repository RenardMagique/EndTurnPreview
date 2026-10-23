using System.Text.Json;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models;

namespace EndTurnDamagePreview;

public sealed record SimulationRequest(string Replay, string Baseline, uint ModelHash, string GameModule, bool BaseLib);
public sealed record SimulatedPetState(int Index, string ModelId, int Hp, int Block, bool Alive);
public sealed record SimulationResult(bool Verified, int Hp, int Block, SimulatedPetState[] Pets, string Status);
public sealed record SimulationServerResponse(SimulationResult Result, long ElapsedMs);

public static class SimulationProtocol
{
    // Fail closed after game updates until the engine compatibility tests are rerun.
    private static readonly HashSet<string> supportedGameModules = new(StringComparer.OrdinalIgnoreCase)
    {
        // Slay the Spire 2 v0.107.1, validated on Windows x64. The Linux build is
        // accepted only when it ships this identical managed game module.
        "97f10687-c306-4798-ab75-8b9f23f34dfb"
    };

    public static bool SupportsGameModule(string moduleVersionId) => supportedGameModules.Contains(moduleVersionId);
    public static IReadOnlyCollection<string> SupportedGameModules => supportedGameModules;

    public static string ResolveDotnetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (IsExecutableFile(hostPath)) return hostPath!;

        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string rootedHost = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(rootedHost)) return rootedHost;
        }

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                string standardHost = Path.Combine(programFiles, "dotnet", "dotnet.exe");
                if (File.Exists(standardHost)) return standardHost;
            }
        }

        // ProcessStartInfo resolves this final fallback through PATH on every supported OS.
        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private static bool IsExecutableFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    public static string Pack<T>(T value) where T : IPacketSerializable
    {
        var writer = new PacketWriter();
        writer.Write(value);
        return Convert.ToBase64String(writer.Buffer, 0, writer.BytePosition);
    }

    public static string Stamp(IRunState run, CombatState combat)
    {
        // The game's network snapshot includes cards, powers, relics and gameplay RNG counters.
        // Add the round and monster moves, which the network snapshot does not contain.
        return JsonSerializer.Serialize(new
        {
            State = Pack(NetFullCombatState.FromRun(run, null)),
            combat.RoundNumber,
            combat.CurrentSide,
            Moves = combat.Enemies.Select(e => new { Id = e.Monster?.Id.ToString(), Move = e.Monster?.NextMove.Id }).ToArray(),
            Models = combat.Creatures.SelectMany(c => c.Powers.Cast<AbstractModel>().Concat(c.Monster == null ? [] : new AbstractModel[] { c.Monster }))
                .Concat(run.Players.SelectMany(p => p.Relics))
                .Select(ModelScalars).ToArray()
        });
    }

    private static object ModelScalars(AbstractModel model)
    {
        var values = new SortedDictionary<string, object?>();
        for (Type? type = model.GetType(); type != null && type != typeof(AbstractModel)
            && type != typeof(RelicModel) && type != typeof(PowerModel) && type != typeof(MonsterModel); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(decimal) || field.FieldType == typeof(string))
                    values[type.FullName + "." + field.Name] = field.GetValue(model);
        return new { Id = model.Id.ToString(), Values = values };
    }
}
