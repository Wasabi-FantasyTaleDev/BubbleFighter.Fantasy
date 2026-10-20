using BubbleFighter.Fantasy.Contracts.Settings;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Stores scoped key maps in the player's atomic, schema-versioned save snapshot.</summary>
internal sealed class SqlitePlayerSettings(SqliteDatabase database) : IPlayerSettingsRepository
{
    public CustomizedKeyLimits Limits => database.Options.CustomizedKeys;

    public ValueTask<IReadOnlyList<CustomizedKey>> ReadKeysAsync(long userNum, string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CustomizedKeyLimits.ValidateIdentity(userNum, scope);
        return new(database.Read(db =>
        {
            var state = db.State(userNum);
            return (IReadOnlyList<CustomizedKey>)(state is not null && state.CustomizedKeys.TryGetValue(scope, out var keys)
                ? Limits.Copy(keys) : []);
        }, ct));
    }

    public ValueTask ReplaceKeysAsync(long userNum, string scope, IReadOnlyList<CustomizedKey> mappings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CustomizedKeyLimits.ValidateIdentity(userNum, scope);
        var copy = Limits.Copy(mappings);
        database.Write((db, _) =>
        {
            var state = db.RequireState(userNum);
            if (copy.Length == 0) state.CustomizedKeys.Remove(scope);
            else
            {
                if (!state.CustomizedKeys.ContainsKey(scope) && state.CustomizedKeys.Count >= Limits.MaxScopes)
                    throw new InvalidOperationException("The player's customized-key scope capacity has been reached.");
                state.CustomizedKeys[scope] = copy;
            }
            db.Save(userNum, state);
            return true;
        }, ct);
        return ValueTask.CompletedTask;
    }
}
