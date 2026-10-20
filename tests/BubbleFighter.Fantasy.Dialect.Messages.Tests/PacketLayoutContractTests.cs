using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Behaviour of <see cref="PacketLayoutContract"/>: one test per check and failure message, against a small packet
/// folder written for the test and against a copy of the repository tables with one field's type changed.
/// </summary>
public sealed class PacketLayoutContractTests
{
    private const string Build = "kr-812";

    /// <summary>The signature prints the table order as name:type pairs.</summary>
    [Fact]
    public void SignaturePrintsTableOrder()
    {
        var tables = SmallTables();
        Assert.Equal("first:u32,second:wstr", PacketLayoutContract.Signature(tables, Build, "Probe"));
        Assert.Equal("", PacketLayoutContract.Signature(tables, Build, "EmptyProbe"));
    }

    /// <summary>A matching field of the exact type passes; other fields of the structure may exist.</summary>
    [Fact]
    public void RequireFieldsAcceptsTheStatedType()
    {
        var tables = SmallTables();
        PacketLayoutContract.RequireFields(tables, Build, "Probe", "Probe", ("first", "u32"), ("second", "wstr"));
    }

    /// <summary>A missing field names the consumer, build, structure, field and needed type.</summary>
    [Fact]
    public void RequireFieldsNamesAMissingField()
    {
        var tables = SmallTables();
        var error = Assert.Throws<ConfigException>(() =>
            PacketLayoutContract.RequireFields(tables, Build, "Probe", "Probe", ("missing", "u32")));
        Assert.Equal($"Probe needs Probe.missing as u32 for '{Build}', but the table does not state it.", error.Message);
    }

    /// <summary>A field whose name matches but whose type differs names both types.</summary>
    [Fact]
    public void RequireFieldsNamesTheStatedType()
    {
        var edited = PacketTableEdits.WithFieldType("LoginReply", "result", "u32", "wstr");
        var tables = PacketTables.Load(edited.FullName);
        var error = Assert.Throws<ConfigException>(() =>
            PacketLayoutContract.RequireFields(tables, Build, "LoginReply", "Login", ("result", "u32")));
        Assert.Equal($"Login needs LoginReply.result as u32 for '{Build}', but the table states wstr.", error.Message);
    }

    /// <summary>Zero fields is a caller bug, not a silent pass.</summary>
    [Fact]
    public void RequireFieldsRejectsAnEmptyList()
    {
        var tables = SmallTables();
        Assert.Throws<ArgumentException>(() => PacketLayoutContract.RequireFields(tables, Build, "Probe", "Probe"));
    }

    /// <summary>A signature check accepts one of the stated shapes and names the actual layout otherwise.</summary>
    [Fact]
    public void RequireSignatureAcceptsOnlyTheListedShapes()
    {
        var tables = SmallTables();
        PacketLayoutContract.RequireSignature(tables, Build, "Probe", "Probe", "first:u32,second:wstr");
        var error = Assert.Throws<ConfigException>(() =>
            PacketLayoutContract.RequireSignature(tables, Build, "Probe", "Probe", "first:wstr,second:wstr"));
        Assert.Equal($"Probe cannot use the Probe layout of '{Build}': first:u32,second:wstr.", error.Message);
    }

    /// <summary>The empty check accepts a structure with no fields and reports the actual layout otherwise.</summary>
    [Fact]
    public void RequireEmptyAcceptsOnlyAnEmptyStructure()
    {
        var tables = SmallTables();
        PacketLayoutContract.RequireEmpty(tables, Build, "EmptyProbe", "Probe");
        var error = Assert.Throws<ConfigException>(() => PacketLayoutContract.RequireEmpty(tables, Build, "Probe", "Probe"));
        Assert.Equal($"Probe needs an empty Probe for '{Build}', but the table states first:u32,second:wstr.", error.Message);
    }

    /// <summary>A structure the tables do not have keeps the loader's own exception.</summary>
    [Fact]
    public void MissingStructureKeepsTheTablesException()
    {
        var tables = SmallTables();
        Assert.Throws<ConfigException>(() => PacketLayoutContract.Signature(tables, Build, "NoSuchStructure"));
    }

    private static PacketTables SmallTables()
    {
        var directory = new DirectoryInfo(TestTempDirectories.ForTest("bff-layout-contract"));
        File.WriteAllText(Path.Combine(directory.FullName, "builds.yaml"),
            "version: 1\nbuilds:\n  kr-812: {}\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory.FullName, "probe.yaml"),
            """
            version: 1
            structs:
              Probe:
                - first: u32
                  offset: 0
                - second: wstr
                  offset: 4
              EmptyProbe: []
            """, new UTF8Encoding(false));
        return PacketTables.Load(directory.FullName);
    }
}
