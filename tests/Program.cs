using DiscordXpBot.Data;
using DiscordXpBot.Services;
using Microsoft.Data.Sqlite;

var databasePath = Path.Combine(
    Path.GetTempPath(),
    $"discord-xp-bot-selftest-{Guid.NewGuid():N}.db");
var legacyDatabasePath = Path.Combine(
    Path.GetTempPath(),
    $"discord-xp-bot-legacy-selftest-{Guid.NewGuid():N}.db");

try
{
    var database = new BotDatabase(databasePath);
    await database.InitializeAsync();

    const ulong guildId = 100;
    const ulong inviterId = 200;
    const ulong memberId = 300;
    var now = DateTimeOffset.UtcNow;

    const ulong deterministicMessageId = 123456789012345678;
    var deterministicXp = MessageXpCalculator.Calculate(deterministicMessageId, 15, 25);
    Assert(
        deterministicXp is >= 15 and <= 25,
        "Die Nachrichten-XP liegen nicht zwischen 15 und 25.");
    Assert(
        deterministicXp == MessageXpCalculator.Calculate(deterministicMessageId, 15, 25),
        "Dieselbe Nachrichten-ID erzeugt nicht denselben XP-Wert.");
    Assert(
        Enumerable.Range(1, 1000)
            .Select(id => MessageXpCalculator.Calculate((ulong)id, 15, 25))
            .Distinct()
            .Count() == 11,
        "Die Formel nutzt nicht den vollständigen Bereich von 15 bis 25.");

    var invite = new InviteRewardRecord(
        Guid.NewGuid().ToString("N"),
        guildId,
        memberId,
        inviterId,
        "self-test",
        now.AddDays(-8),
        500,
        false);

    await database.UpsertInviteAsync(invite);
    var dueInvites = await database.GetDueInvitesAsync(guildId, now.AddDays(-7));
    Assert(dueInvites.Count == 1, "Die fällige Einladung wurde nicht gefunden.");

    var rewarded = await database.MarkInviteRewardGivenAsync(dueInvites[0], now);
    Assert(rewarded, "Der Invite-Reward wurde nicht verbucht.");
    Assert(
        !await database.MarkInviteRewardGivenAsync(dueInvites[0], now),
        "Der gleiche Invite-Reward konnte doppelt verbucht werden.");

    var storedInvite = await database.GetInviteAsync(guildId, memberId);
    Assert(storedInvite is { RewardGiven: true }, "Der Reward-Status wurde nicht gespeichert.");

    var revoked = await database.RevokeInviteAndDeleteAsync(storedInvite!, now.AddMinutes(1));
    Assert(revoked, "Die Einladung wurde beim Leave nicht gelöscht.");
    Assert(
        await database.GetInviteLedgerEntryCountAsync(guildId) == 2,
        "Reward und Rückbuchung wurden nicht dauerhaft im Invite-Ledger gespeichert.");

    var historicalInvite = new InviteRewardRecord(
        Guid.NewGuid().ToString("N"),
        guildId,
        301,
        inviterId,
        "historical",
        now.AddDays(-8),
        475,
        false,
        AutoProcess: false);
    await database.UpsertInviteAsync(historicalInvite);
    Assert(
        (await database.GetDueInvitesAsync(guildId, now.AddDays(-7))).Count == 0,
        "Eine historische Einladung wurde ohne ausdrücklichen Backfill freigegeben.");
    Assert(
        await database.EnableHistoricalInvitesAsync(guildId) == 1,
        "Die historische Einladung wurde nicht durch den Befehl freigeschaltet.");
    Assert(
        (await database.GetDueInvitesAsync(guildId, now.AddDays(-7))).Count == 1,
        "Die freigeschaltete historische Einladung wurde nicht zur Verarbeitung angeboten.");

    const ulong voiceUserId = 400;
    await database.StartVoiceSessionAsync(
        guildId,
        voiceUserId,
        500,
        now.AddMinutes(-4));

    var tooShortReward = await database.RewardVoiceTimeAsync(
        guildId,
        voiceUserId,
        now,
        minXpPerMinute: 1,
        maxXpPerMinute: 3,
        minimumRewardableMinutes: 5,
        deleteSession: false);
    Assert(
        tooShortReward.Xp == 0,
        "Eine Voice-Sitzung unter fünf Minuten wurde fälschlich vergütet.");

    var firstVoiceReward = await database.RewardVoiceTimeAsync(
        guildId,
        voiceUserId,
        now.AddMinutes(1),
        minXpPerMinute: 1,
        maxXpPerMinute: 3,
        minimumRewardableMinutes: 5,
        deleteSession: false);
    Assert(firstVoiceReward.Minutes == 5, "Die ersten fünf Voice-Minuten wurden nicht erkannt.");
    Assert(
        firstVoiceReward.Xp is >= 5 and <= 15,
        "Beim Erreichen der Mindestzeit wurden nicht alle fünf Minuten vergütet.");

    var followUpVoiceReward = await database.RewardVoiceTimeAsync(
        guildId,
        voiceUserId,
        now.AddMinutes(3),
        minXpPerMinute: 1,
        maxXpPerMinute: 3,
        minimumRewardableMinutes: 5,
        deleteSession: false);
    Assert(
        followUpVoiceReward.Minutes == 2 && followUpVoiceReward.Xp is >= 2 and <= 6,
        "Nach erreichter Mindestzeit wurden weitere vollständige Minuten nicht vergütet.");

    const ulong messageUserId = 600;
    const ulong firstMessageId = 700;
    const ulong secondMessageId = 701;
    var firstMessageXp = MessageXpCalculator.Calculate(firstMessageId, 15, 25);
    var secondMessageXp = MessageXpCalculator.Calculate(secondMessageId, 15, 25);
    Assert(
        await database.RegisterMessageXpAsync(
            guildId,
            800,
            firstMessageId,
            messageUserId,
            firstMessageXp,
            now),
        "Die erste Nachricht wurde nicht verbucht.");
    Assert(
        !await database.RegisterMessageXpAsync(
            guildId,
            800,
            firstMessageId,
            messageUserId,
            firstMessageXp,
            now),
        "Dieselbe Nachrichten-ID wurde doppelt verbucht.");
    Assert(
        await database.RegisterMessageXpAsync(
            guildId,
            800,
            secondMessageId,
            messageUserId,
            secondMessageXp,
            now),
        "Die zweite Nachricht wurde nicht verbucht.");

    var messageEntry = (await database.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == messageUserId);
    Assert(
        messageEntry.MessageCount == 2 &&
        messageEntry.MessageXp == firstMessageXp + secondMessageXp &&
        messageEntry.TotalXp == firstMessageXp + secondMessageXp,
        "Nachrichtenanzahl oder interne Nachrichten-XP stimmen nicht.");

    Assert(
        await database.RemoveMessageXpAsync(guildId, firstMessageId),
        "Gelöschte Nachrichten-XP wurden nicht entfernt.");
    messageEntry = (await database.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == messageUserId);
    Assert(
        messageEntry.MessageCount == 1 &&
        messageEntry.MessageXp == secondMessageXp &&
        messageEntry.TotalXp == secondMessageXp,
        "Die XP einer gelöschten Nachricht wurden nicht exakt zurückgenommen.");

    var pendingDispatches = await database.GetPendingXpDispatchesAsync(guildId);
    Assert(
        pendingDispatches.Count == 4,
        "Die MEE6-Versandwarteschlange enthält nicht Reward, Rückbuchung und beide Voice-XP-Blöcke.");
    Assert(
        await database.TryClaimXpDispatchAsync(pendingDispatches[0].Id, now),
        "Ein MEE6-Versandauftrag konnte nicht exklusiv reserviert werden.");
    Assert(
        !await database.TryClaimXpDispatchAsync(pendingDispatches[0].Id, now),
        "Ein MEE6-Versandauftrag konnte doppelt reserviert werden.");
    await database.MarkXpDispatchSentAsync(pendingDispatches[0].Id, 12345, now);

    await CreateLegacyDatabaseAsync(legacyDatabasePath, now);
    var migratedDatabase = new BotDatabase(legacyDatabasePath);
    await migratedDatabase.InitializeAsync();
    var migratedInvite = await migratedDatabase.GetInviteAsync(guildId, memberId);
    Assert(
        migratedInvite is { AutoProcess: false, RewardGiven: true },
        "Ein alter Invite-Datensatz wurde nicht sicher als manueller Backfill migriert.");
    Assert(
        await migratedDatabase.GetInviteLedgerEntryCountAsync(guildId) == 1,
        "Bereits früher vergebene Invite-XP wurden nicht in das dauerhafte Ledger migriert.");
    var migratedInternalXp = (await migratedDatabase.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == inviterId);
    Assert(
        migratedInternalXp.TotalXp == 500,
        "Die früheren XP wurden nicht in das neue interne XP-System migriert.");
    Assert(
        !await TableExistsAsync(legacyDatabasePath, "xp_accounts") &&
        !await TableExistsAsync(legacyDatabasePath, "xp_transactions"),
        "Die alten internen XP-Konten und XP-Transaktionen wurden nicht entfernt.");

    Console.WriteLine(
        "Selbsttest erfolgreich: Nachrichten-XP, internes Ledger, Invite-, Voice- und MEE6-Logik funktionieren.");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (File.Exists(databasePath))
    {
        File.Delete(databasePath);
    }

    if (File.Exists(legacyDatabasePath))
    {
        File.Delete(legacyDatabasePath);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task CreateLegacyDatabaseAsync(string path, DateTimeOffset now)
{
    await using var connection = new SqliteConnection($"Data Source={path}");
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        CREATE TABLE xp_accounts (
            guild_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            xp INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (guild_id, user_id)
        );
        CREATE TABLE xp_transactions (
            id TEXT PRIMARY KEY,
            guild_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            amount INTEGER NOT NULL,
            reason TEXT NOT NULL,
            reference_id TEXT NOT NULL UNIQUE,
            created_at_utc TEXT NOT NULL
        );
        CREATE TABLE invite_rewards (
            id TEXT PRIMARY KEY,
            guild_id TEXT NOT NULL,
            member_id TEXT NOT NULL,
            inviter_id TEXT NOT NULL,
            invite_code TEXT NOT NULL,
            joined_at_utc TEXT NOT NULL,
            reward_xp INTEGER NOT NULL,
            reward_given INTEGER NOT NULL DEFAULT 0,
            rewarded_at_utc TEXT NULL,
            UNIQUE (guild_id, member_id)
        );
        CREATE TABLE voice_sessions (
            session_id TEXT PRIMARY KEY,
            guild_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            channel_id TEXT NOT NULL,
            started_at_utc TEXT NOT NULL,
            UNIQUE (guild_id, user_id)
        );
        INSERT INTO invite_rewards (
            id, guild_id, member_id, inviter_id, invite_code,
            joined_at_utc, reward_xp, reward_given, rewarded_at_utc
        )
        VALUES (
            'legacy-invite', '100', '300', '200', 'legacy-code',
            $joinedAt, 500, 1, $rewardedAt
        );
        INSERT INTO xp_transactions (
            id, guild_id, user_id, amount, reason, reference_id, created_at_utc
        )
        VALUES (
            'legacy-transaction', '100', '200', 500, 'invite-reward',
            'invite-reward:legacy-invite', $rewardedAt
        );
        """;
    command.Parameters.AddWithValue("$joinedAt", now.AddDays(-8).UtcDateTime.ToString("O"));
    command.Parameters.AddWithValue("$rewardedAt", now.UtcDateTime.ToString("O"));
    await command.ExecuteNonQueryAsync();
}

static async Task<bool> TableExistsAsync(string path, string tableName)
{
    await using var connection = new SqliteConnection($"Data Source={path}");
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        SELECT 1
        FROM sqlite_master
        WHERE type = 'table' AND name = $tableName
        LIMIT 1;
        """;
    command.Parameters.AddWithValue("$tableName", tableName);
    return await command.ExecuteScalarAsync() is not null;
}
