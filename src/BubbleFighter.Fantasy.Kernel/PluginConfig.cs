using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Kernel;

/// <summary>
/// Binds a row's configuration mapping onto a POCO. YAML keys are snake_case and map to
/// PascalCase properties. Binding reports every conversion failure, unknown key, and
/// validation failure in a single <see cref="ConfigException"/>.
/// </summary>
public sealed class PluginConfig : IPluginConfig
{
    private readonly PluginRow _row;

    /// <summary>Captures a bounded, read-only row snapshot for all subsequent binds.</summary>
    /// <param name="row">The row whose <see cref="PluginRow.Config"/> is bound.</param>
    public PluginConfig(PluginRow row) : this(row, captureSnapshot: true) { }

    internal PluginConfig(PluginRow row, bool captureSnapshot) =>
        _row = captureSnapshot ? PluginRowSnapshot.Capture(row) : row;

    /// <inheritdoc />
    public T Bind<T>() where T : class, new()
    {
        var errors = new List<string>();
        var instance = new T();
        if (_row.Config is { } map)
            BindObject(map, instance, path: "", errors);
        ValidateGraph(instance, path: "", errors, []);
        if (instance is IValidatableOptions validatable)
            foreach (var result in validatable.Validate())
                errors.Add(result.ErrorMessage ?? "validation failed");
        if (errors.Count > 0)
            throw new ConfigException(_row.Id, errors);
        return instance;
    }

    private static void BindObject(IReadOnlyDictionary<string, object?> map, object target, string path, List<string> errors)
    {
        var properties = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in map)
        {
            var propertyName = SnakeToPascal(key);
            var location = path.Length == 0 ? key : $"{path}.{key}";
            if (!properties.TryGetValue(propertyName, out var property))
            {
                errors.Add($"unknown key '{location}'");
                continue;
            }
            try
            {
                var converted = Convert(value, property.PropertyType, location, errors);
                if (converted is not Unconvertible)
                    property.SetValue(target, converted);
            }
            catch (Exception ex) when (ex is not ConfigException)
            {
                errors.Add($"key '{location}': {ex.Message}");
            }
        }
    }

    private sealed class Unconvertible { }

    private static readonly Unconvertible Failed = new();

    private static object? Convert(object? value, Type target, string location, List<string> errors)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        if (value is null)
        {
            if (underlying is not null || !target.IsValueType) return null;
            errors.Add($"key '{location}': null is not valid for {target.Name}");
            return Failed;
        }

        var effective = underlying ?? target;

        if (effective.IsEnum)
        {
            if (value is string enumName && Enum.TryParse(effective, enumName, ignoreCase: true, out var parsed))
                return parsed;
            errors.Add($"key '{location}': '{value}' is not a valid {effective.Name}");
            return Failed;
        }

        if (effective == typeof(string))
            return value.ToString();

        if (effective == typeof(bool))
        {
            if (value is bool b) return b;
            if (value is string bs && bool.TryParse(bs, out var bb)) return bb;
            errors.Add($"key '{location}': '{value}' is not a boolean");
            return Failed;
        }

        if (IsNumeric(effective))
        {
            try
            {
                return value is string s
                    ? System.Convert.ChangeType(s, effective, CultureInfo.InvariantCulture)
                    : System.Convert.ChangeType(value, effective, CultureInfo.InvariantCulture);
            }
            catch
            {
                errors.Add($"key '{location}': '{value}' is not a valid {effective.Name}");
                return Failed;
            }
        }

        if (effective.IsArray)
        {
            var element = effective.GetElementType()!;
            if (value is not IList items)
            {
                errors.Add($"key '{location}': expected a list");
                return Failed;
            }
            var array = Array.CreateInstance(element, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var item = Convert(items[i], element, $"{location}[{i}]", errors);
                if (item is Unconvertible) return Failed;
                array.SetValue(item, i);
            }
            return array;
        }

        if (effective.IsGenericType && effective.GetGenericTypeDefinition() == typeof(List<>))
        {
            var element = effective.GetGenericArguments()[0];
            if (value is not IList items)
            {
                errors.Add($"key '{location}': expected a list");
                return Failed;
            }
            var list = (IList)Activator.CreateInstance(effective)!;
            for (var i = 0; i < items.Count; i++)
            {
                var item = Convert(items[i], element, $"{location}[{i}]", errors);
                if (item is Unconvertible) return Failed;
                list.Add(item);
            }
            return list;
        }

        // A read-only collection interface (IReadOnlyList<T>, IReadOnlyCollection<T>)
        // or any other collection interface with an addable List<T> behind it. The
        // options POCOs expose their sequences as IReadOnlyList<T> so callers
        // cannot mutate them, and a sequence that only bound to List<T> would make
        // those rows unbindable.
        if (effective.IsInterface && effective.IsGenericType
            && IsCollectionInterface(effective.GetGenericTypeDefinition())
            && typeof(IList).IsAssignableFrom(effective) is false)
        {
            var element = effective.GetGenericArguments()[0];
            if (value is not IList items)
            {
                errors.Add($"key '{location}': expected a list");
                return Failed;
            }
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            for (var i = 0; i < items.Count; i++)
            {
                var item = Convert(items[i], element, $"{location}[{i}]", errors);
                if (item is Unconvertible) return Failed;
                list.Add(item);
            }
            return list;
        }

        if (effective.IsClass && value is IReadOnlyDictionary<string, object?> nested)
        {
            var instance = Activator.CreateInstance(effective)!;
            BindObject(nested, instance, location, errors);
            return instance;
        }

        errors.Add($"key '{location}': cannot bind a value of type '{value.GetType().Name}' to '{effective.Name}'");
        return Failed;
    }

    private static bool IsNumeric(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    /// <summary>
    /// Reports whether a generic type definition is a collection interface a
    /// <see cref="List{T}"/> satisfies, which is what lets a YAML sequence bind
    /// to a read-only sequence property.
    /// </summary>
    /// <param name="definition">The open generic definition to test.</param>
    private static bool IsCollectionInterface(Type definition) =>
        definition == typeof(IReadOnlyList<>)
        || definition == typeof(IReadOnlyCollection<>)
        || definition == typeof(IEnumerable<>)
        || definition == typeof(ICollection<>)
        || definition == typeof(IList<>);

    private static void ValidateGraph(object instance, string path, List<string> errors, HashSet<object> visited)
    {
        if (instance is null || instance.GetType().IsValueType || instance is string)
            return;
        if (!visited.Add(instance))
            return;

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        foreach (var result in results)
        {
            var member = result.MemberNames.FirstOrDefault();
            errors.Add(path.Length == 0 ? result.ErrorMessage ?? "validation failed" : $"{path}: {result.ErrorMessage} ({member})");
        }

        if (instance is IEnumerable enumerable and not IDictionary)
        {
            var i = 0;
            foreach (var item in enumerable)
                ValidateGraph(item, $"{path}[{i++}]", errors, visited);
            return;
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;
            var value = property.GetValue(instance);
            if (value is null)
                continue;
            var name = path.Length == 0 ? PascalToSnake(property.Name) : $"{path}.{PascalToSnake(property.Name)}";
            if (value is IEnumerable and not string)
                ValidateGraph(value, name, errors, visited);
            else if (property.PropertyType.IsClass && property.PropertyType != typeof(string))
                ValidateGraph(value, name, errors, visited);
        }
    }

    private static string SnakeToPascal(string key)
    {
        var builder = new StringBuilder(key.Length);
        var upper = true;
        foreach (var c in key)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }
            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return builder.ToString();
    }

    private static string PascalToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
