using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Loads every shipped profile's opcode table through the runtime loader, so a malformed or
/// duplicated entry fails the suite instead of failing the server when that build starts.
/// </summary>
public sealed class ProfileOpcodeTablesLoadTests
{
    /// <summary>Every <c>profiles/*/opcodes.yaml</c> in the repository.</summary>
    public static TheoryData<string> Profiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(ProfilesDirectory(), "opcodes.yaml", SearchOption.AllDirectories))
            data.Add(Path.GetFileName(Path.GetDirectoryName(file)!));
        return data;
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void OpcodeTableLoads(string profile)
    {
        var path = Path.Combine(ProfilesDirectory(), profile, "opcodes.yaml");
        var table = OpcodeTableLoader.Load(path, new ClientProfile(profile, profile.Split('-')[0], 0, 0, 0, profile));
        Assert.NotNull(table);
    }

    private static string ProfilesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "profiles");
            if (File.Exists(Path.Combine(candidate, "kr-812", "opcodes.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository profiles directory was not found.");
    }
}
