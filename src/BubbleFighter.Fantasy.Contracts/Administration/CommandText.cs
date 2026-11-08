namespace BubbleFighter.Fantasy.Contracts.Administration;

/// <summary>Shares command trigger normalization and usage presentation across text entry points.</summary>
public static class CommandText
{
    /// <summary>Removes one optional trigger character; all three accepted characters have identical meaning.</summary>
    /// <param name="text">The input line, including any surrounding whitespace.</param>
    /// <param name="requirePrefix">Whether ordinary text must remain chat rather than a command.</param>
    /// <param name="content">The trimmed command name and arguments without their trigger character.</param>
    /// <returns>Whether a nonempty command line was admitted.</returns>
    public static bool TryNormalize(string text, bool requirePrefix, out string content)
    {
        var span = text.AsSpan().Trim();
        content = string.Empty;
        if (span.IsEmpty) return false;
        if (span[0] is '/' or '!' or '\uFF01') span = span[1..].TrimStart();
        else if (requirePrefix) return false;
        if (span.IsEmpty) return false;
        content = span.ToString();
        return true;
    }

    /// <summary>Chooses a display trigger from the initiating entry point, independently of the trigger typed.</summary>
    /// <param name="caller">The transport-established caller, including the original initiator.</param>
    /// <returns>An exclamation mark in game chat, otherwise a slash.</returns>
    public static string DisplayPrefix(CommandCaller caller) => caller.Actor.Kind == PrincipalKind.Game ? "!" : "/";

    /// <summary>Formats the registered argument schema using the entry point's preferred display trigger.</summary>
    /// <param name="caller">The transport-established caller.</param>
    /// <param name="definition">The canonical command name and positional argument schema.</param>
    /// <returns>A usage line with required arguments in angle brackets and optional arguments in square brackets.</returns>
    public static string Usage(CommandCaller caller, CommandDefinition definition) =>
        "Usage: " + DisplayPrefix(caller) + definition.Name + string.Concat(definition.Arguments.Select(argument =>
            argument.Optional ? " [" + argument.Name + "]" : " <" + argument.Name + ">"));
}
