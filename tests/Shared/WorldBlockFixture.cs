using System.Numerics;
using System.Text;

namespace BubbleFighter.Fantasy.Tests;

/// <summary>Builds synthetic version-2 helper records without distributing client map assets.</summary>
internal static class WorldBlockFixture
{
    internal static byte[] Create(params (string Name, Vector3 Position)[] helpers)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(2);
        writer.Write(10000f);
        foreach (var (name, point) in helpers)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            writer.Write(checked((byte)bytes.Length));
            writer.Write(bytes);
            writer.Write(1);
            writer.Write((byte)0);
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
            writer.Write(0.25f);
            writer.Write(-0.5f);
            writer.Write(1.5f);
        }
        return stream.ToArray();
    }

    internal static byte[] HelperWithProperties(string name, Vector3 point, string properties)
    {
        var simple = Create((name, point));
        var propertyBytes = Encoding.Latin1.GetBytes(properties);
        var propertyOffset = 8 + 1 + Encoding.ASCII.GetByteCount(name) + 4;
        return [.. simple[..propertyOffset], checked((byte)propertyBytes.Length), .. propertyBytes, .. simple[(propertyOffset + 1)..]];
    }

    internal static byte[] MeshWithEmbeddedHelper()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        writer.Write(2); writer.Write(5000f);
        WriteString(writer, "mesh"); writer.Write(0); WriteString(writer, "mesh properties");
        writer.Write(new byte[12]); writer.Write(2); writer.Write(0); writer.Write(1);
        WriteString(writer, "No Texture");
        WriteString(writer, "texture.tga"); writer.Write(0); WriteString(writer, "material");
        writer.Write(new byte[36]);
        var embedded = Create(("#sp_star_fake", new(900, 900, 900)))[8..];
        writer.Write(checked((byte)embedded.Length)); writer.Write(embedded);
        writer.Write(1); WriteString(writer, "extra.tga"); writer.Write(new byte[36]);
        writer.Write(2); writer.Write(0); writer.Write(new byte[32]);
        writer.Write(3); writer.Write(1); writer.Write(new byte[24]);
        writer.Write(new byte[16]);
        writer.Write(Create(("#spawn_real", new(1, 2, 3)))[8..]);
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        writer.Write(checked((byte)bytes.Length)); writer.Write(bytes);
    }
}
