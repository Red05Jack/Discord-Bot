using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DiscordXpBot.Leveling;
using Microsoft.Data.Sqlite;

namespace DiscordXpBot.Persistence;

public sealed class BotDatabase
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BotDatabase(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS internal_xp_accounts (
                    guild_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    total_xp INTEGER NOT NULL DEFAULT 0,
                    current_level INTEGER NOT NULL DEFAULT 0,
                    current_level_progress INTEGER NOT NULL DEFAULT 0,
                    message_xp INTEGER NOT NULL DEFAULT 0,
                    voice_xp INTEGER NOT NULL DEFAULT 0,
                    invite_xp INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (guild_id, user_id)
                );

                CREATE TABLE IF NOT EXISTS user_preferences (
                    guild_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    rank_color TEXT NOT NULL DEFAULT '#FFFFFF',
                    PRIMARY KEY (guild_id, user_id)
                );

                CREATE TABLE IF NOT EXISTS xp_snapshot_baselines (
                    guild_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    message_xp INTEGER NOT NULL DEFAULT 0,
                    voice_xp INTEGER NOT NULL DEFAULT 0,
                    invite_xp INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (guild_id, user_id)
                );

                CREATE TABLE IF NOT EXISTS internal_xp_ledger (
                    id TEXT PRIMARY KEY,
                    guild_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    amount INTEGER NOT NULL,
                    reason TEXT NOT NULL,
                    reference_id TEXT NOT NULL UNIQUE,
                    created_at_utc TEXT NOT NULL,
                    old_xp INTEGER NOT NULL DEFAULT 0,
                    new_xp INTEGER NOT NULL DEFAULT 0,
                    old_level INTEGER NOT NULL DEFAULT 0,
                    new_level INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS ix_internal_xp_ledger_user
                    ON internal_xp_ledger (guild_id, user_id, created_at_utc);

                CREATE TABLE IF NOT EXISTS message_xp (
                    guild_id TEXT NOT NULL,
                    channel_id TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    xp INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    PRIMARY KEY (guild_id, message_id)
                );

                CREATE INDEX IF NOT EXISTS ix_message_xp_user
                    ON message_xp (guild_id, user_id);

                CREATE TABLE IF NOT EXISTS invite_rewards (
                    id TEXT PRIMARY KEY,
                    guild_id TEXT NOT NULL,
                    member_id TEXT NOT NULL,
                    inviter_id TEXT NOT NULL,
                    invite_code TEXT NOT NULL,
                    joined_at_utc TEXT NOT NULL,
                    reward_xp INTEGER NOT NULL,
                    reward_given INTEGER NOT NULL DEFAULT 0,
                    rewarded_at_utc TEXT NULL,
                    auto_process INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (guild_id, member_id)
                );

                CREATE INDEX IF NOT EXISTS ix_invite_rewards_due
                    ON invite_rewards (guild_id, reward_given, joined_at_utc);

                CREATE TABLE IF NOT EXISTS voice_sessions (
                    session_id TEXT PRIMARY KEY,
                    guild_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    channel_id TEXT NOT NULL,
                    started_at_utc TEXT NOT NULL,
                    minimum_reached INTEGER NOT NULL DEFAULT 0,
                    UNIQUE (guild_id, user_id)
                );

                CREATE TABLE IF NOT EXISTS invite_xp_ledger (
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

                CREATE INDEX IF NOT EXISTS ix_invite_xp_ledger_member
                    ON invite_xp_ledger (guild_id, member_id, created_at_utc);

                """;
            await command.ExecuteNonQueryAsync();
            await EnsureColumnAsync(
                connection,
                "invite_rewards",
                "auto_process",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "voice_sessions",
                "minimum_reached",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_accounts",
                "total_xp",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_accounts",
                "current_level",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_accounts",
                "current_level_progress",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_accounts",
                "message_xp",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_accounts",
                "voice_xp",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_accounts",
                "invite_xp",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_ledger",
                "reason",
                "TEXT NOT NULL DEFAULT ''");
            await EnsureColumnAsync(
                connection,
                "internal_xp_ledger",
                "old_xp",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_ledger",
                "new_xp",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_ledger",
                "old_level",
                "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(
                connection,
                "internal_xp_ledger",
                "new_level",
                "INTEGER NOT NULL DEFAULT 0");
            await MigrateLegacyMovementReasonAsync(connection);
            await RebuildLegacyInternalXpLedgerSchemaAsync(connection);
            await MigrateAndRemoveLegacyXpTablesAsync(connection);
            await ImportAndRemoveLegacyDispatchesAsync(connection);
            await ImportInviteLedgerIntoInternalXpAsync(connection);
            await RemoveEstimatedInviteUsageBackfillAsync(connection);
            await RebuildLevelStateAsync(connection);
            await ReconcileMessageXpAccountsAsync(connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertInviteAsync(InviteRewardRecord record)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO invite_rewards (
                    id, guild_id, member_id, inviter_id, invite_code,
                    joined_at_utc, reward_xp, reward_given, rewarded_at_utc, auto_process
                )
                VALUES (
                    $id, $guildId, $memberId, $inviterId, $inviteCode,
                    $joinedAt, $rewardXp, 0, NULL, $autoProcess
                )
                ON CONFLICT (guild_id, member_id) DO UPDATE SET
                    id = excluded.id,
                    inviter_id = excluded.inviter_id,
                    invite_code = excluded.invite_code,
                    joined_at_utc = excluded.joined_at_utc,
                    reward_xp = excluded.reward_xp,
                    reward_given = 0,
                    rewarded_at_utc = NULL,
                    auto_process = excluded.auto_process;
                """;
            AddParameter(command, "$id", record.Id);
            AddParameter(command, "$guildId", Id(record.GuildId));
            AddParameter(command, "$memberId", Id(record.MemberId));
            AddParameter(command, "$inviterId", Id(record.InviterId));
            AddParameter(command, "$inviteCode", record.InviteCode);
            AddParameter(command, "$joinedAt", Timestamp(record.JoinedAtUtc));
            AddParameter(command, "$rewardXp", record.RewardXp);
            AddParameter(command, "$autoProcess", record.AutoProcess ? 1 : 0);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InviteRewardRecord?> GetInviteAsync(ulong guildId, ulong memberId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, guild_id, member_id, inviter_id, invite_code,
                       joined_at_utc, reward_xp, reward_given, auto_process
                FROM invite_rewards
                WHERE guild_id = $guildId AND member_id = $memberId;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            AddParameter(command, "$memberId", Id(memberId));
            await using var reader = await command.ExecuteReaderAsync();
            return await reader.ReadAsync() ? ReadInvite(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InviteRewardRecord>> GetDueInvitesAsync(
        ulong guildId,
        DateTimeOffset joinedBeforeUtc,
        bool includeHistorical = false)
    {
        await _gate.WaitAsync();
        try
        {
            var result = new List<InviteRewardRecord>();
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, guild_id, member_id, inviter_id, invite_code,
                       joined_at_utc, reward_xp, reward_given, auto_process
                FROM invite_rewards
                WHERE guild_id = $guildId
                  AND reward_given = 0
                  AND joined_at_utc <= $joinedBefore
                  AND ($includeHistorical = 1 OR auto_process = 1)
                ORDER BY joined_at_utc;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            AddParameter(command, "$joinedBefore", Timestamp(joinedBeforeUtc));
            AddParameter(command, "$includeHistorical", includeHistorical ? 1 : 0);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(ReadInvite(reader));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> EnableHistoricalInvitesAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE invite_rewards
                SET auto_process = 1
                WHERE guild_id = $guildId
                  AND reward_given = 0
                  AND auto_process = 0;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            return await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<XpMovementResult?> MarkInviteRewardGivenAsync(
        InviteRewardRecord record,
        DateTimeOffset rewardedAtUtc)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE invite_rewards
                SET reward_given = 1, rewarded_at_utc = $rewardedAt
                WHERE id = $id AND reward_given = 0;
                """;
            AddParameter(update, "$rewardedAt", Timestamp(rewardedAtUtc));
            AddParameter(update, "$id", record.Id);
            var changed = await update.ExecuteNonQueryAsync();
            if (changed == 0)
            {
                transaction.Rollback();
                return null;
            }

            var movement = await ApplyInternalXpAsync(
                connection,
                transaction,
                record.GuildId,
                record.InviterId,
                record.RewardXp,
                "invite-reward",
                $"invite-reward:{record.Id}",
                rewardedAtUtc);
            await InsertInviteLedgerAsync(
                connection,
                transaction,
                record,
                record.RewardXp,
                "reward",
                $"invite-reward:{record.Id}",
                rewardedAtUtc);

            transaction.Commit();
            return movement;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InviteRemovalResult> RevokeInviteAndDeleteAsync(
        InviteRewardRecord record,
        DateTimeOffset nowUtc)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            XpMovementResult? movement = null;
            if (record.RewardGiven)
            {
                movement = await ApplyInternalXpAsync(
                    connection,
                    transaction,
                    record.GuildId,
                    record.InviterId,
                    -record.RewardXp,
                    "invite-revocation",
                    $"invite-revocation:{record.Id}",
                    nowUtc);
                await InsertInviteLedgerAsync(
                    connection,
                    transaction,
                    record,
                    -record.RewardXp,
                    "revocation",
                    $"invite-revocation:{record.Id}",
                    nowUtc);
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM invite_rewards WHERE id = $id;";
            AddParameter(delete, "$id", record.Id);
            var changed = await delete.ExecuteNonQueryAsync();
            transaction.Commit();
            return new InviteRemovalResult(changed > 0, movement);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InviteMemberBackfillResult> BackfillInviteMembersAsync(
        ulong guildId,
        IReadOnlyCollection<HistoricalInviteMember> members,
        DateTimeOffset rewardCutoffUtc,
        int minXp,
        int maxXp,
        DateTimeOffset nowUtc)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();
            var movements = new List<XpMovementResult>();
            var alreadyTrackedMembers = 0;
            var importedMembers = 0;
            var pendingMembers = 0;
            var rewardedMembers = 0;
            var awardedXp = 0;

            foreach (var member in members
                         .GroupBy(member => member.MemberId)
                         .Select(group => group.OrderByDescending(
                             member => member.JoinedAtUtc).First()))
            {
                var alreadyTracked = false;
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText =
                        """
                        SELECT 1
                        FROM invite_rewards
                        WHERE guild_id = $guildId AND member_id = $memberId
                        LIMIT 1;
                        """;
                    AddParameter(exists, "$guildId", Id(guildId));
                    AddParameter(exists, "$memberId", Id(member.MemberId));
                    alreadyTracked = await exists.ExecuteScalarAsync() is not null;
                }

                if (alreadyTracked)
                {
                    alreadyTrackedMembers++;
                    continue;
                }

                var record = new InviteRewardRecord(
                    CreateHistoricalInviteRewardId(
                        guildId,
                        member.MemberId,
                        member.InviteCode,
                        member.JoinedAtUtc),
                    guildId,
                    member.MemberId,
                    member.InviterId,
                    member.InviteCode,
                    member.JoinedAtUtc,
                    RandomNumberGenerator.GetInt32(minXp, maxXp + 1),
                    false);

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO invite_rewards (
                            id, guild_id, member_id, inviter_id, invite_code,
                            joined_at_utc, reward_xp, reward_given,
                            rewarded_at_utc, auto_process
                        )
                        VALUES (
                            $id, $guildId, $memberId, $inviterId, $inviteCode,
                            $joinedAt, $rewardXp, 0, NULL, 1
                        );
                        """;
                    AddParameter(insert, "$id", record.Id);
                    AddParameter(insert, "$guildId", Id(guildId));
                    AddParameter(insert, "$memberId", Id(record.MemberId));
                    AddParameter(insert, "$inviterId", Id(record.InviterId));
                    AddParameter(insert, "$inviteCode", record.InviteCode);
                    AddParameter(insert, "$joinedAt", Timestamp(record.JoinedAtUtc));
                    AddParameter(insert, "$rewardXp", record.RewardXp);
                    await insert.ExecuteNonQueryAsync();
                }

                importedMembers++;
                if (record.JoinedAtUtc > rewardCutoffUtc)
                {
                    pendingMembers++;
                    continue;
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE invite_rewards
                        SET reward_given = 1, rewarded_at_utc = $rewardedAt
                        WHERE id = $id;
                        """;
                    AddParameter(update, "$rewardedAt", Timestamp(nowUtc));
                    AddParameter(update, "$id", record.Id);
                    await update.ExecuteNonQueryAsync();
                }

                var movement = await ApplyInternalXpAsync(
                    connection,
                    transaction,
                    guildId,
                    record.InviterId,
                    record.RewardXp,
                    "invite-reward",
                    $"invite-reward:{record.Id}",
                    nowUtc);
                if (!movement.Applied)
                {
                    await InsertInviteLedgerAsync(
                        connection,
                        transaction,
                        record,
                        record.RewardXp,
                        "reward",
                        $"invite-reward:{record.Id}",
                        nowUtc);
                    alreadyTrackedMembers++;
                    continue;
                }

                await InsertInviteLedgerAsync(
                    connection,
                    transaction,
                    record,
                    record.RewardXp,
                    "reward",
                    $"invite-reward:{record.Id}",
                    nowUtc);
                movements.Add(movement);
                rewardedMembers++;
                awardedXp += record.RewardXp;
            }

            transaction.Commit();
            return new InviteMemberBackfillResult(
                members.Count,
                alreadyTrackedMembers,
                importedMembers,
                pendingMembers,
                rewardedMembers,
                awardedXp,
                movements);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> SynchronizeVoiceSessionsAsync(
        ulong guildId,
        IEnumerable<(ulong UserId, ulong ChannelId)> activeUsers,
        DateTimeOffset nowUtc)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            var activeUsersById = activeUsers
                .GroupBy(user => user.UserId)
                .ToDictionary(group => group.Key, group => group.Last().ChannelId);
            var existingSessions = new Dictionary<ulong, ulong>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT user_id, channel_id
                    FROM voice_sessions
                    WHERE guild_id = $guildId;
                    """;
                AddParameter(select, "$guildId", Id(guildId));
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existingSessions[
                        ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] =
                        ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                }
            }

            foreach (var activeUser in activeUsersById)
            {
                if (existingSessions.TryGetValue(
                        activeUser.Key,
                        out var existingChannelId) &&
                    existingChannelId == activeUser.Value)
                {
                    continue;
                }

                await InsertVoiceSessionAsync(
                    connection,
                    transaction,
                    guildId,
                    activeUser.Key,
                    activeUser.Value,
                    nowUtc);
            }

            transaction.Commit();
            return activeUsersById.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> EnsureVoiceSessionAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        DateTimeOffset nowUtc)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            ulong? existingChannelId = null;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT channel_id
                    FROM voice_sessions
                    WHERE guild_id = $guildId AND user_id = $userId;
                    """;
                AddParameter(select, "$guildId", Id(guildId));
                AddParameter(select, "$userId", Id(userId));
                var value = await select.ExecuteScalarAsync();
                if (value is string channelValue)
                {
                    existingChannelId = ulong.Parse(
                        channelValue,
                        CultureInfo.InvariantCulture);
                }
            }

            if (existingChannelId == channelId)
            {
                transaction.Commit();
                return false;
            }

            await InsertVoiceSessionAsync(
                connection,
                transaction,
                guildId,
                userId,
                channelId,
                nowUtc);
            transaction.Commit();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartVoiceSessionAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        DateTimeOffset nowUtc)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();
            await InsertVoiceSessionAsync(
                connection,
                transaction,
                guildId,
                userId,
                channelId,
                nowUtc);
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VoiceRewardResult> RewardVoiceTimeAsync(
        ulong guildId,
        ulong userId,
        DateTimeOffset nowUtc,
        int minXpPerBlock,
        int maxXpPerBlock,
        int rewardBlockMinutes,
        bool deleteSession)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT session_id, started_at_utc
                FROM voice_sessions
                WHERE guild_id = $guildId AND user_id = $userId;
                """;
            AddParameter(select, "$guildId", Id(guildId));
            AddParameter(select, "$userId", Id(userId));

            string? sessionId = null;
            DateTimeOffset startedAtUtc = default;
            await using (var reader = await select.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    sessionId = reader.GetString(0);
                    startedAtUtc = ParseTimestamp(reader.GetString(1));
                }
            }

            if (sessionId is null)
            {
                transaction.Rollback();
                return VoiceRewardResult.Empty;
            }

            var elapsed = nowUtc - startedAtUtc;
            var completedBlocks = Math.Max(
                0,
                (int)Math.Floor(elapsed.TotalMinutes / rewardBlockMinutes));
            var rewardedMinutes = completedBlocks * rewardBlockMinutes;
            var xp = 0;
            XpMovementResult? movement = null;

            if (completedBlocks > 0)
            {
                for (var block = 0; block < completedBlocks; block++)
                {
                    xp += RandomNumberGenerator.GetInt32(minXpPerBlock, maxXpPerBlock + 1);
                }

                movement = await ApplyInternalXpAsync(
                    connection,
                    transaction,
                    guildId,
                    userId,
                    xp,
                    "voice",
                    $"voice:{sessionId}:{startedAtUtc.ToUnixTimeMilliseconds()}",
                    nowUtc);

                if (!deleteSession)
                {
                    await using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE voice_sessions
                        SET started_at_utc = $startedAt,
                            minimum_reached = 1
                        WHERE session_id = $sessionId;
                        """;
                    AddParameter(
                        update,
                        "$startedAt",
                        Timestamp(startedAtUtc.AddMinutes(rewardedMinutes)));
                    AddParameter(update, "$sessionId", sessionId);
                    await update.ExecuteNonQueryAsync();
                }
            }

            if (deleteSession)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM voice_sessions WHERE session_id = $sessionId;";
                AddParameter(delete, "$sessionId", sessionId);
                await delete.ExecuteNonQueryAsync();
            }

            transaction.Commit();
            return new VoiceRewardResult(
                rewardedMinutes,
                xp,
                xp > 0 ? movement : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetInviteLedgerEntryCountAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM invite_xp_ledger WHERE guild_id = $guildId;";
            AddParameter(command, "$guildId", Id(guildId));
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<XpMovementResult> AddXpAsync(
        ulong guildId,
        ulong userId,
        int amount,
        string reason,
        string referenceId,
        DateTimeOffset timestamp)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must not be negative.");
        }

        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();
            var result = await ApplyXpMovementAsync(
                connection,
                transaction,
                guildId,
                userId,
                amount,
                reason,
                referenceId,
                timestamp,
                ResolveXpSource(reason));
            transaction.Commit();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureUserAccountsAsync(
        ulong guildId,
        IEnumerable<ulong> userIds)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            foreach (var userId in userIds.Distinct())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO internal_xp_accounts (
                        guild_id, user_id, total_xp, current_level, current_level_progress,
                        message_xp, voice_xp, invite_xp
                    )
                    VALUES ($guildId, $userId, 0, 0, 0, 0, 0, 0);
                    """;
                AddParameter(command, "$guildId", Id(guildId));
                AddParameter(command, "$userId", Id(userId));
                await command.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetRankColorAsync(ulong guildId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT rank_color
                FROM user_preferences
                WHERE guild_id = $guildId AND user_id = $userId;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            AddParameter(command, "$userId", Id(userId));
            return await command.ExecuteScalarAsync() as string ?? "#FFFFFF";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetRankColorAsync(
        ulong guildId,
        ulong userId,
        string rankColor)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO user_preferences (guild_id, user_id, rank_color)
                VALUES ($guildId, $userId, $rankColor)
                ON CONFLICT (guild_id, user_id) DO UPDATE SET
                    rank_color = excluded.rank_color;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            AddParameter(command, "$userId", Id(userId));
            AddParameter(command, "$rankColor", rankColor);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PersistentUserProfile>>
        GetPersistentUserProfilesAsync(ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var profiles = new List<PersistentUserProfile>();
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    accounts.user_id,
                    accounts.message_xp,
                    accounts.voice_xp,
                    accounts.invite_xp,
                    COALESCE(preferences.rank_color, '#FFFFFF')
                FROM internal_xp_accounts AS accounts
                LEFT JOIN user_preferences AS preferences
                    ON preferences.guild_id = accounts.guild_id
                   AND preferences.user_id = accounts.user_id
                WHERE accounts.guild_id = $guildId
                ORDER BY accounts.user_id;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                profiles.Add(new PersistentUserProfile(
                    ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetString(4)));
            }

            return profiles;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistentRestoreResult> RestorePersistentUserProfilesAsync(
        ulong guildId,
        IReadOnlyCollection<PersistentUserProfile> profiles,
        DateTimeOffset timestamp)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();
            var canRestoreXp = await HasNoStoredXpAsync(
                connection,
                transaction,
                guildId);
            var restoredXpUsers = 0;
            var restoredColors = 0;

            foreach (var profile in profiles
                         .GroupBy(profile => profile.UserId)
                         .Select(group => group.Last()))
            {
                await using (var preference = connection.CreateCommand())
                {
                    preference.Transaction = transaction;
                    preference.CommandText =
                        """
                        INSERT INTO user_preferences (guild_id, user_id, rank_color)
                        VALUES ($guildId, $userId, $rankColor)
                        ON CONFLICT (guild_id, user_id) DO UPDATE SET
                            rank_color = excluded.rank_color;
                        """;
                    AddParameter(preference, "$guildId", Id(guildId));
                    AddParameter(preference, "$userId", Id(profile.UserId));
                    AddParameter(
                        preference,
                        "$rankColor",
                        NormalizeStoredRankColor(profile.RankColor));
                    await preference.ExecuteNonQueryAsync();
                    restoredColors++;
                }

                if (!canRestoreXp)
                {
                    continue;
                }

                var totals = new XpSourceTotals(
                    Math.Max(0, profile.MessageXp),
                    Math.Max(0, profile.VoiceXp),
                    Math.Max(0, profile.InviteXp));
                await using (var baseline = connection.CreateCommand())
                {
                    baseline.Transaction = transaction;
                    baseline.CommandText =
                        """
                        INSERT INTO xp_snapshot_baselines (
                            guild_id, user_id, message_xp, voice_xp, invite_xp
                        )
                        VALUES (
                            $guildId, $userId, $messageXp, $voiceXp, $inviteXp
                        )
                        ON CONFLICT (guild_id, user_id) DO UPDATE SET
                            message_xp = excluded.message_xp,
                            voice_xp = excluded.voice_xp,
                            invite_xp = excluded.invite_xp;
                        """;
                    AddParameter(baseline, "$guildId", Id(guildId));
                    AddParameter(baseline, "$userId", Id(profile.UserId));
                    AddParameter(baseline, "$messageXp", totals.MessageXp);
                    AddParameter(baseline, "$voiceXp", totals.VoiceXp);
                    AddParameter(baseline, "$inviteXp", totals.InviteXp);
                    await baseline.ExecuteNonQueryAsync();
                }

                var cumulativeXp = 0L;
                foreach (var source in new[]
                         {
                             (Amount: totals.MessageXp, Reason: "message-snapshot"),
                             (Amount: totals.VoiceXp, Reason: "voice-snapshot"),
                             (Amount: totals.InviteXp, Reason: "invite-snapshot")
                         })
                {
                    if (source.Amount <= 0)
                    {
                        continue;
                    }

                    var oldState =
                        LevelCalculator.CalculateLevelFromTotalXp(cumulativeXp);
                    cumulativeXp += source.Amount;
                    var newState =
                        LevelCalculator.CalculateLevelFromTotalXp(cumulativeXp);
                    await InsertXpMovementLogAsync(
                        connection,
                        transaction,
                        guildId,
                        profile.UserId,
                        checked((int)source.Amount),
                        source.Reason,
                        $"discord-db:{guildId}:{profile.UserId}:{source.Reason}",
                        timestamp,
                        oldState,
                        newState);
                }

                var finalState =
                    LevelCalculator.CalculateLevelFromTotalXp(totals.TotalXp);
                await UpsertXpAccountAsync(
                    connection,
                    transaction,
                    guildId,
                    profile.UserId,
                    totals,
                    finalState);
                restoredXpUsers++;
            }

            transaction.Commit();
            return new PersistentRestoreResult(
                canRestoreXp,
                restoredXpUsers,
                restoredColors);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<XpMovementResult> RemoveXpAsync(
        ulong guildId,
        ulong userId,
        int amount,
        string reason,
        string referenceId,
        DateTimeOffset timestamp)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must not be negative.");
        }

        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();
            var result = await ApplyXpMovementAsync(
                connection,
                transaction,
                guildId,
                userId,
                -amount,
                reason,
                referenceId,
                timestamp,
                ResolveXpSource(reason));
            transaction.Commit();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MessageXpResult> RegisterMessageXpAsync(
        ulong guildId,
        ulong channelId,
        ulong messageId,
        ulong userId,
        int xp,
        DateTimeOffset createdAtUtc)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            await using var message = connection.CreateCommand();
            message.Transaction = transaction;
            message.CommandText =
                """
                INSERT OR IGNORE INTO message_xp (
                    guild_id, channel_id, message_id, user_id, xp, created_at_utc
                )
                VALUES (
                    $guildId, $channelId, $messageId, $userId, $xp, $createdAt
                );
                """;
            AddParameter(message, "$guildId", Id(guildId));
            AddParameter(message, "$channelId", Id(channelId));
            AddParameter(message, "$messageId", Id(messageId));
            AddParameter(message, "$userId", Id(userId));
            AddParameter(message, "$xp", xp);
            AddParameter(message, "$createdAt", Timestamp(createdAtUtc));
            var inserted = await message.ExecuteNonQueryAsync();
            if (inserted == 0)
            {
                transaction.Rollback();
                return MessageXpResult.NotChanged;
            }

            var movement = await ApplyXpMovementAsync(
                connection,
                transaction,
                guildId,
                userId,
                xp,
                "message",
                $"message-add:{messageId}:{Guid.NewGuid():N}",
                createdAtUtc,
                XpSource.Message);
            if (!movement.Applied)
            {
                transaction.Rollback();
                throw new InvalidOperationException(
                    $"Message XP movement for {messageId} was not applied.");
            }

            transaction.Commit();
            return new MessageXpResult(true, movement);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MessageXpResult> RemoveMessageXpAsync(ulong guildId, ulong messageId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            ulong userId;
            var xp = 0;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT user_id, xp
                    FROM message_xp
                    WHERE guild_id = $guildId AND message_id = $messageId;
                    """;
                AddParameter(select, "$guildId", Id(guildId));
                AddParameter(select, "$messageId", Id(messageId));
                await using var reader = await select.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    transaction.Rollback();
                    return MessageXpResult.NotChanged;
                }

                userId = ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                xp = reader.GetInt32(1);
            }

            await using (var deleteMessage = connection.CreateCommand())
            {
                deleteMessage.Transaction = transaction;
                deleteMessage.CommandText =
                    """
                    DELETE FROM message_xp
                    WHERE guild_id = $guildId AND message_id = $messageId;
                    """;
                AddParameter(deleteMessage, "$guildId", Id(guildId));
                AddParameter(deleteMessage, "$messageId", Id(messageId));
                await deleteMessage.ExecuteNonQueryAsync();
            }

            var movement = await ApplyXpMovementAsync(
                connection,
                transaction,
                guildId,
                userId,
                -xp,
                "message-deleted",
                $"message-delete:{messageId}:{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                XpSource.Message);

            transaction.Commit();
            return new MessageXpResult(true, movement);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<XpMovementResult>> ReplaceMessageXpAsync(
        ulong guildId,
        IReadOnlyCollection<MessageXpSnapshot> messages,
        DateTimeOffset timestamp)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            using var transaction = connection.BeginTransaction();

            var oldAccounts = new Dictionary<ulong, XpSourceTotals>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT user_id, message_xp, voice_xp, invite_xp
                    FROM internal_xp_accounts
                    WHERE guild_id = $guildId;
                    """;
                AddParameter(select, "$guildId", Id(guildId));
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    oldAccounts[ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture)] =
                        new XpSourceTotals(
                            reader.GetInt64(1),
                            reader.GetInt64(2),
                            reader.GetInt64(3));
                }
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM message_xp WHERE guild_id = $guildId;";
                AddParameter(delete, "$guildId", Id(guildId));
                await delete.ExecuteNonQueryAsync();
            }

            await using (var clearSnapshotBaseline = connection.CreateCommand())
            {
                clearSnapshotBaseline.Transaction = transaction;
                clearSnapshotBaseline.CommandText =
                    """
                    UPDATE xp_snapshot_baselines
                    SET message_xp = 0
                    WHERE guild_id = $guildId;
                    """;
                AddParameter(clearSnapshotBaseline, "$guildId", Id(guildId));
                await clearSnapshotBaseline.ExecuteNonQueryAsync();
            }

            foreach (var message in messages)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO message_xp (
                        guild_id, channel_id, message_id, user_id, xp, created_at_utc
                    )
                    VALUES (
                        $guildId, $channelId, $messageId, $userId, $xp, $createdAt
                    );
                    """;
                AddParameter(insert, "$guildId", Id(guildId));
                AddParameter(insert, "$channelId", Id(message.ChannelId));
                AddParameter(insert, "$messageId", Id(message.MessageId));
                AddParameter(insert, "$userId", Id(message.UserId));
                AddParameter(insert, "$xp", message.Xp);
                AddParameter(insert, "$createdAt", Timestamp(message.CreatedAtUtc));
                await insert.ExecuteNonQueryAsync();
            }

            var newMessageXpByUser = messages
                .GroupBy(message => message.UserId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(message => (long)message.Xp));
            var userIds = oldAccounts.Keys
                .Union(newMessageXpByUser.Keys)
                .Distinct()
                .ToArray();
            var results = new List<XpMovementResult>();
            var runId = Guid.NewGuid().ToString("N");

            foreach (var userId in userIds)
            {
                oldAccounts.TryGetValue(userId, out var oldTotals);
                oldTotals ??= new XpSourceTotals();
                var newMessageXp = newMessageXpByUser.GetValueOrDefault(userId);
                var newTotals = oldTotals with { MessageXp = newMessageXp };
                var oldState = LevelCalculator.CalculateLevelFromTotalXp(oldTotals.TotalXp);
                var newState = LevelCalculator.CalculateLevelFromTotalXp(newTotals.TotalXp);
                var amount = checked((int)(newTotals.TotalXp - oldTotals.TotalXp));

                if (amount != 0)
                {
                    await InsertXpMovementLogAsync(
                        connection,
                        transaction,
                        guildId,
                        userId,
                        amount,
                        "message-recalculate",
                        $"message-recalculate:{runId}:{userId}",
                        timestamp,
                        oldState,
                        newState);
                }

                await UpsertXpAccountAsync(
                    connection,
                    transaction,
                    guildId,
                    userId,
                    newTotals,
                    newState);

                results.Add(new XpMovementResult(
                    amount != 0,
                    guildId,
                    userId,
                    amount,
                    "message-recalculate",
                    $"message-recalculate:{runId}:{userId}",
                    timestamp,
                    oldState.TotalXp,
                    newState.TotalXp,
                    oldState.Level,
                    newState.Level,
                    newState.CurrentLevelProgress,
                    newState.XpForNextLevel));
            }

            transaction.Commit();
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InternalXpEntry>> GetInternalXpLeaderboardAsync(
        ulong guildId)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = new List<InternalXpEntry>();
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    accounts.user_id,
                    accounts.total_xp,
                    accounts.current_level,
                    accounts.current_level_progress,
                    accounts.message_xp,
                    accounts.voice_xp,
                    accounts.invite_xp,
                    COALESCE(messages.message_count, 0)
                FROM internal_xp_accounts AS accounts
                LEFT JOIN (
                    SELECT user_id, COUNT(*) AS message_count
                    FROM message_xp
                    WHERE guild_id = $guildId
                    GROUP BY user_id
                ) AS messages ON messages.user_id = accounts.user_id
                WHERE accounts.guild_id = $guildId
                ORDER BY accounts.total_xp DESC, accounts.user_id;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(new InternalXpEntry(
                    ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7)));
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InternalXpEntry> GetInternalXpAccountAsync(
        ulong guildId,
        ulong userId)
    {
        await EnsureUserAccountsAsync(guildId, [userId]);
        var entries = await GetInternalXpLeaderboardAsync(guildId);
        return entries.Single(entry => entry.UserId == userId);
    }

    public async Task<IReadOnlyList<XpMovementLogEntry>> GetXpMovementLogAsync(
        ulong guildId,
        ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            var entries = new List<XpMovementLogEntry>();
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT amount, reason, created_at_utc, old_xp, new_xp, old_level, new_level
                FROM internal_xp_ledger
                WHERE guild_id = $guildId AND user_id = $userId
                ORDER BY created_at_utc, rowid;
                """;
            AddParameter(command, "$guildId", Id(guildId));
            AddParameter(command, "$userId", Id(userId));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(new XpMovementLogEntry(
                    userId,
                    reader.GetInt32(0),
                    reader.GetString(1),
                    ParseTimestamp(reader.GetString(2)),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task InsertVoiceSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        ulong userId,
        ulong channelId,
        DateTimeOffset nowUtc)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO voice_sessions (
                session_id, guild_id, user_id, channel_id, started_at_utc, minimum_reached
            )
            VALUES ($sessionId, $guildId, $userId, $channelId, $startedAt, 0)
            ON CONFLICT (guild_id, user_id) DO UPDATE SET
                session_id = excluded.session_id,
                channel_id = excluded.channel_id,
                started_at_utc = excluded.started_at_utc,
                minimum_reached = 0;
            """;
        AddParameter(command, "$sessionId", Guid.NewGuid().ToString("N"));
        AddParameter(command, "$guildId", Id(guildId));
        AddParameter(command, "$userId", Id(userId));
        AddParameter(command, "$channelId", Id(channelId));
        AddParameter(command, "$startedAt", Timestamp(nowUtc));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<XpMovementResult> ApplyInternalXpAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        ulong userId,
        int amount,
        string reason,
        string referenceId,
        DateTimeOffset nowUtc)
    {
        return await ApplyXpMovementAsync(
            connection,
            transaction,
            guildId,
            userId,
            amount,
            reason,
            referenceId,
            nowUtc,
            ResolveXpSource(reason));
    }

    private static async Task MigrateAndRemoveLegacyXpTablesAsync(SqliteConnection connection)
    {
        if (await TableExistsAsync(connection, "xp_transactions"))
        {
            await using var migrateInviteLedger = connection.CreateCommand();
            migrateInviteLedger.CommandText =
                """
                INSERT OR IGNORE INTO invite_xp_ledger (
                    id, invite_reward_id, guild_id, member_id, inviter_id,
                    invite_code, amount, action, dispatch_reference_id, created_at_utc
                )
                SELECT
                    lower(hex(randomblob(16))),
                    COALESCE(
                        rewards.id,
                        substr(transactions.reference_id, instr(transactions.reference_id, ':') + 1)
                    ),
                    transactions.guild_id,
                    COALESCE(rewards.member_id, 'unknown'),
                    transactions.user_id,
                    COALESCE(rewards.invite_code, 'unknown'),
                    transactions.amount,
                    CASE
                        WHEN transactions.reason = 'invite-revocation' THEN 'revocation'
                        ELSE 'reward'
                    END,
                    transactions.reference_id,
                    transactions.created_at_utc
                FROM xp_transactions AS transactions
                LEFT JOIN invite_rewards AS rewards
                    ON transactions.reference_id = 'invite-reward:' || rewards.id
                    OR transactions.reference_id = 'invite-revocation:' || rewards.id
                WHERE transactions.reason IN ('invite-reward', 'invite-revocation');
                """;
            await migrateInviteLedger.ExecuteNonQueryAsync();

            await using var migrateInternalXp = connection.CreateCommand();
            migrateInternalXp.CommandText =
                """
                INSERT OR IGNORE INTO internal_xp_ledger (
                    id, guild_id, user_id, amount, reason, reference_id, created_at_utc
                )
                SELECT
                    lower(hex(randomblob(16))),
                    guild_id,
                    user_id,
                    amount,
                    reason,
                    reference_id,
                    created_at_utc
                FROM xp_transactions;
                """;
            await migrateInternalXp.ExecuteNonQueryAsync();
        }

        await using var remove = connection.CreateCommand();
        remove.CommandText =
            """
            DROP TABLE IF EXISTS xp_accounts;
            DROP TABLE IF EXISTS xp_transactions;
            """;
        await remove.ExecuteNonQueryAsync();
    }

    private static async Task ImportAndRemoveLegacyDispatchesAsync(
        SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "xp_dispatches"))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO internal_xp_ledger (
                id, guild_id, user_id, amount, reason, reference_id, created_at_utc
            )
            SELECT
                lower(hex(randomblob(16))),
                guild_id,
                user_id,
                amount,
                reason,
                reference_id,
                created_at_utc
            FROM xp_dispatches
            WHERE 1 = 1
            ON CONFLICT(reference_id) DO NOTHING;

            DROP TABLE xp_dispatches;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveEstimatedInviteUsageBackfillAsync(
        SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "invite_usage_backfill"))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM internal_xp_ledger
            WHERE reason = 'invite-history'
               OR reference_id LIKE 'invite-history:%';

            DROP TABLE invite_usage_backfill;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateLegacyMovementReasonAsync(SqliteConnection connection)
    {
        if (!await ColumnExistsAsync(connection, "internal_xp_ledger", "source"))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE internal_xp_ledger
            SET reason = source
            WHERE reason = '';
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RebuildLegacyInternalXpLedgerSchemaAsync(
        SqliteConnection connection)
    {
        if (!await ColumnExistsAsync(connection, "internal_xp_ledger", "source"))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DROP TABLE IF EXISTS internal_xp_ledger_migrated;

            CREATE TABLE internal_xp_ledger_migrated (
                id TEXT PRIMARY KEY,
                guild_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                amount INTEGER NOT NULL,
                reason TEXT NOT NULL,
                reference_id TEXT NOT NULL UNIQUE,
                created_at_utc TEXT NOT NULL,
                old_xp INTEGER NOT NULL DEFAULT 0,
                new_xp INTEGER NOT NULL DEFAULT 0,
                old_level INTEGER NOT NULL DEFAULT 0,
                new_level INTEGER NOT NULL DEFAULT 0
            );

            INSERT INTO internal_xp_ledger_migrated (
                id, guild_id, user_id, amount, reason, reference_id,
                created_at_utc, old_xp, new_xp, old_level, new_level
            )
            SELECT
                id,
                guild_id,
                user_id,
                amount,
                CASE
                    WHEN reason IS NULL OR reason = '' THEN source
                    ELSE reason
                END,
                reference_id,
                created_at_utc,
                old_xp,
                new_xp,
                old_level,
                new_level
            FROM internal_xp_ledger;

            DROP TABLE internal_xp_ledger;
            ALTER TABLE internal_xp_ledger_migrated RENAME TO internal_xp_ledger;

            CREATE INDEX ix_internal_xp_ledger_user
                ON internal_xp_ledger (guild_id, user_id, created_at_utc);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ImportInviteLedgerIntoInternalXpAsync(
        SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO internal_xp_ledger (
                id, guild_id, user_id, amount, reason, reference_id, created_at_utc
            )
            SELECT
                lower(hex(randomblob(16))),
                guild_id,
                inviter_id,
                amount,
                CASE
                    WHEN action = 'revocation' THEN 'invite-revocation'
                    ELSE 'invite-reward'
                END,
                dispatch_reference_id,
                created_at_utc
            FROM invite_xp_ledger
            WHERE 1 = 1
            ON CONFLICT(reference_id) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RebuildLevelStateAsync(SqliteConnection connection)
    {
        var movements = new List<StoredXpMovement>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT id, guild_id, user_id, amount, reason
                FROM internal_xp_ledger
                ORDER BY guild_id, user_id, created_at_utc, rowid;
                """;
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                movements.Add(new StoredXpMovement(
                    reader.GetString(0),
                    ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    ulong.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetInt32(3),
                    reader.GetString(4)));
            }
        }

        var currentXpByUser =
            new Dictionary<(ulong GuildId, ulong UserId), XpSourceTotals>();
        using var transaction = connection.BeginTransaction();

        foreach (var movement in movements)
        {
            var key = (movement.GuildId, movement.UserId);
            currentXpByUser.TryGetValue(key, out var totals);
            totals ??= new XpSourceTotals();
            var oldXp = totals.TotalXp;
            totals = totals.Apply(ResolveXpSource(movement.Reason), movement.Amount);
            var newXp = totals.TotalXp;
            var effectiveAmount = checked((int)(newXp - oldXp));
            var oldState = LevelCalculator.CalculateLevelFromTotalXp(oldXp);
            var newState = LevelCalculator.CalculateLevelFromTotalXp(newXp);

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE internal_xp_ledger
                SET amount = $amount,
                    old_xp = $oldXp,
                    new_xp = $newXp,
                    old_level = $oldLevel,
                    new_level = $newLevel
                WHERE id = $id;
                """;
            AddParameter(update, "$amount", effectiveAmount);
            AddParameter(update, "$oldXp", oldXp);
            AddParameter(update, "$newXp", newXp);
            AddParameter(update, "$oldLevel", oldState.Level);
            AddParameter(update, "$newLevel", newState.Level);
            AddParameter(update, "$id", movement.Id);
            await update.ExecuteNonQueryAsync();
            currentXpByUser[key] = totals;
        }

        await using (var deleteAccounts = connection.CreateCommand())
        {
            deleteAccounts.Transaction = transaction;
            deleteAccounts.CommandText = "DELETE FROM internal_xp_accounts;";
            await deleteAccounts.ExecuteNonQueryAsync();
        }

        foreach (var pair in currentXpByUser)
        {
            var state = LevelCalculator.CalculateLevelFromTotalXp(pair.Value.TotalXp);
            await using var account = connection.CreateCommand();
            account.Transaction = transaction;
            account.CommandText =
                """
                INSERT INTO internal_xp_accounts (
                    guild_id, user_id, total_xp, current_level, current_level_progress,
                    message_xp, voice_xp, invite_xp
                )
                VALUES (
                    $guildId, $userId, $totalXp, $currentLevel, $progress,
                    $messageXp, $voiceXp, $inviteXp
                );
                """;
            AddParameter(account, "$guildId", Id(pair.Key.GuildId));
            AddParameter(account, "$userId", Id(pair.Key.UserId));
            AddParameter(account, "$totalXp", state.TotalXp);
            AddParameter(account, "$currentLevel", state.Level);
            AddParameter(account, "$progress", state.CurrentLevelProgress);
            AddParameter(account, "$messageXp", pair.Value.MessageXp);
            AddParameter(account, "$voiceXp", pair.Value.VoiceXp);
            AddParameter(account, "$inviteXp", pair.Value.InviteXp);
            await account.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    private static async Task ReconcileMessageXpAccountsAsync(
        SqliteConnection connection)
    {
        var accounts = new Dictionary<(ulong GuildId, ulong UserId), XpSourceTotals>();
        await using (var selectAccounts = connection.CreateCommand())
        {
            selectAccounts.CommandText =
                """
                SELECT guild_id, user_id, message_xp, voice_xp, invite_xp
                FROM internal_xp_accounts;
                """;
            await using var reader = await selectAccounts.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                accounts[(
                    ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture))] =
                    new XpSourceTotals(
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4));
            }
        }

        var messageXpByUser = new Dictionary<(ulong GuildId, ulong UserId), long>();
        await using (var selectMessages = connection.CreateCommand())
        {
            selectMessages.CommandText =
                """
                SELECT guild_id, user_id, SUM(xp)
                FROM message_xp
                GROUP BY guild_id, user_id;
                """;
            await using var reader = await selectMessages.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                messageXpByUser[(
                    ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture))] =
                    reader.GetInt64(2);
            }
        }

        await using (var selectBaselines = connection.CreateCommand())
        {
            selectBaselines.CommandText =
                """
                SELECT guild_id, user_id, message_xp
                FROM xp_snapshot_baselines
                WHERE message_xp != 0;
                """;
            await using var reader = await selectBaselines.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var key = (
                    ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
                messageXpByUser[key] =
                    messageXpByUser.GetValueOrDefault(key) + reader.GetInt64(2);
            }
        }

        var keys = accounts.Keys
            .Union(messageXpByUser.Keys)
            .Distinct()
            .ToArray();
        var runId = Guid.NewGuid().ToString("N");
        using var transaction = connection.BeginTransaction();

        foreach (var key in keys)
        {
            accounts.TryGetValue(key, out var oldTotals);
            oldTotals ??= new XpSourceTotals();
            var authoritativeMessageXp = messageXpByUser.GetValueOrDefault(key);
            if (oldTotals.MessageXp == authoritativeMessageXp)
            {
                continue;
            }

            var newTotals = oldTotals with { MessageXp = authoritativeMessageXp };
            var oldState = LevelCalculator.CalculateLevelFromTotalXp(oldTotals.TotalXp);
            var newState = LevelCalculator.CalculateLevelFromTotalXp(newTotals.TotalXp);
            var amount = checked((int)(newTotals.TotalXp - oldTotals.TotalXp));
            var timestamp = DateTimeOffset.UtcNow;

            await InsertXpMovementLogAsync(
                connection,
                transaction,
                key.GuildId,
                key.UserId,
                amount,
                "message-reconcile",
                $"message-reconcile:{runId}:{key.UserId}",
                timestamp,
                oldState,
                newState);
            await UpsertXpAccountAsync(
                connection,
                transaction,
                key.GuildId,
                key.UserId,
                newTotals,
                newState);
        }

        transaction.Commit();
    }

    private static async Task<XpMovementResult> ApplyXpMovementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        ulong userId,
        int amount,
        string reason,
        string referenceId,
        DateTimeOffset nowUtc,
        XpSource source)
    {
        long oldMessageXp = 0;
        long oldVoiceXp = 0;
        long oldInviteXp = 0;
        await using (var readAccount = connection.CreateCommand())
        {
            readAccount.Transaction = transaction;
            readAccount.CommandText =
                """
                SELECT message_xp, voice_xp, invite_xp
                FROM internal_xp_accounts
                WHERE guild_id = $guildId AND user_id = $userId;
                """;
            AddParameter(readAccount, "$guildId", Id(guildId));
            AddParameter(readAccount, "$userId", Id(userId));
            await using var reader = await readAccount.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                oldMessageXp = reader.GetInt64(0);
                oldVoiceXp = reader.GetInt64(1);
                oldInviteXp = reader.GetInt64(2);
            }
        }

        var oldXp = oldMessageXp + oldVoiceXp + oldInviteXp;
        var newMessageXp = oldMessageXp;
        var newVoiceXp = oldVoiceXp;
        var newInviteXp = oldInviteXp;

        switch (source)
        {
            case XpSource.Message:
                newMessageXp = Math.Max(0, oldMessageXp + amount);
                break;
            case XpSource.Voice:
                newVoiceXp = Math.Max(0, oldVoiceXp + amount);
                break;
            case XpSource.Invite:
                newInviteXp = Math.Max(0, oldInviteXp + amount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }

        var newXp = newMessageXp + newVoiceXp + newInviteXp;
        var effectiveAmount = checked((int)(newXp - oldXp));
        var oldState = LevelCalculator.CalculateLevelFromTotalXp(oldXp);
        var newState = LevelCalculator.CalculateLevelFromTotalXp(newXp);

        var inserted = await InsertXpMovementLogAsync(
            connection,
            transaction,
            guildId,
            userId,
            effectiveAmount,
            reason,
            referenceId,
            nowUtc,
            oldState,
            newState);
        if (inserted == 0)
        {
            return XpMovementResult.NotApplied(
                guildId,
                userId,
                reason,
                referenceId,
                nowUtc,
                oldState);
        }

        await UpsertXpAccountAsync(
            connection,
            transaction,
            guildId,
            userId,
            new XpSourceTotals(newMessageXp, newVoiceXp, newInviteXp),
            newState);
        return new XpMovementResult(
            true,
            guildId,
            userId,
            effectiveAmount,
            reason,
            referenceId,
            nowUtc,
            oldState.TotalXp,
            newState.TotalXp,
            oldState.Level,
            newState.Level,
            newState.CurrentLevelProgress,
            newState.XpForNextLevel);
    }

    private static async Task<int> InsertXpMovementLogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        ulong userId,
        int amount,
        string reason,
        string referenceId,
        DateTimeOffset timestamp,
        LevelState oldState,
        LevelState newState)
    {
        await using var ledger = connection.CreateCommand();
        ledger.Transaction = transaction;
        ledger.CommandText =
            """
            INSERT INTO internal_xp_ledger (
                id, guild_id, user_id, amount, reason, reference_id, created_at_utc,
                old_xp, new_xp, old_level, new_level
            )
            VALUES (
                $id, $guildId, $userId, $amount, $reason, $referenceId, $createdAt,
                $oldXp, $newXp, $oldLevel, $newLevel
            )
            ON CONFLICT(reference_id) DO NOTHING;
            """;
        AddParameter(ledger, "$id", Guid.NewGuid().ToString("N"));
        AddParameter(ledger, "$guildId", Id(guildId));
        AddParameter(ledger, "$userId", Id(userId));
        AddParameter(ledger, "$amount", amount);
        AddParameter(ledger, "$reason", reason);
        AddParameter(ledger, "$referenceId", referenceId);
        AddParameter(ledger, "$createdAt", Timestamp(timestamp));
        AddParameter(ledger, "$oldXp", oldState.TotalXp);
        AddParameter(ledger, "$newXp", newState.TotalXp);
        AddParameter(ledger, "$oldLevel", oldState.Level);
        AddParameter(ledger, "$newLevel", newState.Level);
        return await ledger.ExecuteNonQueryAsync();
    }

    private static async Task UpsertXpAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        ulong userId,
        XpSourceTotals totals,
        LevelState state)
    {
        await using var account = connection.CreateCommand();
        account.Transaction = transaction;
        account.CommandText =
            """
            INSERT INTO internal_xp_accounts (
                guild_id, user_id, total_xp, current_level, current_level_progress,
                message_xp, voice_xp, invite_xp
            )
            VALUES (
                $guildId, $userId, $totalXp, $currentLevel, $progress,
                $messageXp, $voiceXp, $inviteXp
            )
            ON CONFLICT (guild_id, user_id) DO UPDATE SET
                total_xp = excluded.total_xp,
                current_level = excluded.current_level,
                current_level_progress = excluded.current_level_progress,
                message_xp = excluded.message_xp,
                voice_xp = excluded.voice_xp,
                invite_xp = excluded.invite_xp;
            """;
        AddParameter(account, "$guildId", Id(guildId));
        AddParameter(account, "$userId", Id(userId));
        AddParameter(account, "$totalXp", state.TotalXp);
        AddParameter(account, "$currentLevel", state.Level);
        AddParameter(account, "$progress", state.CurrentLevelProgress);
        AddParameter(account, "$messageXp", totals.MessageXp);
        AddParameter(account, "$voiceXp", totals.VoiceXp);
        AddParameter(account, "$inviteXp", totals.InviteXp);
        await account.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $tableName
            LIMIT 1;
            """;
        AddParameter(command, "$tableName", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> HasNoStoredXpAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*)
                 FROM internal_xp_ledger
                 WHERE guild_id = $guildId),
                COALESCE((
                    SELECT SUM(total_xp)
                    FROM internal_xp_accounts
                    WHERE guild_id = $guildId
                ), 0);
            """;
        AddParameter(command, "$guildId", Id(guildId));
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0;
    }

    private static string NormalizeStoredRankColor(string? rankColor)
    {
        if (string.IsNullOrWhiteSpace(rankColor) ||
            rankColor.Length != 7 ||
            rankColor[0] != '#' ||
            !rankColor.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            return "#FFFFFF";
        }

        return rankColor.ToUpperInvariant();
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static XpSource ResolveXpSource(string reason)
    {
        if (reason.StartsWith("voice", StringComparison.OrdinalIgnoreCase))
        {
            return XpSource.Voice;
        }

        if (reason.StartsWith("invite", StringComparison.OrdinalIgnoreCase))
        {
            return XpSource.Invite;
        }

        return XpSource.Message;
    }

    private static async Task InsertInviteLedgerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InviteRewardRecord record,
        int amount,
        string action,
        string dispatchReferenceId,
        DateTimeOffset nowUtc)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO invite_xp_ledger (
                id, invite_reward_id, guild_id, member_id, inviter_id,
                invite_code, amount, action, dispatch_reference_id, created_at_utc
            )
            VALUES (
                $id, $inviteRewardId, $guildId, $memberId, $inviterId,
                $inviteCode, $amount, $action, $dispatchReferenceId, $createdAt
            );
            """;
        AddParameter(command, "$id", Guid.NewGuid().ToString("N"));
        AddParameter(command, "$inviteRewardId", record.Id);
        AddParameter(command, "$guildId", Id(record.GuildId));
        AddParameter(command, "$memberId", Id(record.MemberId));
        AddParameter(command, "$inviterId", Id(record.InviterId));
        AddParameter(command, "$inviteCode", record.InviteCode);
        AddParameter(command, "$amount", amount);
        AddParameter(command, "$action", action);
        AddParameter(command, "$dispatchReferenceId", dispatchReferenceId);
        AddParameter(command, "$createdAt", Timestamp(nowUtc));
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateHistoricalInviteRewardId(
        ulong guildId,
        ulong memberId,
        string inviteCode,
        DateTimeOffset joinedAtUtc)
    {
        var identity =
            $"{guildId}:{memberId}:{inviteCode}:{joinedAtUtc.UtcTicks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await inspect.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync();
    }

    private static InviteRewardRecord ReadInvite(SqliteDataReader reader)
    {
        return new InviteRewardRecord(
            reader.GetString(0),
            ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            ulong.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            ulong.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            reader.GetInt32(6),
            reader.GetInt32(7) != 0,
            reader.GetInt32(8) != 0);
    }

    private static void AddParameter(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value);
    }

    private static string Id(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed record InviteRewardRecord(
    string Id,
    ulong GuildId,
    ulong MemberId,
    ulong InviterId,
    string InviteCode,
    DateTimeOffset JoinedAtUtc,
    int RewardXp,
    bool RewardGiven,
    bool AutoProcess = true);

public sealed record VoiceRewardResult(
    int Minutes,
    int Xp,
    XpMovementResult? Movement)
{
    public static VoiceRewardResult Empty { get; } = new(0, 0, null);
}

public sealed record InviteRemovalResult(
    bool Deleted,
    XpMovementResult? Movement);

public sealed record HistoricalInviteMember(
    ulong MemberId,
    ulong InviterId,
    string InviteCode,
    DateTimeOffset JoinedAtUtc);

public sealed record InviteMemberBackfillResult(
    int MatchedMembers,
    int AlreadyTrackedMembers,
    int ImportedMembers,
    int PendingMembers,
    int RewardedMembers,
    int AwardedXp,
    IReadOnlyList<XpMovementResult> Movements)
{
    public static InviteMemberBackfillResult Empty { get; } =
        new(0, 0, 0, 0, 0, 0, []);
}

public sealed record MessageXpResult(
    bool Changed,
    XpMovementResult? Movement)
{
    public static MessageXpResult NotChanged { get; } = new(false, null);
}

public sealed record MessageXpSnapshot(
    ulong ChannelId,
    ulong MessageId,
    ulong UserId,
    int Xp,
    DateTimeOffset CreatedAtUtc);

public sealed record InternalXpEntry(
    ulong UserId,
    long TotalXp,
    int CurrentLevel,
    long CurrentLevelProgress,
    long MessageXp,
    long VoiceXp,
    long InviteXp,
    long MessageCount);

public sealed record PersistentUserProfile(
    ulong UserId,
    long MessageXp,
    long VoiceXp,
    long InviteXp,
    string RankColor)
{
    public long TotalXp => MessageXp + VoiceXp + InviteXp;
}

public sealed record PersistentRestoreResult(
    bool XpRestoreApplied,
    int RestoredXpUsers,
    int RestoredColors);

public sealed record XpMovementResult(
    bool Applied,
    ulong GuildId,
    ulong UserId,
    int Amount,
    string Reason,
    string ReferenceId,
    DateTimeOffset Timestamp,
    long OldXp,
    long NewXp,
    int OldLevel,
    int NewLevel,
    long CurrentLevelProgress,
    int XpForNextLevel)
{
    public bool LeveledUp => Applied && NewLevel > OldLevel;

    public static XpMovementResult NotApplied(
        ulong guildId,
        ulong userId,
        string reason,
        string referenceId,
        DateTimeOffset timestamp,
        LevelState currentState) =>
        new(
            false,
            guildId,
            userId,
            0,
            reason,
            referenceId,
            timestamp,
            currentState.TotalXp,
            currentState.TotalXp,
            currentState.Level,
            currentState.Level,
            currentState.CurrentLevelProgress,
            currentState.XpForNextLevel);
}

public sealed record XpMovementLogEntry(
    ulong UserId,
    int Amount,
    string Reason,
    DateTimeOffset Timestamp,
    long OldXp,
    long NewXp,
    int OldLevel,
    int NewLevel);

internal sealed record StoredXpMovement(
    string Id,
    ulong GuildId,
    ulong UserId,
    int Amount,
    string Reason);

internal sealed record XpSourceTotals(
    long MessageXp = 0,
    long VoiceXp = 0,
    long InviteXp = 0)
{
    public long TotalXp => MessageXp + VoiceXp + InviteXp;

    public XpSourceTotals Apply(XpSource source, int amount) =>
        source switch
        {
            XpSource.Message => this with { MessageXp = Math.Max(0, MessageXp + amount) },
            XpSource.Voice => this with { VoiceXp = Math.Max(0, VoiceXp + amount) },
            XpSource.Invite => this with { InviteXp = Math.Max(0, InviteXp + amount) },
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };
}

internal enum XpSource
{
    Message,
    Voice,
    Invite
}
