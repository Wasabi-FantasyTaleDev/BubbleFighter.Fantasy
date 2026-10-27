using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Resolves server-assigned preset names without changing human nicknames or shared room state.</summary>
internal static class RoomNames
{
    internal static string Resolve(string nickname, string key, ISession? recipient, IGameText? texts)
    {
        if (key.Length == 0)
            return nickname;
        if (recipient is null || texts is null)
            throw new ConfigException("Localized AI names require a recipient and the localization service.");
        return texts.Format(recipient, key);
    }
}
