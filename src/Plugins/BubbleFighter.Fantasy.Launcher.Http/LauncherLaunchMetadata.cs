using System.Text.Json;

namespace BubbleFighter.Fantasy.Launcher.Http;

internal sealed record LauncherLaunchMetadata(string? ReleaseVersion, string? PatchVersion,
    string? LauncherLanguage, string? NativeLanguage, string? ClientLanguage)
{
    internal static LauncherLaunchMetadata Read(JsonElement body) => new(
        Label(body, "release_version"), Label(body, "patch_version"),
        Language(body, "launcher_language"), Language(body, "native_language"), Language(body, "client_language"));

    private static string? Label(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        var label = value.GetString()!;
        return label.Length <= 64 && !string.IsNullOrWhiteSpace(label) && !label.Any(char.IsControl)
            ? label : throw new InvalidDataException();
    }

    private static string? Language(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        return value.GetString() is { } language && language is "zh-CN" or "zh-TW" or "ko" or "en" or "id" or "vi"
            ? language : throw new InvalidDataException();
    }
}
