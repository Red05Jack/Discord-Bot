using DiscordXpBot.Persistence;
using DiscordXpBot.Leveling;
using DiscordXpBot.Services;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

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
    Assert(
        !await TableExistsAsync(databasePath, "xp_dispatches"),
        "Die alte XP-Versandwarteschlange wurde in einer neuen Datenbank angelegt.");

    const ulong guildId = 100;
    const ulong inviterId = 200;
    const ulong memberId = 300;
    var now = DateTimeOffset.UtcNow;

    await database.EnsureUserAccountsAsync(guildId, [901, 902]);
    var initialAccounts = await database.GetInternalXpLeaderboardAsync(guildId);
    Assert(
        initialAccounts.Count(entry =>
            (entry.UserId is 901 or 902) &&
            entry.TotalXp == 0 &&
            entry.CurrentLevel == 0 &&
            entry.CurrentLevelProgress == 0) == 2,
        "Neue Benutzer wurden nicht mit Level 0 und 0 XP angelegt.");

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

    Assert(
        LevelCalculator.GetXpForNextLevel(0) == 20,
        "Level 0 benötigt nicht exakt 20 XP bis Level 1.");
    Assert(
        LevelCalculator.GetXpForNextLevel(1) ==
        (int)Math.Round(20.0 * Math.Pow(2, 1.9), MidpointRounding.ToEven),
        "Die Level-Kurve entspricht nicht 20 * (level + 1)^1.9.");
    await using (var rankCard = new RankCardRenderer().Render(
                     new RankCardData("Rank Test", 3, 12, 345, 1000),
                     avatarStream: null))
    {
        var signature = new byte[8];
        Assert(
            await rankCard.ReadAsync(signature) == signature.Length &&
            signature.SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "Die Rank-Karte wurde nicht als gültige PNG-Datei erzeugt.");
    }
    var multiLevelState = LevelCalculator.CalculateLevelFromTotalXp(
        LevelCalculator.GetXpForNextLevel(0) +
        LevelCalculator.GetXpForNextLevel(1) +
        5);
    Assert(
        multiLevelState.Level == 2 && multiLevelState.CurrentLevelProgress == 5,
        "Mehrere Level-Ups aus einer XP-Summe wurden nicht korrekt berechnet.");

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
    Assert(rewarded is { Applied: true }, "Der Invite-Reward wurde nicht verbucht.");
    Assert(
        await database.MarkInviteRewardGivenAsync(dueInvites[0], now) is null,
        "Der gleiche Invite-Reward konnte doppelt verbucht werden.");

    var storedInvite = await database.GetInviteAsync(guildId, memberId);
    Assert(storedInvite is { RewardGiven: true }, "Der Reward-Status wurde nicht gespeichert.");

    var revoked = await database.RevokeInviteAndDeleteAsync(storedInvite!, now.AddMinutes(1));
    Assert(revoked.Deleted, "Die Einladung wurde beim Leave nicht gelöscht.");
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

    const ulong historicalInviterId = 250;
    const string availableInviteCode = "available-code";
    await database.UpsertInviteAsync(new InviteRewardRecord(
        Guid.NewGuid().ToString("N"),
        guildId,
        302,
        historicalInviterId,
        availableInviteCode,
        now,
        500,
        false));
    var firstInviteMemberBackfill = await database.BackfillInviteMembersAsync(
        guildId,
        [
            new HistoricalInviteMember(
                302,
                historicalInviterId,
                availableInviteCode,
                now),
            new HistoricalInviteMember(
                303,
                historicalInviterId,
                availableInviteCode,
                now.AddDays(-8)),
            new HistoricalInviteMember(
                304,
                historicalInviterId,
                availableInviteCode,
                now.AddDays(-2))
        ],
        now.AddDays(-7),
        minXp: 500,
        maxXp: 500,
        now);
    Assert(
        firstInviteMemberBackfill.MatchedMembers == 3 &&
        firstInviteMemberBackfill.AlreadyTrackedMembers == 1 &&
        firstInviteMemberBackfill.ImportedMembers == 2 &&
        firstInviteMemberBackfill.PendingMembers == 1 &&
        firstInviteMemberBackfill.RewardedMembers == 1 &&
        firstInviteMemberBackfill.AwardedXp == 500,
        "Konkrete Invite-Mitglieder wurden nicht korrekt importiert oder nach sieben Tagen vergütet.");
    var duplicateInviteMemberBackfill = await database.BackfillInviteMembersAsync(
        guildId,
        [
            new HistoricalInviteMember(
                302,
                historicalInviterId,
                availableInviteCode,
                now),
            new HistoricalInviteMember(
                303,
                historicalInviterId,
                availableInviteCode,
                now.AddDays(-8)),
            new HistoricalInviteMember(
                304,
                historicalInviterId,
                availableInviteCode,
                now.AddDays(-2))
        ],
        now.AddDays(-7),
        minXp: 500,
        maxXp: 500,
        now.AddMinutes(1));
    var historicalInviterAccount = await database.GetInternalXpAccountAsync(
        guildId,
        historicalInviterId);
    Assert(
        duplicateInviteMemberBackfill.AlreadyTrackedMembers == 3 &&
        duplicateInviteMemberBackfill.ImportedMembers == 0 &&
        historicalInviterAccount.InviteXp == 500,
        "Dieselben konkreten Invite-Mitglieder konnten doppelt vergütet werden.");

    const ulong recoveredInviteMemberId = 305;
    const string recoveredInviteCode = "recovered-code";
    var recoveredJoinedAt = now.AddDays(-10);
    var recoveredIdentity =
        $"{guildId}:{recoveredInviteMemberId}:{recoveredInviteCode}:" +
        $"{recoveredJoinedAt.UtcTicks}";
    var recoveredHash = SHA256.HashData(Encoding.UTF8.GetBytes(recoveredIdentity));
    var recoveredRewardId = Convert.ToHexString(
        recoveredHash.AsSpan(0, 16)).ToLowerInvariant();
    await database.AddXpAsync(
        guildId,
        historicalInviterId,
        500,
        "invite-reward",
        $"invite-reward:{recoveredRewardId}",
        now.AddMinutes(2));
    var recoveredBackfill = await database.BackfillInviteMembersAsync(
        guildId,
        [
            new HistoricalInviteMember(
                recoveredInviteMemberId,
                historicalInviterId,
                recoveredInviteCode,
                recoveredJoinedAt)
        ],
        now.AddDays(-7),
        minXp: 500,
        maxXp: 500,
        now.AddMinutes(3));
    Assert(
        recoveredBackfill.ImportedMembers == 1 &&
        recoveredBackfill.AlreadyTrackedMembers == 1 &&
        recoveredBackfill.RewardedMembers == 0,
        "Ein bereits im XP-Ledger vorhandener Invite hat den Rücklauf nicht idempotent überstanden.");

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
        minXpPerBlock: 5,
        maxXpPerBlock: 15,
        rewardBlockMinutes: 5,
        deleteSession: false);
    Assert(
        tooShortReward.Xp == 0,
        "Eine Voice-Sitzung unter fünf Minuten wurde fälschlich vergütet.");

    var firstVoiceReward = await database.RewardVoiceTimeAsync(
        guildId,
        voiceUserId,
        now.AddMinutes(1),
        minXpPerBlock: 5,
        maxXpPerBlock: 15,
        rewardBlockMinutes: 5,
        deleteSession: false);
    Assert(firstVoiceReward.Minutes == 5, "Die ersten fünf Voice-Minuten wurden nicht erkannt.");
    Assert(
        firstVoiceReward.Xp is >= 5 and <= 15,
        "Beim Erreichen der Mindestzeit wurden nicht alle fünf Minuten vergütet.");

    var followUpVoiceReward = await database.RewardVoiceTimeAsync(
        guildId,
        voiceUserId,
        now.AddMinutes(3),
        minXpPerBlock: 5,
        maxXpPerBlock: 15,
        rewardBlockMinutes: 5,
        deleteSession: false);
    Assert(
        followUpVoiceReward.Minutes == 0 && followUpVoiceReward.Xp == 0,
        "Unvollständige Voice-Blöcke wurden fälschlich vergütet.");

    var secondVoiceReward = await database.RewardVoiceTimeAsync(
        guildId,
        voiceUserId,
        now.AddMinutes(6),
        minXpPerBlock: 5,
        maxXpPerBlock: 15,
        rewardBlockMinutes: 5,
        deleteSession: false);
    Assert(
        secondVoiceReward.Minutes == 5 && secondVoiceReward.Xp is >= 5 and <= 15,
        "Der zweite vollständige 5-Minuten-Block wurde nicht vergütet.");

    var discardedRemainder = await database.RewardVoiceTimeAsync(
        guildId,
        voiceUserId,
        now.AddMinutes(9),
        minXpPerBlock: 5,
        maxXpPerBlock: 15,
        rewardBlockMinutes: 5,
        deleteSession: true);
    Assert(
        discardedRemainder.Minutes == 0 && discardedRemainder.Xp == 0,
        "Die übrigen drei Voice-Minuten sind beim Verlassen nicht verfallen.");

    const ulong synchronizedVoiceUserId = 401;
    await database.StartVoiceSessionAsync(
        guildId,
        synchronizedVoiceUserId,
        501,
        now);
    Assert(
        await database.SynchronizeVoiceSessionsAsync(
            guildId,
            [(synchronizedVoiceUserId, 501)],
            now.AddMinutes(3)) == 1,
        "Die aktive Voice-Sitzung wurde nicht synchronisiert.");
    var synchronizedVoiceReward = await database.RewardVoiceTimeAsync(
        guildId,
        synchronizedVoiceUserId,
        now.AddMinutes(5),
        minXpPerBlock: 5,
        maxXpPerBlock: 5,
        rewardBlockMinutes: 5,
        deleteSession: false);
    Assert(
        synchronizedVoiceReward is { Minutes: 5, Xp: 5 },
        "Die Voice-Synchronisierung hat eine bestehende Sitzung fälschlich zurückgesetzt.");

    const ulong recoveredVoiceUserId = 402;
    Assert(
        await database.EnsureVoiceSessionAsync(
            guildId,
            recoveredVoiceUserId,
            502,
            now),
        "Eine fehlende Voice-Sitzung wurde nicht wiederhergestellt.");
    Assert(
        !await database.EnsureVoiceSessionAsync(
            guildId,
            recoveredVoiceUserId,
            502,
            now.AddMinutes(1)),
        "Eine vorhandene Voice-Sitzung wurde unnötig zurückgesetzt.");

    const ulong messageUserId = 600;
    const ulong firstMessageId = 700;
    const ulong secondMessageId = 701;
    var firstMessageXp = MessageXpCalculator.Calculate(firstMessageId, 15, 25);
    var secondMessageXp = MessageXpCalculator.Calculate(secondMessageId, 15, 25);
    Assert(
        (await database.RegisterMessageXpAsync(
            guildId,
            800,
            firstMessageId,
            messageUserId,
            firstMessageXp,
            now)).Changed,
        "Die erste Nachricht wurde nicht verbucht.");
    Assert(
        !(await database.RegisterMessageXpAsync(
            guildId,
            800,
            firstMessageId,
            messageUserId,
            firstMessageXp,
            now)).Changed,
        "Dieselbe Nachrichten-ID wurde doppelt verbucht.");
    Assert(
        (await database.RegisterMessageXpAsync(
            guildId,
            800,
            secondMessageId,
            messageUserId,
            secondMessageXp,
            now)).Changed,
        "Die zweite Nachricht wurde nicht verbucht.");

    var messageEntry = (await database.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == messageUserId);
    Assert(
        messageEntry.MessageCount == 2 &&
        messageEntry.MessageXp == firstMessageXp + secondMessageXp &&
        messageEntry.TotalXp == firstMessageXp + secondMessageXp,
        "Nachrichtenanzahl oder interne Nachrichten-XP stimmen nicht.");

    Assert(
        (await database.RemoveMessageXpAsync(guildId, firstMessageId)).Changed,
        "Gelöschte Nachrichten-XP wurden nicht entfernt.");
    messageEntry = (await database.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == messageUserId);
    Assert(
        messageEntry.MessageCount == 1 &&
        messageEntry.MessageXp == secondMessageXp &&
        messageEntry.TotalXp == secondMessageXp,
        "Die XP einer gelöschten Nachricht wurden nicht exakt zurückgenommen.");

    const ulong recalculationGuildId = 101;
    const ulong recalculationUserId = 601;
    const ulong recalculatedLiveMessageId = 702;
    const int recalculatedLiveMessageXp = 17;
    await database.RegisterMessageXpAsync(
        recalculationGuildId,
        800,
        recalculatedLiveMessageId,
        recalculationUserId,
        recalculatedLiveMessageXp,
        now);
    await database.ReplaceMessageXpAsync(recalculationGuildId, [], now.AddMinutes(1));
    var readdedLiveMessage = await database.RegisterMessageXpAsync(
        recalculationGuildId,
        800,
        recalculatedLiveMessageId,
        recalculationUserId,
        recalculatedLiveMessageXp,
        now.AddMinutes(2));
    var recalculationEntry = await database.GetInternalXpAccountAsync(
        recalculationGuildId,
        recalculationUserId);
    Assert(
        readdedLiveMessage is { Changed: true, Movement.Applied: true } &&
        recalculationEntry.MessageXp == recalculatedLiveMessageXp,
        "Eine Live-Nachricht wurde nach einer Neuberechnung nicht erneut auf das XP-Konto gebucht.");

    const ulong sourceUserId = 750;
    await database.AddXpAsync(
        guildId,
        sourceUserId,
        100,
        "voice:self-test",
        "voice:self-test",
        now);
    await database.AddXpAsync(
        guildId,
        sourceUserId,
        200,
        "invite:self-test",
        "invite:self-test",
        now);
    var recalculatedMessages = new[]
    {
        new MessageXpSnapshot(800, 801, sourceUserId, 15, now),
        new MessageXpSnapshot(800, 802, sourceUserId, 20, now)
    };
    await database.ReplaceMessageXpAsync(guildId, recalculatedMessages, now);
    var sourceEntry = (await database.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == sourceUserId);
    var directSourceEntry = await database.GetInternalXpAccountAsync(guildId, sourceUserId);
    Assert(
        sourceEntry.MessageXp == 35 &&
        sourceEntry.VoiceXp == 100 &&
        sourceEntry.InviteXp == 200 &&
        sourceEntry.TotalXp == 335 &&
        directSourceEntry == sourceEntry,
        "Die drei XP-Töpfe wurden nicht getrennt gespeichert oder korrekt summiert.");

    await database.ReplaceMessageXpAsync(guildId, [], now.AddMinutes(1));
    sourceEntry = (await database.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == sourceUserId);
    Assert(
        sourceEntry.MessageXp == 0 &&
        sourceEntry.VoiceXp == 100 &&
        sourceEntry.InviteXp == 200 &&
        sourceEntry.TotalXp == 300,
        "Die Neuberechnung hat Voice- oder Invite-XP verändert.");

    var reopenedDatabase = new BotDatabase(databasePath);
    await reopenedDatabase.InitializeAsync();
    var persistedSourceEntry =
        (await reopenedDatabase.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == sourceUserId);
    Assert(
        persistedSourceEntry.MessageXp == 0 &&
        persistedSourceEntry.VoiceXp == 100 &&
        persistedSourceEntry.InviteXp == 200 &&
        persistedSourceEntry.TotalXp == 300 &&
        persistedSourceEntry.CurrentLevel ==
        LevelCalculator.CalculateLevelFromTotalXp(300).Level,
        "XP-Töpfe oder Level wurden nach einem Datenbank-Neustart nicht wiederhergestellt.");

    const ulong levelUserId = 900;
    var levelSystem = new LevelSystemService(database);
    levelSystem.ConfigureGuild(guildId);
    var largeReward = LevelCalculator.GetXpForNextLevel(0) +
                      LevelCalculator.GetXpForNextLevel(1) +
                      5;
    var levelUpMovement = await levelSystem.AddXp(
        levelUserId,
        largeReward,
        "self-test-level-up");
    Assert(
        levelUpMovement.OldLevel == 0 &&
        levelUpMovement.NewLevel == 2 &&
        levelUpMovement.CurrentLevelProgress == 5,
        "AddXp hat nicht alle Level-Ups angewendet.");
    var storedLevelAccount = (await database.GetInternalXpLeaderboardAsync(guildId))
        .Single(entry => entry.UserId == levelUserId);
    Assert(
        storedLevelAccount.TotalXp == largeReward &&
        storedLevelAccount.CurrentLevel == 2 &&
        storedLevelAccount.CurrentLevelProgress == 5,
        "Gesamt-XP, Level oder Level-Fortschritt wurden nicht gespeichert.");

    var clampedRemoval = await levelSystem.RemoveXp(
        levelUserId,
        largeReward + 10_000,
        "self-test-clamp");
    Assert(
        clampedRemoval.NewXp == 0 &&
        clampedRemoval.NewLevel == 0 &&
        clampedRemoval.Amount == -largeReward,
        "RemoveXp hat XP nicht bei 0 begrenzt.");

    var movementLog = await database.GetXpMovementLogAsync(guildId, levelUserId);
    Assert(
        movementLog.Count == 2 &&
        movementLog[0].OldXp == 0 &&
        movementLog[0].NewXp == largeReward &&
        movementLog[0].OldLevel == 0 &&
        movementLog[0].NewLevel == 2 &&
        movementLog[0].Reason == "self-test-level-up" &&
        movementLog[0].Timestamp != default &&
        movementLog[1].OldXp == largeReward &&
        movementLog[1].NewXp == 0,
        "Das XP-Movement-Log enthält nicht alle geforderten Vorher-/Nachher-Werte.");

    await CreateLegacyDatabaseAsync(legacyDatabasePath, now);
    var migratedDatabase = new BotDatabase(legacyDatabasePath);
    await migratedDatabase.InitializeAsync();
    var migratedInvite = await migratedDatabase.GetInviteAsync(guildId, memberId);
    Assert(
        migratedInvite is { AutoProcess: false, RewardGiven: true },
        "Ein alter Invite-Datensatz wurde nicht sicher als manueller Backfill migriert.");
    Assert(
        await migratedDatabase.GetInviteLedgerEntryCountAsync(guildId) == 2,
        "Bereits früher vergebene Invite-XP wurden nicht in das dauerhafte Ledger migriert.");
    var migratedLeaderboard = await migratedDatabase.GetInternalXpLeaderboardAsync(guildId);
    var recoveredInviteXp = migratedLeaderboard.Single(entry => entry.UserId == 201);
    Assert(
        recoveredInviteXp.TotalXp == 700 &&
        recoveredInviteXp.InviteXp == 700,
        "An existing invite ledger entry without internal XP was not recovered.");
    var migratedInternalXp = migratedLeaderboard
        .Single(entry => entry.UserId == inviterId);
    Assert(
        migratedInternalXp.TotalXp == 525 &&
        migratedInternalXp.InviteXp == 500 &&
        migratedInternalXp.VoiceXp == 25,
        "Die früheren XP oder die alte source-Spalte wurden nicht korrekt migriert.");
    Assert(
        !await TableExistsAsync(legacyDatabasePath, "xp_accounts") &&
        !await TableExistsAsync(legacyDatabasePath, "xp_transactions") &&
        !await ColumnExistsAsync(
            legacyDatabasePath,
            "internal_xp_ledger",
            "source"),
        "Die alten XP-Tabellen oder die veraltete source-Spalte wurden nicht entfernt.");

    Console.WriteLine(
        "Selbsttest erfolgreich: Nachrichten-XP, internes Ledger sowie Invite- und Voice-Logik funktionieren.");
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
        CREATE TABLE internal_xp_ledger (
            id TEXT PRIMARY KEY,
            guild_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            amount INTEGER NOT NULL,
            source TEXT NOT NULL,
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
        CREATE TABLE invite_xp_ledger (
            id TEXT PRIMARY KEY,
            invite_reward_id TEXT NOT NULL,
            guild_id TEXT NOT NULL,
            member_id TEXT NOT NULL,
            inviter_id TEXT NOT NULL,
            invite_code TEXT NOT NULL,
            amount INTEGER NOT NULL,
            action TEXT NOT NULL,
            dispatch_reference_id TEXT NOT NULL UNIQUE,
            created_at_utc TEXT NOT NULL
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
        INSERT INTO internal_xp_ledger (
            id, guild_id, user_id, amount, source, reference_id, created_at_utc
        )
        VALUES (
            'legacy-voice-ledger', '100', '200', 25, 'voice',
            'voice:legacy', $rewardedAt
        );
        INSERT INTO invite_xp_ledger (
            id, invite_reward_id, guild_id, member_id, inviter_id,
            invite_code, amount, action, dispatch_reference_id, created_at_utc
        )
        VALUES (
            'orphan-invite-ledger', 'orphan-invite', '100', '301', '201',
            'orphan-code', 700, 'reward', 'invite-reward:orphan-invite', $rewardedAt
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

static async Task<bool> ColumnExistsAsync(
    string path,
    string tableName,
    string columnName)
{
    await using var connection = new SqliteConnection($"Data Source={path}");
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({tableName});";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        if (string.Equals(
                reader.GetString(1),
                columnName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}
