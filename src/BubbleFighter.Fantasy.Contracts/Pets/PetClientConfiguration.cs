using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Pets;

/// <summary>Immutable pet settings that must agree with the client's cooldown and mileage displays.</summary>
public sealed record PetClientConfiguration
{
    /// <summary>Creates a client configuration with an explicit mileage maximum and feeding cooldown.</summary>
    /// <param name="mileageDisplayMaximum">The positive ordinary mileage balance displayed as a full meter.</param>
    /// <param name="feedCooldownSeconds">The nonnegative normal-food cooldown in seconds; zero disables the wait.</param>
    public PetClientConfiguration(int mileageDisplayMaximum, int feedCooldownSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mileageDisplayMaximum);
        ArgumentOutOfRangeException.ThrowIfNegative(feedCooldownSeconds);
        MileageDisplayMaximum = mileageDisplayMaximum;
        FeedCooldownSeconds = feedCooldownSeconds;
    }

    /// <summary>Gets the positive ordinary mileage balance displayed as a full native meter.</summary>
    public int MileageDisplayMaximum { get; }

    /// <summary>Gets the explicit normal-food cooldown in seconds, before the dialect converts its native units.</summary>
    public int FeedCooldownSeconds { get; }
}

/// <summary>Requests one coherent pet configuration when a dialect prepares a native configuration response.</summary>
/// <param name="Protocol">The client protocol whose active Pets row owns the settings.</param>
public sealed record PetClientConfigurationRequested(string Protocol) : IEvent;

/// <summary>Validates that an active dialect can deliver both pet settings through its audited configuration route.</summary>
public interface IPetClientConfigurationSink
{
    /// <summary>Gets the protocol whose configuration request route this sink serves.</summary>
    string Protocol { get; }

    /// <summary>Rejects settings that this dialect cannot encode and publish through its active configuration route.</summary>
    /// <param name="configuration">The complete settings the Pets row intends to publish.</param>
    void Validate(PetClientConfiguration configuration);
}
