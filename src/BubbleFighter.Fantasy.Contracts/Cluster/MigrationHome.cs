namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>The first login node and native channel retained across shared-account room visits.</summary>
/// <param name="NodeId">The original node, resolved again before returning.</param>
/// <param name="Channel">The original native channel preserved by the client's migration path.</param>
public sealed record MigrationHome(string NodeId, int Channel);

/// <summary>Resolves a current destination lease without granting account authority or a room seat.</summary>
public interface IMigrationTargetDirectory
{
    /// <summary>Returns an eligible shared-account destination for this source and build, or null when unavailable.</summary>
    ValueTask<MigrationTarget?> ResolveAsync(string nodeId, string clientBuild, CancellationToken cancellationToken);
}

/// <summary>An authenticated routing observation that must still be rechecked during migration preparation.</summary>
/// <param name="NodeId">The destination's configured identity.</param>
/// <param name="Epoch">Its current node lease generation.</param>
public sealed record MigrationTarget(string NodeId, long Epoch);
