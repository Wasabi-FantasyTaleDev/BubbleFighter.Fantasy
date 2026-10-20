using System.Text.Json;

namespace BubbleFighter.Fantasy.Scenario.Tests.Harness;

/// <summary>
/// Sanitized C2S test payloads, keyed by opcode in fixture order. Scenarios reuse fixed
/// inputs except where a reply supplies an identifier or a test changes a known field.
/// </summary>
internal sealed class SimFixtures
{
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxPerOpcode = 64;
    private readonly Dictionary<ushort, List<byte[]>> _byOpcode = [];

    /// <summary>
    /// Loads C2S inputs from a sanitized test fixture (<c>frames.jsonl</c>: one JSON object per line
    /// with <c>type</c>, <c>dir</c>, <c>opcode</c>, <c>payload</c> hex). At most 64 payloads are kept per opcode.
    /// </summary>
    public static SimFixtures Load(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new ArgumentException($"Fixture file '{path}' exceeds {MaxFileBytes >> 20} MiB.");
        var fixtures = new SimFixtures();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() != "frame")
                continue;
            if (root.GetProperty("dir").GetString() != "c2s")
                continue;
            fixtures.Add(root.GetProperty("opcode").GetUInt16(),
                Convert.FromHexString(root.GetProperty("payload").GetString() ?? ""));
        }
        return fixtures;
    }

    /// <summary>Adds one fixture payload for <paramref name="opcode"/>.</summary>
    public void Add(ushort opcode, byte[] payload)
    {
        if (!_byOpcode.TryGetValue(opcode, out var list))
            _byOpcode[opcode] = list = [];
        if (list.Count < MaxPerOpcode)
            list.Add(payload);
    }

    /// <summary>The first fixture payload for <paramref name="opcode"/>.</summary>
    public byte[] First(ushort opcode) => Get(opcode)[0];

    /// <summary>The last kept fixture payload for <paramref name="opcode"/>.</summary>
    public byte[] Last(ushort opcode) => Get(opcode)[^1];

    /// <summary>Whether any payload for <paramref name="opcode"/> is known.</summary>
    public bool Has(ushort opcode) => _byOpcode.ContainsKey(opcode);

    private List<byte[]> Get(ushort opcode) =>
        _byOpcode.TryGetValue(opcode, out var list)
            ? list
            : throw new InvalidOperationException($"The fixtures have no C2S {opcode} payload.");
}
