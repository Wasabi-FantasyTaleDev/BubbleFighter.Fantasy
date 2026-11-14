using System.Globalization;
using System.Text;

namespace BubbleFighter.Fantasy.Contracts.ClientData;

/// <summary>
/// How one catalog a row builds from client data is written to and read from the build's compiled client data file.
/// The format is the row's own compact binary of exactly what it keeps in memory; it is not a client format. A
/// value that is written and read back must equal the value that was written: each implementation has a round-trip
/// equality test, and the store also checks that reading and writing again yields the same bytes before it keeps them.
/// </summary>
/// <typeparam name="T">The catalog as the row keeps it.</typeparam>
public interface ICompiledCatalog<T>
{
    /// <summary>Gets the catalog's section name in the file, for example <c>wear-locations</c>.</summary>
    string Name { get; }

    /// <summary>Writes the catalog. Equal catalogs must write equal bytes.</summary>
    /// <param name="value">The catalog.</param>
    /// <param name="output">The section's stream.</param>
    void Write(T value, Stream output);

    /// <summary>
    /// Reads a catalog <see cref="Write"/> wrote, consuming the whole section. Throws <see cref="InvalidDataException"/>
    /// when the bytes are not a catalog; the store then builds the catalog from the client data again.
    /// </summary>
    /// <param name="input">The section's stream, positioned at its start and ending at its end.</param>
    /// <returns>The catalog.</returns>
    T Read(Stream input);
}

/// <summary>
/// A client data store that keeps the catalogs rows build from it in one file per build, so a later start reads the
/// catalogs instead of unpacking and parsing the client's workbooks again. The file is a cache and never a source:
/// it is used only while the archives, the loose files the catalog read, the row's own inputs and the server's
/// assemblies are the ones it was written from, and deleting it changes nothing but the time a start takes.
/// </summary>
public interface ICompiledClientData
{
    /// <summary>
    /// Returns the catalog from the compiled file when it is current, and otherwise builds it with
    /// <paramref name="compile"/> and keeps it for the next start.
    /// </summary>
    /// <typeparam name="T">The catalog as the row keeps it.</typeparam>
    /// <param name="catalog">How the catalog is written and read.</param>
    /// <param name="inputs">Everything the catalog depends on besides the client data read through the store that
    /// <paramref name="compile"/> receives: row options, file paths with their sizes and times. Build it with
    /// <see cref="CompiledInputs"/>. A catalog compiled from other inputs is never returned.</param>
    /// <param name="compile">Builds the catalog. It must read client data only through the store it is given, which
    /// records the files it asks for; a loose file among them that later changes makes the kept catalog stale.</param>
    /// <returns>The catalog.</returns>
    T Load<T>(ICompiledCatalog<T> catalog, string inputs, Func<IClientDataStore, T> compile);
}

/// <summary>Loads a catalog through a store's compiled client data when the store has it.</summary>
public static class CompiledClientData
{
    /// <summary>
    /// Returns the catalog from the store's compiled file when the store keeps one and it is current; otherwise
    /// builds it with <paramref name="compile"/>. A store without compiled data (a test fake, a remote store) builds
    /// it every time.
    /// </summary>
    /// <typeparam name="T">The catalog as the row keeps it.</typeparam>
    /// <param name="store">The build's client data store.</param>
    /// <param name="catalog">How the catalog is written and read.</param>
    /// <param name="inputs">Everything the catalog depends on besides the client data; see
    /// <see cref="ICompiledClientData.Load"/>.</param>
    /// <param name="compile">Builds the catalog from the store it is given.</param>
    /// <returns>The catalog.</returns>
    public static T Load<T>(IClientDataStore store, ICompiledCatalog<T> catalog, string inputs, Func<IClientDataStore, T> compile)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(compile);
        return store is ICompiledClientData compiled ? compiled.Load(catalog, inputs, compile) : compile(store);
    }
}

/// <summary>
/// A compiled catalog written by the contracts' binary serializer. <typeparamref name="T"/> is a type the serializer
/// knows: a primitive, a string, an array or collection of such, or a type the row declares packable. The host loads
/// one copy of the serializer and shares it with every plugin, so a row's packed types work here.
/// </summary>
/// <typeparam name="T">The catalog as the row keeps it, which is also what is written.</typeparam>
/// <param name="name">The catalog's section name.</param>
public sealed class PackedCatalog<T>(string name) : ICompiledCatalog<T>
{
    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public void Write(T value, Stream output) => PackedBytes.Write(value, output);

    /// <inheritdoc />
    public T Read(Stream input) => PackedBytes.Read<T>(input);
}

/// <summary>
/// A compiled catalog whose in-memory form is not what is written: <typeparamref name="TPacked"/> is the packable
/// form, and the two functions convert. Reading goes through <c>unpack</c>, so whatever the catalog's own
/// constructor checks is checked on every start, also for a catalog that came from the file.
/// </summary>
/// <typeparam name="T">The catalog as the row keeps it.</typeparam>
/// <typeparam name="TPacked">The form that is written.</typeparam>
/// <param name="name">The catalog's section name.</param>
/// <param name="pack">Converts the catalog to its written form. Equal catalogs must give equal forms, in one order.</param>
/// <param name="unpack">Builds the catalog from its written form; throws when the form is not a valid catalog.</param>
public sealed class PackedCatalog<T, TPacked>(string name, Func<T, TPacked> pack, Func<TPacked, T> unpack) : ICompiledCatalog<T>
{
    /// <inheritdoc />
    public string Name { get; } = name;

    /// <inheritdoc />
    public void Write(T value, Stream output) => PackedBytes.Write(pack(value), output);

    /// <inheritdoc />
    public T Read(Stream input) => unpack(PackedBytes.Read<TPacked>(input));
}

internal static class PackedBytes
{
    internal static void Write<T>(T value, Stream output)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        MemoryPack.MemoryPackSerializer.Serialize(buffer, value);
        output.Write(buffer.WrittenSpan);
    }

    // Reads one value and leaves the stream after it, so the store can tell a section that was not read to its end.
    internal static T Read<T>(Stream input)
    {
        T? value = default;
        int consumed;
        if (input is MemoryStream memory && memory.TryGetBuffer(out var segment))
        {
            // The store hands a section over as its own array; the value is read from it without another copy.
            var start = checked((int)memory.Position);
            consumed = MemoryPack.MemoryPackSerializer.Deserialize(segment.AsSpan(start), ref value);
            memory.Position = start + consumed;
        }
        else
        {
            using var rest = new MemoryStream();
            input.CopyTo(rest);
            consumed = MemoryPack.MemoryPackSerializer.Deserialize(rest.GetBuffer().AsSpan(0, checked((int)rest.Length)), ref value);
            if (consumed != rest.Length)
                throw new InvalidDataException("A compiled catalog did not read its whole section.");
        }
        return value ?? throw new InvalidDataException("A compiled catalog section holds no value.");
    }
}

/// <summary>
/// Builds the text that names a compiled catalog's inputs. Each value is written with its name and length, so two
/// different sets of inputs never produce the same text.
/// </summary>
public sealed class CompiledInputs
{
    private readonly StringBuilder _text = new();

    /// <summary>Adds a named text value; null and empty are different inputs.</summary>
    /// <param name="name">The input's name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public CompiledInputs Add(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _text.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name).Append('=');
        if (value is null) _text.Append("null;");
        else _text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        return this;
    }

    /// <summary>Adds a named integer.</summary>
    /// <param name="name">The input's name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public CompiledInputs Add(string name, long value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Adds a named flag.</summary>
    /// <param name="name">The input's name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public CompiledInputs Add(string name, bool value) => Add(name, value ? "true" : "false");

    /// <summary>Adds a named list of text values, in order.</summary>
    /// <param name="name">The input's name.</param>
    /// <param name="values">Its values.</param>
    /// <returns>This builder.</returns>
    public CompiledInputs Add(string name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var index = 0;
        foreach (var value in values)
            Add(name + "[" + index++.ToString(CultureInfo.InvariantCulture) + "]", value);
        return Add(name + ".count", index);
    }

    /// <summary>
    /// Adds a file outside the client data that the catalog reads: its full path, size and last write time, or that
    /// it is absent. A file that is edited therefore gives other inputs.
    /// </summary>
    /// <param name="name">The input's name.</param>
    /// <param name="path">The file's path; empty adds an empty input.</param>
    /// <returns>This builder.</returns>
    public CompiledInputs AddFile(string name, string? path)
    {
        if (string.IsNullOrEmpty(path)) return Add(name, path);
        var file = new FileInfo(path);
        return Add(name, file.Exists
            ? file.FullName + "|" + file.Length.ToString(CultureInfo.InvariantCulture) + "|" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
            : file.FullName + "|absent");
    }

    /// <summary>Returns the inputs as one text.</summary>
    public override string ToString() => _text.ToString();
}
