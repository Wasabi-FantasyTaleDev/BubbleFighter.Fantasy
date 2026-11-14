using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Keeps successful practice identities in the owned SQLite save.</summary>
internal sealed class SqliteTutorialPractice(SqliteDatabase database) : ITutorialPracticeStore
{
    public ValueTask<bool> HasClearedAsync(TutorialPracticeKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ValueTask.FromResult(database.Read(db => Convert.ToInt64(db.RawScalar(
            "SELECT EXISTS(SELECT 1 FROM tutorial_practice_clears WHERE profile=$profile AND user_num=$user AND map_key=$map)",
            null, ("$profile", key.Profile), ("$user", key.UserNum), ("$map", key.MapKey))) != 0, ct));
    }

    public ValueTask<bool> RecordClearAsync(TutorialPracticeKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ValueTask.FromResult(database.Write((db, transaction) => db.RawExecute(
            "INSERT OR IGNORE INTO tutorial_practice_clears(profile,user_num,map_key) VALUES($profile,$user,$map)",
            transaction, ("$profile", key.Profile), ("$user", key.UserNum), ("$map", key.MapKey)) == 1, ct));
    }
}
