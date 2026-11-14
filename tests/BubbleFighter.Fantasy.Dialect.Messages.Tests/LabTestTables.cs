using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Loads native laboratory tables and isolates deliberate configuration faults from the shared fixtures.</summary>
internal static class LabTestTables
{
    internal static IOpcodeTable Opcodes(string build)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        return OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
    }

    internal static PacketTables Modified(string fileName, string before, string after)
    {
        var directory = TestTempDirectories.ForTest("bff-laboratory-validation");
        foreach (var file in Directory.GetFiles(TestBuilds.PacketsDirectory(), "*.yaml"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var path = Path.Combine(directory, fileName);
        var source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(before, source, StringComparison.Ordinal);
        File.WriteAllText(path, source.Replace(before, after, StringComparison.Ordinal));
        return PacketTables.Load(directory);
    }
}
