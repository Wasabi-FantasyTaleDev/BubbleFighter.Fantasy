using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Sessions;

/// <summary>
/// Keeps an account live on one client build at a time across a server that runs several builds. Accounts are
/// shared by every build, but each build's lobby only sees its own players, so a login on one build cannot tell
/// on its own that the account is playing on another. Each build's login claim runs under this server-wide lock,
/// which hands it the account's live sessions on the other builds; the lobby then refuses the new login or closes
/// those sessions, by its own <c>duplicate_login</c> policy.
/// </summary>
public interface IAccountBuildLock
{
    /// <summary>
    /// Runs one login's claim on an account while no other build can claim it. A session counts as live on another
    /// build when its connection is open, its profile is not <paramref name="buildId"/>, and its
    /// <see cref="IPlayerSession"/> names the account.
    /// </summary>
    /// <typeparam name="T">The claim's result.</typeparam>
    /// <param name="userNumber">The account logging in.</param>
    /// <param name="buildId">The client build the login arrived on.</param>
    /// <param name="claim">The build's own claim, given the account's live sessions on other builds.</param>
    /// <returns>What <paramref name="claim"/> returned.</returns>
    T Claim<T>(int userNumber, string buildId, Func<IReadOnlyList<ISession>, T> claim);
}
