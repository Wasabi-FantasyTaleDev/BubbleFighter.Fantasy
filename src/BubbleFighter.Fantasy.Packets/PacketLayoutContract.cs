using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Packets;

/// <summary>
/// Bind-time checks that a build's packet table states what a binding consumes. A binding casts each field it
/// reads and sizes each field it writes, so a table that keeps a field's name but changes its type must fail when
/// the row binds, not on the first packet.
/// </summary>
public static class PacketLayoutContract
{
    /// <summary>
    /// Requires <paramref name="structure"/> to state every listed field with exactly the listed type (the type
    /// string as the table writes it, for example <c>u32</c>, <c>wstr</c>, <c>RelayEndpoint</c>,
    /// <c>when&lt;mask&amp;0x01,f32&gt;</c>). Other fields may exist. Throws <see cref="ConfigException"/> naming
    /// the consumer, build, structure, field, the needed type and the stated type.
    /// </summary>
    /// <param name="tables">The loaded packet tables.</param>
    /// <param name="build">The build whose layout is checked.</param>
    /// <param name="structure">The structure the binding consumes.</param>
    /// <param name="consumer">A short plain name for the binding, used in the failure message.</param>
    /// <param name="fields">The consumed fields and the exact types the binding casts or sizes them as.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fields"/> is empty.</exception>
    /// <exception cref="ConfigException">Thrown for a missing field or a different type.</exception>
    public static void RequireFields(PacketTables tables, string build, string structure, string consumer,
        params (string Name, string Type)[] fields)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrEmpty(build);
        ArgumentException.ThrowIfNullOrEmpty(structure);
        ArgumentException.ThrowIfNullOrEmpty(consumer);
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length == 0)
            throw new ArgumentException("A layout contract needs at least one field; a binding with no consumed field asks for nothing.", nameof(fields));
        var resolved = tables.Resolve(build, structure);
        foreach (var (name, type) in fields)
        {
            var field = resolved.FirstOrDefault(candidate => candidate.Name == name);
            if (field is null)
                throw new ConfigException(
                    $"{consumer} needs {structure}.{name} as {type} for '{build}', but the table does not state it.");
            if (field.Type != type)
                throw new ConfigException(
                    $"{consumer} needs {structure}.{name} as {type} for '{build}', but the table states {field.Type}.");
        }
    }

    /// <summary>
    /// Requires the structure's whole layout, as <see cref="Signature"/> prints it, to equal one of
    /// <paramref name="allowed"/>. For bindings that accept a small fixed set of shapes.
    /// </summary>
    /// <param name="tables">The loaded packet tables.</param>
    /// <param name="build">The build whose layout is checked.</param>
    /// <param name="structure">The structure the binding consumes.</param>
    /// <param name="consumer">A short plain name for the binding, used in the failure message.</param>
    /// <param name="allowed">The exact signatures the binding can encode.</param>
    /// <exception cref="ConfigException">Thrown when the layout matches none of them.</exception>
    public static void RequireSignature(PacketTables tables, string build, string structure, string consumer,
        params string[] allowed)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(allowed);
        var actual = Signature(tables, build, structure);
        if (!allowed.Contains(actual, StringComparer.Ordinal))
            throw new ConfigException($"{consumer} cannot use the {structure} layout of '{build}': {actual}.");
    }

    /// <summary>Requires the structure to state no field (the client reads no body).</summary>
    /// <param name="tables">The loaded packet tables.</param>
    /// <param name="build">The build whose layout is checked.</param>
    /// <param name="structure">The structure the binding consumes.</param>
    /// <param name="consumer">A short plain name for the binding, used in the failure message.</param>
    /// <exception cref="ConfigException">Thrown when the layout states at least one field.</exception>
    public static void RequireEmpty(PacketTables tables, string build, string structure, string consumer)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var actual = Signature(tables, build, structure);
        if (actual.Length != 0)
            throw new ConfigException($"{consumer} needs an empty {structure} for '{build}', but the table states {actual}.");
    }

    /// <summary>The layout as <c>name:type</c> pairs joined by commas, in table order.</summary>
    /// <param name="tables">The loaded packet tables.</param>
    /// <param name="build">The build whose layout is printed.</param>
    /// <param name="structure">The structure to print.</param>
    /// <returns>The signature, for example <c>result:u32,nickname:wstr</c>.</returns>
    public static string Signature(PacketTables tables, string build, string structure)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return string.Join(",", tables.Resolve(build, structure).Select(field => $"{field.Name}:{field.Type}"));
    }
}
