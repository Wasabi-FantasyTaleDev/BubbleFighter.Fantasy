using BubbleFighter.Fantasy.Contracts.Inventory;
using Dapper;

namespace BubbleFighter.Fantasy.Lobby.Data;

public sealed partial class MySqlPlayerRepository
{
    /// <inheritdoc />
    public bool SupportsAtomicPaintApplication => true;

    /// <inheritdoc />
    public async ValueTask<bool> ApplyPaintWithItemAsync(long userNum, int itemId, ushort colorIndex, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        ArgumentOutOfRangeException.ThrowIfZero(colorIndex);
        await using var db = await connections.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT color_index FROM players WHERE user_num=@userNum FOR UPDATE",
            new { userNum }, tx, cancellationToken: ct)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Paint application requires an existing player.");
        if (current == colorIndex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stack = await db.QuerySingleOrDefaultAsync<PaintStack>(new CommandDefinition(
            "SELECT ordinal AS Ordinal,quantity AS Quantity FROM inventory_slots "
            + "WHERE user_num=@userNum AND bag=0 AND item_id=@itemId AND quantity>0 "
            + "AND lifetime_kind<>@life AND (lifetime_kind<>2 OR expires_at_unix_ms>@now) ORDER BY ordinal DESC LIMIT 1 FOR UPDATE",
            new { userNum, itemId, now, life = (int)InventoryLifetimeKind.Life }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (stack is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }
        var remaining = checked(stack.Quantity - 1);
        var mirror = await db.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT ordinal FROM inventory_slots WHERE user_num=@userNum AND bag=1 AND item_id=@itemId "
            + "ORDER BY ordinal DESC LIMIT 1 FOR UPDATE", new { userNum, itemId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        foreach (var target in mirror is { } ordinal ? new[] { (Bag: 0, stack.Ordinal), (Bag: 1, Ordinal: ordinal) }
                     : new[] { (Bag: 0, stack.Ordinal) })
        {
            var count = await db.ExecuteAsync(new CommandDefinition(remaining == 0
                    ? "DELETE FROM inventory_slots WHERE user_num=@userNum AND bag=@bag AND ordinal=@ordinal"
                    : "UPDATE inventory_slots SET quantity=@remaining,updated_at=UTC_TIMESTAMP() "
                      + "WHERE user_num=@userNum AND bag=@bag AND ordinal=@ordinal",
                new { userNum, bag = target.Bag, ordinal = target.Ordinal, remaining }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (count != 1) throw new InvalidOperationException("The locked paint stack changed during application.");
        }
        if (await db.ExecuteAsync(new CommandDefinition(
                "UPDATE players SET color_index=@colorIndex WHERE user_num=@userNum",
                new { userNum, colorIndex }, tx, cancellationToken: ct)).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The locked player changed during paint application.");
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private sealed class PaintStack
    {
        public int Ordinal { get; init; }
        public int Quantity { get; init; }
    }
}
