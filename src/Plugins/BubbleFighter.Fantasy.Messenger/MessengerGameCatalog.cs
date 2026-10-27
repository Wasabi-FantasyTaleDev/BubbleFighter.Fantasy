using BubbleFighter.Fantasy.Contracts.Messenger;

namespace BubbleFighter.Fantasy.Messenger;

/// <summary>
/// The configured games and their regional aliases. Friendships are stored under a game's own code, while a native
/// client treats a record as its own game only when the code equals the one it initialized with: cn-324 sub_7621A0
/// drops the others from the list and tw-454 sub_B0C890 counts and marks them as another game. Every record a session
/// reads is therefore shown under that session's code when it belongs to the same game.
/// </summary>
internal sealed class MessengerGameCatalog
{
    private readonly Dictionary<uint, MessengerGame> _byCode = [];

    internal MessengerGameCatalog(IEnumerable<MessengerGameOptions> games)
    {
        var list = new List<MessengerGame>();
        foreach (var options in games)
        {
            var game = new MessengerGame((uint)options.Code, options.FullName, options.ShortName);
            list.Add(game);
            foreach (var code in options.Aliases.Prepend(options.Code)) _byCode.Add((uint)code, game);
        }
        Games = list;
    }

    /// <summary>Gets the catalog entries under their stored codes.</summary>
    internal IReadOnlyList<MessengerGame> Games { get; }

    /// <summary>Returns the stored code of the game <paramref name="code"/> belongs to; a code outside the catalog is its own game.</summary>
    internal uint Canonical(uint code) => _byCode.TryGetValue(code, out var game) ? game.Code : code;

    /// <summary>Returns <paramref name="code"/> as a session showing <paramref name="viewer"/> must see it.</summary>
    internal uint Present(uint code, uint viewer) => Canonical(code) == Canonical(viewer) ? viewer : code;

    /// <summary>Finds the entry of the game a code or alias belongs to.</summary>
    internal MessengerGame? Find(uint code) => _byCode.GetValueOrDefault(code);
}
