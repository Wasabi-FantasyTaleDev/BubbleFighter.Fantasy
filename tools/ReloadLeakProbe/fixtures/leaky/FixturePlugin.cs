namespace ReloadLeakProbe.Fixtures.Leaky;

/// <summary>
/// Leaky fixture revision, used only as the positive control of the leak probe. Each generation
/// parks its own instance and its activation payload in a static table, so a context that is kept
/// forever keeps one more object graph per generation alive.
/// </summary>
public sealed class FixturePlugin
{
    /// <summary>Size of the retained activation payload, shared with the pure variant.</summary>
    public const int PayloadBytes = 8192;

    private static readonly List<object> Retained = [];

    /// <summary>Creates one revision instance.</summary>
    public FixturePlugin() => Revision = 1;

    /// <summary>Gets the revision number this build bakes into every instance.</summary>
    public int Revision { get; }

    /// <summary>
    /// Runs this revision's activation work and keeps both the payload and this instance in the
    /// static table, which makes the generation reachable for as long as its context lives.
    /// </summary>
    /// <returns>The number of bytes the activation touched.</returns>
    public int Activate()
    {
        var payload = new byte[PayloadBytes];
        payload[0] = 1;
        Retained.Add(this);
        Retained.Add(payload);
        return payload.Length + Revision;
    }

    /// <summary>Gets the number of objects this context has retained so far.</summary>
    public static int RetainedCount => Retained.Count;
}
