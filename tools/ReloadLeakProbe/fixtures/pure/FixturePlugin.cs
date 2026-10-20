namespace ReloadLeakProbe.Fixtures.Pure;

/// <summary>
/// Pure fixture revision. Every generation is built from this same source, so the type keeps an
/// identical signature across reloads, and the type holds no static state: nothing a generation
/// allocates can stay reachable once the probe drops its own reference.
/// </summary>
public sealed class FixturePlugin
{
    /// <summary>Size of the transient activation payload, shared with the leaky variant.</summary>
    public const int PayloadBytes = 8192;

    /// <summary>Creates one revision instance.</summary>
    public FixturePlugin() => Revision = 1;

    /// <summary>Gets the revision number this build bakes into every instance.</summary>
    public int Revision { get; }

    /// <summary>
    /// Runs this revision's activation work and discards everything it allocates, which is the
    /// pure counterpart of the leaky variant's static retention.
    /// </summary>
    /// <returns>The number of bytes the activation touched.</returns>
    public int Activate()
    {
        var payload = new byte[PayloadBytes];
        payload[0] = 1;
        return payload.Length + Revision;
    }
}
