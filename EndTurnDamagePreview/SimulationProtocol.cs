using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

    public static string DescribeStampMismatch(string expected, string actual)
    {
        try
        {
            using var expectedDocument = JsonDocument.Parse(expected);
            using var actualDocument = JsonDocument.Parse(actual);
            string[] sections = ["State", "RoundNumber", "CurrentSide", "Moves", "Models"];
            var differences = new List<string>();
            foreach (string section in sections)
            {
                bool hasExpected = expectedDocument.RootElement.TryGetProperty(section, out var expectedValue);
                bool hasActual = actualDocument.RootElement.TryGetProperty(section, out var actualValue);
                string expectedText = hasExpected ? expectedValue.GetRawText() : "<missing>";
                string actualText = hasActual ? actualValue.GetRawText() : "<missing>";
                if (!string.Equals(expectedText, actualText, StringComparison.Ordinal))
                {
                    string detail = section switch
                    {
                        "State" when hasExpected && hasActual => DescribeNetworkState(expectedValue, actualValue),
                        "Moves" or "Models" when hasExpected && hasActual => DescribeArrayDifference(section, expectedValue, actualValue),
                        _ => $"{Fingerprint(expectedText)}->{Fingerprint(actualText)}"
                    };
                    differences.Add($"{section} {detail}");
                }
            }
            return differences.Count == 0 ? "serialized representation differs" : string.Join(", ", differences);
        }
        catch (JsonException)
        {
            return $"invalid baseline {Fingerprint(expected)}->{Fingerprint(actual)}";
        }
    }

    private static string Fingerprint(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 6);
    }

    private static string DescribeNetworkState(JsonElement expected, JsonElement actual)
    {
        try
        {
            static NetFullCombatState Decode(string packed)
            {
                var reader = new PacketReader();
                reader.Reset(Convert.FromBase64String(packed));
                return reader.Read<NetFullCombatState>();
            }
            string[] expectedLines = Decode(expected.GetString()!).ToString().Split('\n');
            string[] actualLines = Decode(actual.GetString()!).ToString().Split('\n');
            int count = Math.Max(expectedLines.Length, actualLines.Length);
            for (int i = 0; i < count; i++)
            {
                string expectedLine = i < expectedLines.Length ? expectedLines[i].Trim() : "<missing>";
                string actualLine = i < actualLines.Length ? actualLines[i].Trim() : "<missing>";
                if (expectedLine != actualLine) return Shorten($"line {i + 1}: {expectedLine} -> {actualLine}");
            }
        }
        catch (Exception) { }
        return $"{Fingerprint(expected.GetRawText())}->{Fingerprint(actual.GetRawText())}";
    }

    private static string DescribeArrayDifference(string section, JsonElement expected, JsonElement actual)
    {
        int expectedCount = expected.GetArrayLength();
        int actualCount = actual.GetArrayLength();
        if (expectedCount != actualCount) return $"count {expectedCount}->{actualCount}";
        for (int i = 0; i < expectedCount; i++)
        {
            string expectedItem = expected[i].GetRawText();
            string actualItem = actual[i].GetRawText();
            if (expectedItem == actualItem) continue;
            string id = expected[i].TryGetProperty("Id", out var idValue) ? idValue.ToString() : i.ToString();
            if (section == "Models" && expected[i].TryGetProperty("Values", out var expectedValues)
                && actual[i].TryGetProperty("Values", out var actualValues))
            {
                foreach (var property in expectedValues.EnumerateObject())
                    if (!actualValues.TryGetProperty(property.Name, out var actualProperty)
                        || property.Value.GetRawText() != actualProperty.GetRawText())
                        return Shorten($"{id}.{property.Name}: {property.Value} -> {(actualProperty.ValueKind == JsonValueKind.Undefined ? "<missing>" : actualProperty.ToString())}");
            }
            return $"{id} {Fingerprint(expectedItem)}->{Fingerprint(actualItem)}";
        }
        return "serialized representation differs";
    }

    private static string Shorten(string value) => value.Length <= 300 ? value : value[..300] + "…";

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
