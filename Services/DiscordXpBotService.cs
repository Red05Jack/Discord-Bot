using System.Collections.Concurrent;
using System.Security.Cryptography;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using DiscordXpBot.Configuration;
using DiscordXpBot.Data;
using DiscordXpBot.Leveling;

namespace DiscordXpBot.Services;

public sealed class DiscordXpBotService : IAsyncDisposable
{
    private readonly BotOptions _options;
    private readonly BotDatabase _database;
    private readonly DiscordSocketClient _client;
    private readonly ConcurrentDictionary<string, InviteSnapshot> _inviteCache = [];
    private readonly SemaphoreSlim _inviteGate = new(1, 1);
    private readonly SemaphoreSlim _inviteRecalculateGate = new(1, 1);
    private readonly SemaphoreSlim _messageScanGate = new(1, 1);
    private readonly SemaphoreSlim _liveMessageGate = new(1, 1);
    private readonly HttpClient _httpClient = new();
    private readonly RankCardRenderer _rankCardRenderer = new();
    private readonly ConcurrentDictionary<ulong, MessageXpSnapshot> _recalculateMessageBuffer = [];
    private readonly ConcurrentDictionary<ulong, byte> _recalculateDeletedMessageIds = [];
    private CancellationToken _runToken;
    private SocketGuild? _guild;
    private ITextChannel? _botTextChannel;
    private ITextChannel? _levelUpChannel;
    private Task? _inviteRewardLoop;
    private Task? _voiceCheckpointLoop;
    private Task? _messageHistoryTask;
    private int _readyInitialized;
    private int _messageRecalculationInProgress;

    public DiscordXpBotService(BotOptions options, BotDatabase database)
    {
        _options = options;
        _database = database;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMembers |
                GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent |
                GatewayIntents.GuildVoiceStates |
                GatewayIntents.GuildInvites,
            AlwaysDownloadUsers = true,
            LogGatewayIntentWarnings = true
        });

        _client.Log += OnLogAsync;
        _client.Ready += OnReadyAsync;
        _client.UserJoined += OnUserJoinedAsync;
        _client.UserLeft += OnUserLeftAsync;
        _client.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
        _client.InviteCreated += OnInviteCreatedAsync;
        _client.InviteDeleted += OnInviteDeletedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.MessageDeleted += OnMessageDeletedAsync;
        _client.MessagesBulkDeleted += OnMessagesBulkDeletedAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _runToken = cancellationToken;
        await _client.LoginAsync(TokenType.Bot, _options.Discord.Token);
        await _client.StartAsync();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            if (_options.Voice.Enabled)
            {
                await CheckpointAllVoiceUsersAsync(announce: false);
            }

            await _client.StopAsync();
            await WaitForBackgroundTaskAsync(_inviteRewardLoop);
            await WaitForBackgroundTaskAsync(_voiceCheckpointLoop);
            await WaitForBackgroundTaskAsync(_messageHistoryTask);
        }
    }

    private async Task OnReadyAsync()
    {
        if (Interlocked.Exchange(ref _readyInitialized, 1) != 0)
        {
            return;
        }

        _guild = ResolveConfiguredGuild();
        _options.Discord.GuildId = _guild.Id;
        await _database.EnsureUserAccountsAsync(
            _guild.Id,
            _guild.Users.Select(user => user.Id));

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Verbunden als {_client.CurrentUser} mit {_guild.Name}.");
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Verwendete GuildId: {_guild.Id}.");
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Debug-Protokollierung: " +
            $"{(_options.Debug.Enabled ? "aktiv" : "inaktiv")}.");

        if (_options.BotChannel.Enabled || _options.Messages.Enabled)
        {
            try
            {
                _botTextChannel = await GetOrCreateBotTextChannelAsync();
            }
            catch (Exception exception)
            {
                await LogExceptionAsync("BotChannelInitialization", exception);
            }
        }

        if (_options.Levels.Enabled)
        {
            try
            {
                _levelUpChannel = await GetOrCreateLevelUpChannelAsync();
            }
            catch (Exception exception)
            {
                await LogExceptionAsync("LevelChannelInitialization", exception);
            }
        }

        if (_options.InviteTracking.Enabled)
        {
            try
            {
                await RefreshInviteCacheAsync();
                _inviteRewardLoop = RunInviteRewardLoopAsync(_runToken);
            }
            catch (Exception exception)
            {
                await LogExceptionAsync("InviteTrackingInitialization", exception);
            }
        }

        if (_options.Voice.Enabled)
        {
            try
            {
                await SynchronizeVoiceSessionsFromDiscordAsync();
                await CheckpointAllVoiceUsersAsync(announce: false);
            }
            catch (Exception exception)
            {
                await LogExceptionAsync("VoiceTrackingInitialization", exception);
            }

            _voiceCheckpointLoop = RunVoiceCheckpointLoopAsync(_runToken);
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Voice-Live-Tracking aktiv: " +
                $"Prüfung alle {_options.Voice.CheckpointIntervalMinutes:N2} Minute(n), " +
                $"Belohnung je {_options.Voice.RewardBlockMinutes} Minuten.");
        }

        if (_options.Discord.RegisterSlashCommands)
        {
            try
            {
                await RegisterSlashCommandsAsync();
            }
            catch (Exception exception)
            {
                await LogExceptionAsync("SlashCommandInitialization", exception);
            }
        }

        if (_options.Messages.Enabled)
        {
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Nachrichten-Live-Tracking aktiv.");
        }
    }

    private SocketGuild ResolveConfiguredGuild()
    {
        if (_options.Discord.GuildId != 0)
        {
            var configuredGuild = _client.GetGuild(_options.Discord.GuildId);
            if (configuredGuild is not null)
            {
                return configuredGuild;
            }

            if (_client.Guilds.Count == 1)
            {
                var detectedGuild = _client.Guilds.Single();
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] Warnung: Die konfigurierte GuildId " +
                    $"{_options.Discord.GuildId} wurde nicht gefunden. Verwende automatisch " +
                    $"{detectedGuild.Name} ({detectedGuild.Id}).");
                return detectedGuild;
            }

            var configuredAvailableGuilds = string.Join(
                ", ",
                _client.Guilds.Select(guild => $"{guild.Name} ({guild.Id})"));
            throw new InvalidOperationException(
                $"Der Bot ist nicht auf dem konfigurierten Server " +
                $"{_options.Discord.GuildId}. Verfügbare Server: " +
                $"{configuredAvailableGuilds}");
        }

        if (_client.Guilds.Count == 1)
        {
            return _client.Guilds.Single();
        }

        if (_client.Guilds.Count == 0)
        {
            throw new InvalidOperationException(
                "Discord.GuildId ist 0 und der Bot ist auf keinem Discord-Server. " +
                "Lade den Bot zuerst auf einen Server ein.");
        }

        var availableGuilds = string.Join(
            ", ",
            _client.Guilds.Select(guild => $"{guild.Name} ({guild.Id})"));
        throw new InvalidOperationException(
            "Discord.GuildId ist 0, aber der Bot ist auf mehreren Servern. " +
            $"Setze eine dieser IDs in appsettings.json: {availableGuilds}");
    }

    private async Task OnUserJoinedAsync(SocketGuildUser member)
    {
        if (member.Guild.Id != _options.Discord.GuildId)
        {
            return;
        }

        await _database.EnsureUserAccountsAsync(member.Guild.Id, [member.Id]);

        if (!_options.InviteTracking.Enabled)
        {
            return;
        }

        try
        {
            var usedInvite = await DetectUsedInviteAsync(member.Guild);
            if (usedInvite?.InviterId is null)
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] Einladung für {member} konnte nicht zugeordnet werden.");
                return;
            }

            if (member.IsBot && !_options.InviteTracking.RewardBotInvites)
            {
                return;
            }

            if (usedInvite.InviterId == member.Id && !_options.InviteTracking.AllowSelfInvites)
            {
                return;
            }

            var rewardXp = RandomNumberGenerator.GetInt32(
                _options.InviteTracking.RewardMinXp,
                _options.InviteTracking.RewardMaxXp + 1);

            await _database.UpsertInviteAsync(new InviteRewardRecord(
                Guid.NewGuid().ToString("N"),
                member.Guild.Id,
                member.Id,
                usedInvite.InviterId.Value,
                usedInvite.Code,
                DateTimeOffset.UtcNow,
                rewardXp,
                false));

            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Invite gespeichert: {member.Id} durch " +
                $"{usedInvite.InviterId.Value}, vorgemerkt {rewardXp} XP.");
        }
        catch (Exception exception)
        {
            await LogExceptionAsync("UserJoined", exception);
        }
    }

    private async Task OnUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        if (!_options.InviteTracking.Enabled ||
            guild.Id != _options.Discord.GuildId)
        {
            return;
        }

        try
        {
            var invite = await _database.GetInviteAsync(guild.Id, user.Id);
            if (invite is null)
            {
                return;
            }

            var removal = await _database.RevokeInviteAndDeleteAsync(
                invite,
                DateTimeOffset.UtcNow);
            LogXpMovement(removal.Movement);

            if (removal.Deleted && invite.RewardGiven)
            {
                await AnnounceAsync(
                    _options.Announcements.AnnounceInviteRevocations,
                    FormatMessage(
                        _options.Announcements.InviteRevocationMessage,
                        invite.InviterId,
                        invite.MemberId,
                        invite.RewardXp));
            }
        }
        catch (Exception exception)
        {
            await LogExceptionAsync("UserLeft", exception);
        }
    }

    private async Task OnVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        if (user is not SocketGuildUser guildUser ||
            guildUser.Guild.Id != _options.Discord.GuildId)
        {
            return;
        }

        var beforeChannel = before.VoiceChannel;
        var afterChannel = after.VoiceChannel;
        if (beforeChannel?.Id == afterChannel?.Id)
        {
            return;
        }

        LogVoiceMovement(guildUser, beforeChannel, afterChannel);

        if (!_options.Voice.Enabled ||
            user.IsBot && !_options.Voice.RewardBots)
        {
            return;
        }

        try
        {
            if (IsEligibleVoiceChannel(beforeChannel))
            {
                var reward = await _database.RewardVoiceTimeAsync(
                    guildUser.Guild.Id,
                    user.Id,
                    DateTimeOffset.UtcNow,
                    _options.Voice.MinXpPerFiveMinutes,
                    _options.Voice.MaxXpPerFiveMinutes,
                    _options.Voice.RewardBlockMinutes,
                    deleteSession: true);

                LogXpMovement(reward.Movement);
                await NotifyLevelUpAsync(reward.Movement);
                await AnnounceVoiceRewardAsync(user.Id, reward);
            }

            if (IsEligibleVoiceChannel(afterChannel))
            {
                await _database.StartVoiceSessionAsync(
                    guildUser.Guild.Id,
                    user.Id,
                    afterChannel!.Id,
                    DateTimeOffset.UtcNow);
            }
        }
        catch (Exception exception)
        {
            await LogExceptionAsync("VoiceStateUpdated", exception);
        }
    }

    private void LogVoiceMovement(
        SocketGuildUser user,
        SocketVoiceChannel? beforeChannel,
        SocketVoiceChannel? afterChannel)
    {
        if (!_options.Debug.Enabled)
        {
            return;
        }

        var movement = (beforeChannel, afterChannel) switch
        {
            (null, not null) => "VOICE-BEITRITT",
            (not null, null) => "VOICE-AUSTRITT",
            _ => "VOICE-WECHSEL"
        };

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [DEBUG] [{movement}] " +
            $"Benutzer={user.DisplayName} ({user.Id}) | " +
            $"Von={FormatVoiceChannel(beforeChannel)} | " +
            $"Nach={FormatVoiceChannel(afterChannel)}");
    }

    private static string FormatVoiceChannel(SocketVoiceChannel? channel)
    {
        return channel is null
            ? "Kein Voice-Chat"
            : $"{channel.Name} ({channel.Id})";
    }

    private Task OnInviteCreatedAsync(SocketInvite invite)
    {
        if (invite.GuildId == _options.Discord.GuildId)
        {
            _inviteCache[invite.Code] = new InviteSnapshot(
                invite.Uses,
                invite.Inviter?.Id);
        }

        return Task.CompletedTask;
    }

    private Task OnInviteDeletedAsync(SocketGuildChannel channel, string code)
    {
        if (channel.Guild.Id == _options.Discord.GuildId)
        {
            _inviteCache.TryRemove(code, out _);
        }

        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Channel is not SocketGuildChannel guildChannel ||
            guildChannel.Guild.Id != _options.Discord.GuildId)
        {
            return;
        }

        try
        {
            if (message is SocketUserMessage userMessage &&
                string.Equals(
                    userMessage.Content.Trim(),
                    "!recalculate",
                    StringComparison.OrdinalIgnoreCase))
            {
                await HandleRecalculateCommandAsync(userMessage, guildChannel.Guild);
                return;
            }

            if (message is SocketUserMessage rankMessage &&
                string.Equals(
                    rankMessage.Content.Trim(),
                    "!myrank",
                    StringComparison.OrdinalIgnoreCase))
            {
                await HandleMyRankCommandAsync(rankMessage, guildChannel.Guild);
                return;
            }

            if (message is SocketUserMessage inviteRecalculateMessage &&
                string.Equals(
                    inviteRecalculateMessage.Content.Trim(),
                    "!recalculate-invites",
                    StringComparison.OrdinalIgnoreCase))
            {
                await HandleInviteRecalculateCommandAsync(
                    inviteRecalculateMessage,
                    guildChannel.Guild);
                return;
            }

            if (!_options.Messages.Enabled || !IsRewardableMessage(message))
            {
                return;
            }

            var xp = MessageXpCalculator.Calculate(
                message.Id,
                _options.Messages.MinXp,
                _options.Messages.MaxXp);
            var snapshot = new MessageXpSnapshot(
                message.Channel.Id,
                message.Id,
                message.Author.Id,
                xp,
                message.Timestamp);

            await _liveMessageGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _messageRecalculationInProgress) == 1)
                {
                    _recalculateMessageBuffer[message.Id] = snapshot;
                    _recalculateDeletedMessageIds.TryRemove(message.Id, out _);
                    if (_options.Debug.Enabled)
                    {
                        Console.WriteLine(
                            $"[{DateTimeOffset.Now:O}] [DEBUG] [NACHRICHT-GEPUFFERT] " +
                            $"Benutzer={message.Author} ({message.Author.Id}) | " +
                            $"Kanal={message.Channel.Name} ({message.Channel.Id}) | " +
                            $"Nachricht={message.Id} | XP={xp}");
                    }

                    return;
                }

                var result = await _database.RegisterMessageXpAsync(
                    guildChannel.Guild.Id,
                    message.Channel.Id,
                    message.Id,
                    message.Author.Id,
                    xp,
                    message.Timestamp);

                LogXpMovement(result.Movement);
                await NotifyLevelUpAsync(result.Movement);
                if (_options.Debug.Enabled && result.Changed)
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [DEBUG] [NACHRICHT-XP] " +
                        $"Benutzer={message.Author} ({message.Author.Id}) | " +
                        $"Kanal={message.Channel.Name} ({message.Channel.Id}) | " +
                        $"Nachricht={message.Id} | XP={xp}");
                }
                else if (_options.Debug.Enabled)
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [DEBUG] [NACHRICHT-DUPLIKAT] " +
                        $"Nachricht={message.Id} wurde bereits verarbeitet.");
                }
            }
            finally
            {
                _liveMessageGate.Release();
            }
        }
        catch (Exception exception)
        {
            await LogExceptionAsync($"MessageReceived:{message.Id}", exception);
        }
    }

    private async Task HandleRecalculateCommandAsync(
        SocketUserMessage message,
        SocketGuild guild)
    {
        if (message.Author is not SocketGuildUser guildUser ||
            !guildUser.GuildPermissions.ManageGuild)
        {
            await message.Channel.SendMessageAsync(
                "You need the **Manage Server** permission to run `!recalculate`.");
            return;
        }

        if (Interlocked.CompareExchange(
                ref _messageRecalculationInProgress,
                1,
                0) != 0)
        {
            await message.Channel.SendMessageAsync(
                "A message XP recalculation is already running.");
            return;
        }

        _recalculateMessageBuffer.Clear();
        _recalculateDeletedMessageIds.Clear();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            if (_botTextChannel is null &&
                message.Channel is ITextChannel commandChannel)
            {
                _botTextChannel = commandChannel;
            }

            if (_botTextChannel is null)
            {
                throw new InvalidOperationException(
                    "No text channel is available for recalculation output.");
            }

            await message.Channel.SendMessageAsync(
                "XP recalculation started. Messages and currently available invite usages " +
                "will be recalculated. Live voice and invite tracking remain active.");
            _messageHistoryTask = ScanMessageHistoryAndPublishAsync(startedAt, _runToken);
        }
        catch
        {
            Interlocked.Exchange(ref _messageRecalculationInProgress, 0);
            throw;
        }
    }

    private async Task HandleMyRankCommandAsync(
        SocketUserMessage message,
        SocketGuild guild)
    {
        if (message.Author is not SocketGuildUser guildUser)
        {
            return;
        }

        var account = await _database.GetInternalXpAccountAsync(
            guild.Id,
            guildUser.Id);
        var leaderboard = await _database.GetInternalXpLeaderboardAsync(guild.Id);
        var xpByUser = leaderboard.ToDictionary(entry => entry.UserId);
        var rankedUserIds = guild.Users
            .Where(user => _options.Messages.RewardBots || !user.IsBot)
            .Select(user => user.Id)
            .OrderByDescending(userId => xpByUser.GetValueOrDefault(userId)?.TotalXp ?? 0)
            .ThenBy(userId => userId)
            .ToArray();
        var rankIndex = Array.IndexOf(rankedUserIds, guildUser.Id);
        var rank = rankIndex >= 0 ? rankIndex + 1 : rankedUserIds.Length + 1;

        MemoryStream? avatarStream = null;
        try
        {
            var avatarUrl = guildUser.GetDisplayAvatarUrl(ImageFormat.Png, 256);
            var avatarBytes = await _httpClient.GetByteArrayAsync(avatarUrl, _runToken);
            avatarStream = new MemoryStream(avatarBytes, writable: false);
        }
        catch (Exception exception)
        {
            await LogExceptionAsync($"RankAvatar:{guildUser.Id}", exception);
        }

        await using (avatarStream)
        await using (var rankCard = _rankCardRenderer.Render(
                         new RankCardData(
                             guildUser.Username,
                             rank,
                             account.CurrentLevel,
                             account.CurrentLevelProgress,
                             LevelCalculator.GetXpForNextLevel(account.CurrentLevel)),
                         avatarStream))
        {
            await message.Channel.SendFileAsync(rankCard, "rank.png");
        }
    }

    private async Task HandleInviteRecalculateCommandAsync(
        SocketUserMessage message,
        SocketGuild guild)
    {
        if (message.Author is not SocketGuildUser guildUser ||
            !guildUser.GuildPermissions.ManageGuild)
        {
            await message.Channel.SendMessageAsync(
                "You need the **Manage Server** permission to run " +
                "`!recalculate-invites`.");
            return;
        }

        if (!_options.InviteTracking.Enabled)
        {
            await message.Channel.SendMessageAsync("Invite tracking is disabled.");
            return;
        }

        if (!await _inviteRecalculateGate.WaitAsync(0))
        {
            await message.Channel.SendMessageAsync(
                "An invite recalculation is already running.");
            return;
        }

        try
        {
            await message.Channel.SendMessageAsync(
                "Invite recalculation started. Message and voice XP remain unchanged.");
            var result = await BackfillInviteMembersFromDiscordCoreAsync();
            var additionallyRewarded = await CheckDueInviteRewardsAsync();
            await message.Channel.SendMessageAsync(
                $"""
                Invite recalculation finished.
                **Matched members:** {result.MatchedMembers:N0}
                **Newly imported:** {result.ImportedMembers:N0}
                **Waiting for 7 days:** {result.PendingMembers:N0}
                **Immediately rewarded:** {result.RewardedMembers:N0}
                **Other due rewards:** {additionallyRewarded:N0}
                **Awarded XP:** {result.AwardedXp:N0}
                """);
        }
        finally
        {
            _inviteRecalculateGate.Release();
        }
    }

    private async Task OnMessageDeletedAsync(
        Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel)
    {
        if (!_options.Messages.Enabled ||
            !channel.HasValue ||
            channel.Value is not IGuildChannel guildChannel ||
            guildChannel.GuildId != _options.Discord.GuildId)
        {
            return;
        }

        try
        {
            await _liveMessageGate.WaitAsync();
            try
            {
                if (Volatile.Read(ref _messageRecalculationInProgress) == 1)
                {
                    _recalculateMessageBuffer.TryRemove(message.Id, out _);
                    _recalculateDeletedMessageIds[message.Id] = 0;
                    return;
                }

                var result = await _database.RemoveMessageXpAsync(
                    guildChannel.Guild.Id,
                    message.Id);
                LogXpMovement(result.Movement);
                if (_options.Debug.Enabled && result.Changed)
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [DEBUG] [NACHRICHT-GELÖSCHT] " +
                        $"Kanal={channel.Value.Name} ({channel.Id}) | Nachricht={message.Id}");
                }
            }
            finally
            {
                _liveMessageGate.Release();
            }
        }
        catch (Exception exception)
        {
            await LogExceptionAsync($"MessageDeleted:{message.Id}", exception);
        }
    }

    private async Task OnMessagesBulkDeletedAsync(
        IReadOnlyCollection<Cacheable<IMessage, ulong>> messages,
        Cacheable<IMessageChannel, ulong> channel)
    {
        foreach (var message in messages)
        {
            await OnMessageDeletedAsync(message, channel);
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.GuildId != _options.Discord.GuildId)
        {
            await command.RespondAsync("Dieser Bot ist für einen anderen Server konfiguriert.", ephemeral: true);
            return;
        }

        try
        {
            switch (command.Data.Name)
            {
                case "einladungen-nachbearbeiten":
                    await HandleInviteBackfillCommandAsync(command);
                    break;
                case "xp-liste":
                    await HandleXpListCommandAsync(command);
                    break;
            }
        }
        catch (Exception exception)
        {
            await LogExceptionAsync($"SlashCommand:{command.Data.Name}", exception);
            if (!command.HasResponded)
            {
                await command.RespondAsync(
                    "Beim Ausführen des Befehls ist ein Fehler aufgetreten.",
                    ephemeral: true);
            }
        }
    }

    private async Task HandleInviteBackfillCommandAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser guildUser ||
            !guildUser.GuildPermissions.ManageGuild)
        {
            await command.RespondAsync(
                "Dafür brauchst du die Berechtigung „Server verwalten“.",
                ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);
        var enabled = await _database.EnableHistoricalInvitesAsync(_options.Discord.GuildId);
        var rewarded = await CheckDueInviteRewardsAsync();
        await command.ModifyOriginalResponseAsync(message =>
        {
            message.Content =
                $"Nachbearbeitung aktiviert: **{enabled}** bisherige SQL-Einladungen freigeschaltet, " +
                $"davon **{rewarded}** jetzt vergütet. Nicht erreichte 7-Tage-Fristen werden später geprüft.";
        });
    }

    private async Task HandleXpListCommandAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser guildUser ||
            !guildUser.GuildPermissions.ManageGuild)
        {
            await command.RespondAsync(
                "Dafür brauchst du die Berechtigung „Server verwalten“.",
                ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);
        await PublishInternalXpLeaderboardAsync();
        await command.ModifyOriginalResponseAsync(message =>
        {
            message.Content = "Die aktuelle interne XP-Liste wurde im Bot-Textkanal ausgegeben.";
        });
    }

    private async Task RegisterSlashCommandsAsync()
    {
        if (_guild is null)
        {
            return;
        }

        var backfill = new SlashCommandBuilder()
            .WithName("einladungen-nachbearbeiten")
            .WithDescription("Aktiviert die Verarbeitung alter, bereits in SQL gespeicherter Einladungen.")
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .Build();

        var xpList = new SlashCommandBuilder()
            .WithName("xp-liste")
            .WithDescription("Gibt die aktuelle interne XP-Liste im Bot-Textkanal aus.")
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .Build();

        await _guild.BulkOverwriteApplicationCommandAsync([backfill, xpList]);
        Console.WriteLine($"[{DateTimeOffset.Now:O}] Slash-Commands registriert.");
    }

    private async Task<DetectedInvite?> DetectUsedInviteAsync(SocketGuild guild)
    {
        await _inviteGate.WaitAsync();
        try
        {
            var currentInvites = await guild.GetInvitesAsync();
            var current = currentInvites.ToDictionary(
                invite => invite.Code,
                invite => new InviteSnapshot(invite.Uses ?? 0, invite.Inviter?.Id));

            var candidate = current
                .Where(pair =>
                    _inviteCache.TryGetValue(pair.Key, out var previous) &&
                    pair.Value.Uses > previous.Uses)
                .OrderByDescending(pair =>
                    pair.Value.Uses - _inviteCache[pair.Key].Uses)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(candidate.Key))
            {
                ReplaceInviteCache(current);
                return null;
            }

            var oldSnapshot = _inviteCache[candidate.Key];
            _inviteCache[candidate.Key] = candidate.Value with
            {
                Uses = oldSnapshot.Uses + 1
            };

            foreach (var pair in current)
            {
                if (pair.Key == candidate.Key)
                {
                    continue;
                }

                if (!_inviteCache.TryGetValue(pair.Key, out var cached))
                {
                    _inviteCache[pair.Key] = pair.Value;
                }
                else if (pair.Value.Uses <= cached.Uses)
                {
                    _inviteCache[pair.Key] = pair.Value;
                }
            }

            foreach (var cachedCode in _inviteCache.Keys)
            {
                if (!current.ContainsKey(cachedCode))
                {
                    _inviteCache.TryRemove(cachedCode, out _);
                }
            }

            return new DetectedInvite(candidate.Key, candidate.Value.InviterId);
        }
        finally
        {
            _inviteGate.Release();
        }
    }

    private async Task RefreshInviteCacheAsync()
    {
        if (_guild is null)
        {
            return;
        }

        await _inviteGate.WaitAsync();
        try
        {
            var invites = await _guild.GetInvitesAsync();
            ReplaceInviteCache(invites.ToDictionary(
                invite => invite.Code,
                invite => new InviteSnapshot(invite.Uses ?? 0, invite.Inviter?.Id)));
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] {_inviteCache.Count} Einladungen zwischengespeichert.");
        }
        catch (HttpException exception) when (exception.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "Der Bot benötigt die Discord-Berechtigung „Server verwalten“, " +
                "um Einladungen auszulesen.",
                exception);
        }
        finally
        {
            _inviteGate.Release();
        }
    }

    private void ReplaceInviteCache(IReadOnlyDictionary<string, InviteSnapshot> source)
    {
        _inviteCache.Clear();
        foreach (var pair in source)
        {
            _inviteCache[pair.Key] = pair.Value;
        }
    }

    private async Task<InviteMemberBackfillResult>
        BackfillInviteMembersFromDiscordAsync()
    {
        await _inviteRecalculateGate.WaitAsync(_runToken);
        try
        {
            return await BackfillInviteMembersFromDiscordCoreAsync();
        }
        finally
        {
            _inviteRecalculateGate.Release();
        }
    }

    private async Task<InviteMemberBackfillResult>
        BackfillInviteMembersFromDiscordCoreAsync()
    {
        if (_guild is null || !_options.InviteTracking.Enabled)
        {
            return InviteMemberBackfillResult.Empty;
        }

        var matchedMembers = new Dictionary<ulong, HistoricalInviteMember>();
        var seenCursors = new HashSet<(ulong UserId, long JoinedAtUnixMilliseconds)>();
        var properties = new MemberSearchPropertiesV2
        {
            Sort = MemberSearchV2SortType.MemberSinceOldestFirst,
            OrQuery = new MemberSearchFilter
            {
                JoinSourceType = new MemberSearchIntQuery
                {
                    OrQuery = [(int)JoinSourceType.InviteCode]
                }
            }
        };

        while (true)
        {
            var page = await _guild.SearchUsersAsyncV2(1000, properties);
            if (page.Members.Count == 0)
            {
                break;
            }

            foreach (var memberData in page.Members)
            {
                if (memberData.InviterId is not { } inviterId ||
                    string.IsNullOrWhiteSpace(memberData.SourceInviteCode) ||
                    memberData.User.JoinedAt is not { } joinedAt ||
                    memberData.User.IsBot && !_options.InviteTracking.RewardBotInvites ||
                    inviterId == memberData.User.Id &&
                    !_options.InviteTracking.AllowSelfInvites)
                {
                    continue;
                }

                matchedMembers[memberData.User.Id] = new HistoricalInviteMember(
                    memberData.User.Id,
                    inviterId,
                    memberData.SourceInviteCode,
                    joinedAt);
            }

            var lastMember = page.Members.Last();
            if (page.PageResultCount < 1000 ||
                matchedMembers.Count >= page.TotalResultCount ||
                lastMember.User.JoinedAt is not { } lastJoinedAt)
            {
                break;
            }

            var cursor = (
                lastMember.User.Id,
                lastJoinedAt.ToUnixTimeMilliseconds());
            if (!seenCursors.Add(cursor))
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [SCAN] INVITES WARNUNG | " +
                    "Discord lieferte denselben Pagination-Cursor erneut.");
                break;
            }

            properties.After = new MemberSearchPaginationFilter(
                lastMember.User.Id,
                lastJoinedAt);
        }

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [SCAN] INVITES START | " +
            $"Mitglieder mit Invite-Code und Einlader={matchedMembers.Count}");

        var now = DateTimeOffset.UtcNow;
        var result = await _database.BackfillInviteMembersAsync(
            _guild.Id,
            matchedMembers.Values.ToArray(),
            now.AddDays(-_options.InviteTracking.RetentionDays),
            _options.InviteTracking.RewardMinXp,
            _options.InviteTracking.RewardMaxXp,
            now);

        foreach (var movement in result.Movements)
        {
            LogXpMovement(movement);
        }

        foreach (var userMovements in result.Movements.GroupBy(movement => movement.UserId))
        {
            var ordered = userMovements.ToArray();
            var first = ordered[0];
            var last = ordered[^1];
            await NotifyLevelUpAsync(last with
            {
                Amount = ordered.Sum(movement => movement.Amount),
                OldXp = first.OldXp,
                OldLevel = first.OldLevel
            });
        }

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [SCAN] INVITES ENDE | " +
            $"Bereits erfasst={result.AlreadyTrackedMembers} | " +
            $"Neu importiert={result.ImportedMembers} | " +
            $"Noch keine 7 Tage={result.PendingMembers} | " +
            $"Vergütet={result.RewardedMembers} | " +
            $"Vergebene XP={result.AwardedXp}");
        return result;
    }

    private async Task RunInviteRewardLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_options.InviteTracking.CheckIntervalMinutes));

        await CheckDueInviteRewardsAsync();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await CheckDueInviteRewardsAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task<int> CheckDueInviteRewardsAsync()
    {
        if (_guild is null)
        {
            return 0;
        }

        var rewardCount = 0;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.InviteTracking.RetentionDays);
            var dueInvites = await _database.GetDueInvitesAsync(_guild.Id, cutoff);

            foreach (var invite in dueInvites)
            {
                var member = await ((IGuild)_guild).GetUserAsync(
                    invite.MemberId,
                    CacheMode.AllowDownload);

                if (member is null)
                {
                    var removal = await _database.RevokeInviteAndDeleteAsync(
                        invite,
                        DateTimeOffset.UtcNow);
                    LogXpMovement(removal.Movement);
                    continue;
                }

                var movement = await _database.MarkInviteRewardGivenAsync(
                    invite,
                    DateTimeOffset.UtcNow);
                if (movement is null)
                {
                    continue;
                }

                LogXpMovement(movement);
                await NotifyLevelUpAsync(movement);
                rewardCount++;
                await AnnounceAsync(
                    _options.Announcements.AnnounceInviteRewards,
                    FormatMessage(
                        _options.Announcements.InviteRewardMessage,
                        invite.InviterId,
                        invite.MemberId,
                        invite.RewardXp));
            }

        }
        catch (Exception exception)
        {
            await LogExceptionAsync("InviteRewardLoop", exception);
        }

        return rewardCount;
    }

    private async Task RunVoiceCheckpointLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_options.Voice.CheckpointIntervalMinutes));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await CheckpointAllVoiceUsersAsync(
                        _options.Announcements.AnnounceVoiceRewards);
                }
                catch (Exception exception)
                {
                    await LogExceptionAsync("VoiceCheckpointLoop", exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            await LogExceptionAsync("VoiceCheckpointTimer", exception);
        }
    }

    private async Task SynchronizeVoiceSessionsFromDiscordAsync()
    {
        if (_guild is null)
        {
            return;
        }

        var activeUsers = _guild.Users
            .Where(user =>
                (!user.IsBot || _options.Voice.RewardBots) &&
                IsEligibleVoiceChannel(user.VoiceChannel))
            .Select(user => (user.Id, user.VoiceChannel!.Id))
            .ToArray();

        var synchronizedUsers = await _database.SynchronizeVoiceSessionsAsync(
            _guild.Id,
            activeUsers,
            DateTimeOffset.UtcNow);
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Voice-Sitzungen synchronisiert: " +
            $"{synchronizedUsers} aktive Benutzer.");
    }

    private async Task CheckpointAllVoiceUsersAsync(bool announce)
    {
        if (_guild is null)
        {
            return;
        }

        var activeUsers = _guild.Users.Where(user =>
                (!user.IsBot || _options.Voice.RewardBots) &&
                IsEligibleVoiceChannel(user.VoiceChannel))
            .ToArray();

        if (_options.Debug.Enabled)
        {
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] [DEBUG] [VOICE-CHECKPOINT] " +
                $"Aktive Benutzer={activeUsers.Length}");
        }

        foreach (var user in activeUsers)
        {
            try
            {
                var sessionRecovered = await _database.EnsureVoiceSessionAsync(
                    _guild.Id,
                    user.Id,
                    user.VoiceChannel!.Id,
                    DateTimeOffset.UtcNow);
                if (sessionRecovered && _options.Debug.Enabled)
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [DEBUG] [VOICE-SITZUNG-ERSTELLT] " +
                        $"Benutzer={user.DisplayName} ({user.Id}) | " +
                        $"Kanal={user.VoiceChannel.Name} ({user.VoiceChannel.Id})");
                }

                var reward = await _database.RewardVoiceTimeAsync(
                    _guild.Id,
                    user.Id,
                    DateTimeOffset.UtcNow,
                    _options.Voice.MinXpPerFiveMinutes,
                    _options.Voice.MaxXpPerFiveMinutes,
                    _options.Voice.RewardBlockMinutes,
                    deleteSession: false);

                LogXpMovement(reward.Movement);
                await NotifyLevelUpAsync(reward.Movement);
                if (announce)
                {
                    await AnnounceVoiceRewardAsync(user.Id, reward);
                }
            }
            catch (Exception exception)
            {
                await LogExceptionAsync($"VoiceCheckpoint:{user.Id}", exception);
            }
        }
    }

    private bool IsEligibleVoiceChannel(SocketVoiceChannel? channel)
    {
        if (channel is null || _options.Voice.ExcludedChannelIds.Contains(channel.Id))
        {
            return false;
        }

        return _options.Voice.EligibleChannelIds.Count == 0 ||
               _options.Voice.EligibleChannelIds.Contains(channel.Id);
    }

    private async Task AnnounceVoiceRewardAsync(ulong userId, VoiceRewardResult reward)
    {
        if (reward.Xp <= 0)
        {
            return;
        }

        await AnnounceAsync(
            _options.Announcements.AnnounceVoiceRewards,
            FormatMessage(
                _options.Announcements.VoiceRewardMessage,
                userId,
                userId,
                reward.Xp,
            reward.Minutes));
    }

    private bool IsRewardableMessage(IMessage message)
    {
        return message.Source == MessageSource.User &&
               _guild?.GetUser(message.Author.Id) is not null &&
               !string.Equals(
                   message.Content.Trim(),
                   "!recalculate",
                   StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(
                   message.Content.Trim(),
                   "!myrank",
                   StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(
                   message.Content.Trim(),
                   "!recalculate-invites",
                   StringComparison.OrdinalIgnoreCase) &&
               (_options.Messages.RewardBots || !message.Author.IsBot);
    }

    private async Task ScanMessageHistoryAndPublishAsync(
        DateTimeOffset scanStartedAt,
        CancellationToken cancellationToken)
    {
        if (_guild is null || _botTextChannel is null)
        {
            return;
        }

        await _messageScanGate.WaitAsync(cancellationToken);
        IUserMessage? statusMessage = null;
        try
        {
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Nachrichten-XP: Historischer Scan gestartet.");

            try
            {
                statusMessage = await _botTextChannel.SendMessageAsync(
                    """
                    ## Nachrichten-Scan läuft …
                    Der Bot liest jetzt alle erreichbaren Nachrichten. Bei großen Kanälen kann das wegen Discord-Rate-Limits einige Zeit dauern.
                    """,
                    allowedMentions: AllowedMentions.None);
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] Scan-Statusnachricht in " +
                    $"#{_botTextChannel.Name} ({_botTextChannel.Id}) gesendet.");
            }
            catch (Exception exception)
            {
                await LogExceptionAsync("ScanStatusStart", exception);
            }

            var scannedMessages = 0;
            var rewardableMessages = 0;
            var newXpMessages = 0;
            var scannedChannels = 0;
            var skippedChannels = 0;
            var scannedChannelIds = new HashSet<ulong>();
            var scannedMessageIds = new HashSet<ulong>();
            var messageSnapshot = new List<MessageXpSnapshot>();
            var startedAt = DateTimeOffset.UtcNow;
            var lastStatusUpdate = DateTimeOffset.MinValue;

            async Task UpdateStatusAsync(
                string currentChannel,
                MessageChannelProgress? channelProgress = null,
                bool force = false)
            {
                if (statusMessage is null)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                if (!force && now - lastStatusUpdate < TimeSpan.FromSeconds(5))
                {
                    return;
                }

                lastStatusUpdate = now;
                var currentScanned = scannedMessages + (channelProgress?.Scanned ?? 0);
                var currentRewardable =
                    rewardableMessages + (channelProgress?.Rewardable ?? 0);
                var currentInserted = newXpMessages + (channelProgress?.Inserted ?? 0);
                var elapsed = now - startedAt;

                try
                {
                    await statusMessage.ModifyAsync(properties =>
                    {
                        properties.Content =
                            $"""
                            ## Nachrichten-Scan läuft …
                            **Aktuell:** {currentChannel}
                            - Abgeschlossene Kanäle/Threads: {scannedChannels:N0}
                            - Übersprungen: {skippedChannels:N0}
                            - Nachrichten geprüft: {currentScanned:N0}
                            - Gewertete User-Nachrichten: {currentRewardable:N0}
                            - Nachrichten im neuen Snapshot: {currentInserted:N0}
                            - Laufzeit: {elapsed:hh\:mm\:ss}
                            """;
                    });
                }
                catch (Exception exception)
                {
                    await LogExceptionAsync("ScanStatusUpdate", exception);
                    statusMessage = null;
                }
            }

            foreach (var channel in _guild.TextChannels.OrderBy(channel => channel.Position))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [SCAN] KANAL START | " +
                    $"Name=#{channel.Name} | ID={channel.Id} | Typ={channel.GetType().Name}");
                await UpdateStatusAsync($"#{channel.Name}", force: true);
                var result = await ScanMessageChannelAsync(
                    channel,
                    cancellationToken,
                    messageSnapshot,
                    scannedMessageIds,
                    MessageScanPhase.Historical,
                    scanStartedAt,
                    progress => UpdateStatusAsync($"#{channel.Name}", progress));
                scannedMessages += result.Scanned;
                rewardableMessages += result.Rewardable;
                newXpMessages += result.Inserted;
                scannedChannels += result.ChannelsScanned;
                skippedChannels += result.ChannelsSkipped;
                scannedChannelIds.Add(channel.Id);
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [SCAN] KANAL ENDE | " +
                    $"Name=#{channel.Name} | Geprüft={result.Scanned} | " +
                    $"User-Nachrichten={result.Rewardable} | Neu={result.Inserted} | " +
                    $"Status={(result.ChannelsSkipped > 0 ? "ÜBERSPRUNGEN" : "OK")}");
                await UpdateStatusAsync($"#{channel.Name} abgeschlossen", force: true);

                if (_options.Messages.IncludeThreads && channel is not IVoiceChannel)
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [SCAN] THREAD-SUCHE START | " +
                        $"Container=#{channel.Name} | ID={channel.Id}");
                    var threadResult = await ScanThreadsAsync(
                        channel,
                        scannedChannelIds,
                        cancellationToken,
                        messageSnapshot,
                        scannedMessageIds,
                        MessageScanPhase.Historical,
                        scanStartedAt,
                        (threadName, progress) =>
                            UpdateStatusAsync($"Thread: {threadName}", progress));
                    scannedMessages += threadResult.Scanned;
                    rewardableMessages += threadResult.Rewardable;
                    newXpMessages += threadResult.Inserted;
                    scannedChannels += threadResult.ChannelsScanned;
                    skippedChannels += threadResult.ChannelsSkipped;
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [SCAN] THREAD-SUCHE ENDE | " +
                        $"Container=#{channel.Name} | Gelesen={threadResult.ChannelsScanned} | " +
                        $"Übersprungen={threadResult.ChannelsSkipped} | " +
                        $"Nachrichten={threadResult.Scanned}");
                    await UpdateStatusAsync(
                        $"Threads von #{channel.Name} abgeschlossen",
                        force: true);
                }
            }

            if (_options.Messages.IncludeThreads)
            {
                foreach (var forum in _guild.ForumChannels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [SCAN] FORUM START | " +
                        $"Name=#{forum.Name} | ID={forum.Id}");
                    var threadResult = await ScanThreadsAsync(
                        forum,
                        scannedChannelIds,
                        cancellationToken,
                        messageSnapshot,
                        scannedMessageIds,
                        MessageScanPhase.Historical,
                        scanStartedAt,
                        (threadName, progress) =>
                            UpdateStatusAsync($"Forum-Thread: {threadName}", progress));
                    scannedMessages += threadResult.Scanned;
                    rewardableMessages += threadResult.Rewardable;
                    newXpMessages += threadResult.Inserted;
                    scannedChannels += threadResult.ChannelsScanned;
                    skippedChannels += threadResult.ChannelsSkipped;
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [SCAN] FORUM ENDE | " +
                        $"Name=#{forum.Name} | Threads={threadResult.ChannelsScanned} | " +
                        $"Übersprungen={threadResult.ChannelsSkipped} | " +
                        $"Nachrichten={threadResult.Scanned}");
                    await UpdateStatusAsync(
                        $"Forum #{forum.Name} abgeschlossen",
                        force: true);
                }
            }

            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] [SCAN] CATCH-UP START | " +
                $"Zeitpunkt={scanStartedAt:O}");
            await UpdateStatusAsync(
                "Historischer Scan fertig – neue Nachrichten werden nachgezogen",
                force: true);

            var catchUpThreadIds = new HashSet<ulong>();
            foreach (var channel in _guild.TextChannels.OrderBy(channel => channel.Position))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ScanMessageChannelAsync(
                    channel,
                    cancellationToken,
                    messageSnapshot,
                    scannedMessageIds,
                    MessageScanPhase.CatchUp,
                    scanStartedAt,
                    progress => UpdateStatusAsync(
                        $"Nachziehen: #{channel.Name}",
                        progress));
                scannedMessages += result.Scanned;
                rewardableMessages += result.Rewardable;
                newXpMessages += result.Inserted;

                if (_options.Messages.IncludeThreads && channel is not IVoiceChannel)
                {
                    var threadResult = await ScanThreadsAsync(
                        channel,
                        catchUpThreadIds,
                        cancellationToken,
                        messageSnapshot,
                        scannedMessageIds,
                        MessageScanPhase.CatchUp,
                        scanStartedAt,
                        (threadName, progress) =>
                            UpdateStatusAsync(
                                $"Nachziehen, Thread: {threadName}",
                                progress));
                    scannedMessages += threadResult.Scanned;
                    rewardableMessages += threadResult.Rewardable;
                    newXpMessages += threadResult.Inserted;
                }
            }

            if (_options.Messages.IncludeThreads)
            {
                foreach (var forum in _guild.ForumChannels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var threadResult = await ScanThreadsAsync(
                        forum,
                        catchUpThreadIds,
                        cancellationToken,
                        messageSnapshot,
                        scannedMessageIds,
                        MessageScanPhase.CatchUp,
                        scanStartedAt,
                        (threadName, progress) =>
                            UpdateStatusAsync(
                                $"Nachziehen, Forum-Thread: {threadName}",
                                progress));
                    scannedMessages += threadResult.Scanned;
                    rewardableMessages += threadResult.Rewardable;
                    newXpMessages += threadResult.Inserted;
                }
            }

            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] [SCAN] CATCH-UP ENDE | " +
                $"Snapshot={messageSnapshot.Count}");

            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Nachrichten-XP: Scan beendet. " +
                $"{scannedChannels} Kanäle/Threads gelesen, {skippedChannels} übersprungen, " +
                $"{scannedMessages} Nachrichten geprüft, {rewardableMessages} User-Nachrichten, " +
                $"{newXpMessages} im neuen Nachrichten-Snapshot.");

            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] [SCAN] SQL REPLACE START | " +
                $"MessageSnapshot={messageSnapshot.Count}");
            IReadOnlyList<XpMovementResult> movements;
            await _liveMessageGate.WaitAsync(cancellationToken);
            try
            {
                var finalSnapshot = messageSnapshot.ToDictionary(
                    message => message.MessageId);
                foreach (var bufferedMessage in _recalculateMessageBuffer.Values)
                {
                    finalSnapshot[bufferedMessage.MessageId] = bufferedMessage;
                }

                foreach (var deletedMessageId in _recalculateDeletedMessageIds.Keys)
                {
                    finalSnapshot.Remove(deletedMessageId);
                }

                movements = await _database.ReplaceMessageXpAsync(
                    _guild.Id,
                    finalSnapshot.Values.ToArray(),
                    DateTimeOffset.UtcNow);
                _recalculateMessageBuffer.Clear();
                _recalculateDeletedMessageIds.Clear();
                Interlocked.Exchange(ref _messageRecalculationInProgress, 0);
            }
            finally
            {
                _liveMessageGate.Release();
            }
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] [SCAN] SQL REPLACE ENDE | " +
                $"Benutzer aktualisiert={movements.Count}");
            foreach (var movement in movements.Where(movement => movement.LeveledUp))
            {
                await NotifyLevelUpAsync(movement);
            }
            foreach (var movement in movements)
            {
                LogXpMovement(movement);
            }

            var inviteBackfill = InviteMemberBackfillResult.Empty;
            string? inviteBackfillError = null;
            if (_options.InviteTracking.Enabled)
            {
                await UpdateStatusAsync(
                    "Verfügbare Discord-Einladungen werden nachberechnet",
                    force: true);
                try
                {
                    inviteBackfill = await BackfillInviteMembersFromDiscordAsync();
                }
                catch (Exception exception)
                {
                    inviteBackfillError = exception.Message;
                    await LogExceptionAsync("InviteUsageBackfill", exception);
                }
            }

            if (_options.Messages.PublishLeaderboardAfterScan)
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [SCAN] ERGEBNIS START | " +
                    "Interne XP-Liste wird aufgebaut und an Discord gesendet.");
                await PublishInternalXpLeaderboardAsync(new MessageScanSummary(
                    scannedChannels,
                    skippedChannels,
                    scannedMessages,
                    rewardableMessages,
                    newXpMessages,
                    inviteBackfill.MatchedMembers,
                    inviteBackfill.ImportedMembers,
                    inviteBackfill.PendingMembers,
                    inviteBackfill.RewardedMembers,
                    inviteBackfill.AwardedXp,
                    inviteBackfillError),
                    statusMessage);
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [SCAN] ERGEBNIS ENDE | " +
                    "XP-Liste wurde an Discord übergeben.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            await LogExceptionAsync("MessageHistoryScan", exception);
            if (statusMessage is not null)
            {
                try
                {
                    await statusMessage.ModifyAsync(properties =>
                    {
                        properties.Content =
                            $"## Nachrichten-Scan fehlgeschlagen\n`{exception.Message}`";
                    });
                }
                catch (Exception statusException)
                {
                    await LogExceptionAsync("ScanStatusFailure", statusException);
                }
            }
        }
        finally
        {
            if (Interlocked.Exchange(ref _messageRecalculationInProgress, 0) == 1)
            {
                await FlushBufferedLiveMessagesAsync();
            }

            _messageScanGate.Release();
        }
    }

    private async Task FlushBufferedLiveMessagesAsync()
    {
        if (_guild is null)
        {
            return;
        }

        await _liveMessageGate.WaitAsync();
        try
        {
            foreach (var message in _recalculateMessageBuffer.Values)
            {
                if (_recalculateDeletedMessageIds.ContainsKey(message.MessageId))
                {
                    continue;
                }

                var result = await _database.RegisterMessageXpAsync(
                    _guild.Id,
                    message.ChannelId,
                    message.MessageId,
                    message.UserId,
                    message.Xp,
                    message.CreatedAtUtc);
                LogXpMovement(result.Movement);
                await NotifyLevelUpAsync(result.Movement);
            }

            foreach (var messageId in _recalculateDeletedMessageIds.Keys)
            {
                var result = await _database.RemoveMessageXpAsync(_guild.Id, messageId);
                LogXpMovement(result.Movement);
            }

            _recalculateMessageBuffer.Clear();
            _recalculateDeletedMessageIds.Clear();
        }
        finally
        {
            _liveMessageGate.Release();
        }
    }

    private async Task<MessageScanResult> ScanThreadsAsync(
        IThreadContainerChannel container,
        HashSet<ulong> scannedChannelIds,
        CancellationToken cancellationToken,
        List<MessageXpSnapshot> messageSnapshot,
        HashSet<ulong> scannedMessageIds,
        MessageScanPhase phase,
        DateTimeOffset scanStartedAt,
        Func<string, MessageChannelProgress, Task>? progressCallback = null)
    {
        var result = MessageScanResult.Empty;

        try
        {
            var activeThreads = await container.GetActiveThreadsAsync();
            foreach (var thread in activeThreads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scannedChannelIds.Add(thread.Id))
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [SCAN] THREAD GEFUNDEN | " +
                        $"Name={thread.Name} | ID={thread.Id} | Zustand=aktiv");
                    result += await ScanMessageChannelAsync(
                        thread,
                        cancellationToken,
                        messageSnapshot,
                        scannedMessageIds,
                        phase,
                        scanStartedAt,
                        progress => progressCallback?.Invoke(thread.Name, progress) ??
                                    Task.CompletedTask);
                }
            }

            DateTimeOffset? before = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archivedThreads = await container.GetPublicArchivedThreadsAsync(100, before);
                if (archivedThreads.Count == 0)
                {
                    break;
                }

                foreach (var thread in archivedThreads)
                {
                    if (scannedChannelIds.Add(thread.Id))
                    {
                        Console.WriteLine(
                            $"[{DateTimeOffset.Now:O}] [SCAN] THREAD GEFUNDEN | " +
                            $"Name={thread.Name} | ID={thread.Id} | Zustand=archiviert");
                        result += await ScanMessageChannelAsync(
                            thread,
                            cancellationToken,
                            messageSnapshot,
                            scannedMessageIds,
                            phase,
                            scanStartedAt,
                            progress => progressCallback?.Invoke(thread.Name, progress) ??
                                        Task.CompletedTask);
                    }
                }

                if (archivedThreads.Count < 100)
                {
                    break;
                }

                var nextBefore = archivedThreads.Min(thread => thread.ArchiveTimestamp);
                if (nextBefore == before)
                {
                    break;
                }

                before = nextBefore;
            }
        }
        catch (HttpException exception) when (
            exception.HttpCode is System.Net.HttpStatusCode.Forbidden or
                System.Net.HttpStatusCode.NotFound)
        {
            if (_options.Debug.Enabled)
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [DEBUG] Thread-Scan übersprungen: " +
                    $"{exception.HttpCode}.");
            }
        }
        catch (NotSupportedException exception)
        {
            if (_options.Debug.Enabled)
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [DEBUG] Thread-Scan nicht unterstützt: " +
                    $"{exception.Message}");
            }

            result += new MessageScanResult(0, 0, 0, 0, 1);
        }
        catch (Exception exception)
        {
            await LogExceptionAsync("ThreadScan", exception);
            result += new MessageScanResult(0, 0, 0, 0, 1);
        }

        return result;
    }

    private async Task<MessageScanResult> ScanMessageChannelAsync(
        IMessageChannel channel,
        CancellationToken cancellationToken,
        List<MessageXpSnapshot> messageSnapshot,
        HashSet<ulong> scannedMessageIds,
        MessageScanPhase phase,
        DateTimeOffset scanStartedAt,
        Func<MessageChannelProgress, Task>? progressCallback = null)
    {
        var scanned = 0;
        var rewardable = 0;
        var inserted = 0;
        var channelScanned = 0;
        var channelSkipped = 0;
        ulong? beforeMessageId = null;
        var batchNumber = 0;
        var channelStartedAt = DateTimeOffset.UtcNow;

        try
        {
            if (progressCallback is not null)
            {
                await progressCallback(new MessageChannelProgress(0, 0, 0));
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchNumber++;
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [SCAN] BATCH ANFRAGE | " +
                    $"Kanal=#{channel.Name} | Batch={batchNumber} | " +
                    $"VorMessageId={(beforeMessageId?.ToString() ?? "neueste")}");
                var batch = beforeMessageId is null
                    ? await channel.GetMessagesAsync(100, CacheMode.AllowDownload).FlattenAsync()
                    : await channel.GetMessagesAsync(
                        beforeMessageId.Value,
                        Direction.Before,
                        100,
                        CacheMode.AllowDownload).FlattenAsync();
                var messages = batch.ToArray();
                if (messages.Length == 0)
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [SCAN] BATCH LEER | " +
                        $"Kanal=#{channel.Name} | Batch={batchNumber}");
                    break;
                }

                var reachedCatchUpBoundary = false;
                foreach (var message in messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (phase == MessageScanPhase.Historical &&
                        message.Timestamp >= scanStartedAt)
                    {
                        continue;
                    }

                    if (phase == MessageScanPhase.CatchUp &&
                        message.Timestamp < scanStartedAt)
                    {
                        reachedCatchUpBoundary = true;
                        continue;
                    }

                    scanned++;
                    if (!IsRewardableMessage(message))
                    {
                        continue;
                    }

                    rewardable++;
                    var xp = MessageXpCalculator.Calculate(
                        message.Id,
                        _options.Messages.MinXp,
                        _options.Messages.MaxXp);
                    if (scannedMessageIds.Add(message.Id))
                    {
                        messageSnapshot.Add(new MessageXpSnapshot(
                            channel.Id,
                            message.Id,
                            message.Author.Id,
                            xp,
                            message.Timestamp));
                        inserted++;
                    }
                }

                if (progressCallback is not null)
                {
                    await progressCallback(new MessageChannelProgress(
                        scanned,
                        rewardable,
                        inserted));
                }

                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [SCAN] BATCH FERTIG | " +
                    $"Phase={phase} | Kanal=#{channel.Name} | Batch={batchNumber} | " +
                    $"Geladen={messages.Length} | GesamtGeprüft={scanned} | " +
                    $"User-Nachrichten={rewardable} | Neu={inserted}");

                var nextBeforeMessageId = messages.Min(message => message.Id);
                if (reachedCatchUpBoundary ||
                    messages.Length < 100 ||
                    nextBeforeMessageId == beforeMessageId)
                {
                    break;
                }

                beforeMessageId = nextBeforeMessageId;
            }

            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Nachrichten-XP: " +
                $"#{channel.Name} — {scanned} geprüft, " +
                $"{rewardable} User-Nachrichten, {inserted} neu. " +
                $"Dauer={(DateTimeOffset.UtcNow - channelStartedAt):hh\\:mm\\:ss}");
            channelScanned = 1;
        }
        catch (HttpException exception) when (
            exception.HttpCode is System.Net.HttpStatusCode.Forbidden or
                System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Nachrichten-XP: #{channel.Name} " +
                $"übersprungen ({exception.HttpCode}).");
            channelSkipped = 1;
        }
        catch (Exception exception)
        {
            await LogExceptionAsync($"MessageChannelScan:{channel.Id}", exception);
            channelSkipped = 1;
        }

        return new MessageScanResult(
            scanned,
            rewardable,
            inserted,
            channelScanned,
            channelSkipped);
    }

    private async Task PublishInternalXpLeaderboardAsync(
        MessageScanSummary? scanSummary = null,
        IUserMessage? statusMessage = null)
    {
        if (_guild is null || _botTextChannel is null)
        {
            return;
        }

        var databaseEntries = await _database.GetInternalXpLeaderboardAsync(_guild.Id);
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [SCAN] RANGLISTE DATEN | " +
            $"SQL-Einträge={databaseEntries.Count} | Server-Mitglieder={_guild.Users.Count}");
        var entriesByUser = databaseEntries.ToDictionary(entry => entry.UserId);
        var currentMemberEntries = _guild.Users
            .Where(user => _options.Messages.RewardBots || !user.IsBot)
            .Select(user => user.Id)
            .Distinct()
            .Select(userId => entriesByUser.GetValueOrDefault(userId) ??
                              new InternalXpEntry(userId, 0, 0, 0, 0, 0, 0, 0))
            .OrderByDescending(entry => entry.TotalXp)
            .ThenBy(entry => entry.UserId)
            .ToArray();

        var pages = BuildLeaderboardPages(currentMemberEntries);
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [SCAN] RANGLISTE SEITEN | " +
            $"Benutzer={currentMemberEntries.Length} | Seiten={pages.Count}");
        for (var index = 0; index < pages.Count; index++)
        {
            var pageHeader = pages.Count == 1
                ? "## Nachrichten-Scan und interne XP-Liste"
                : $"## Nachrichten-Scan und interne XP-Liste ({index + 1}/{pages.Count})";
            var summary = index == 0 && scanSummary is not null
                ? $"""
                   **Scan abgeschlossen**
                   - Kanäle/Threads gelesen: {scanSummary.ChannelsScanned:N0}
                   - Kanäle/Threads übersprungen: {scanSummary.ChannelsSkipped:N0}
                   - Nachrichten geprüft: {scanSummary.MessagesScanned:N0}
                   - Gewertete User-Nachrichten: {scanSummary.RewardableMessages:N0}
                   - Nachrichten im gespeicherten Snapshot: {scanSummary.NewMessages:N0}
                   - Mitglieder mit Invite-Code und Einlader: {scanSummary.MatchedInviteMembers:N0}
                   - Neu als Einladung importiert: {scanSummary.ImportedInviteMembers:N0}
                   - Davon noch keine 7 Tage auf dem Server: {scanSummary.PendingInviteMembers:N0}
                   - Sofort vergütete Einladungen: {scanSummary.RewardedInviteMembers:N0}
                   - Neu vergebene Invite-XP: {scanSummary.InviteXpAwarded:N0}
                   {(
                       string.IsNullOrWhiteSpace(scanSummary.InviteBackfillError)
                           ? string.Empty
                           : $"- Invite-Nachberechnung fehlgeschlagen: {scanSummary.InviteBackfillError}\n"
                   )}

                   """
                : string.Empty;
            var content = $"{pageHeader}\n{summary}{pages[index]}";
            if (index == 0 && statusMessage is not null)
            {
                try
                {
                    Console.WriteLine(
                        $"[{DateTimeOffset.Now:O}] [SCAN] DISCORD UPDATE | " +
                        $"Seite={index + 1}/{pages.Count} | Nachricht={statusMessage.Id}");
                    await statusMessage.ModifyAsync(properties =>
                    {
                        properties.Content = content;
                        properties.AllowedMentions = AllowedMentions.None;
                    });
                    continue;
                }
                catch (Exception exception)
                {
                    await LogExceptionAsync("ScanFinalStatusUpdate", exception);
                }
            }

            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] [SCAN] DISCORD SENDEN | " +
                $"Seite={index + 1}/{pages.Count} | Kanal=#{_botTextChannel.Name}");
            await _botTextChannel.SendMessageAsync(
                content,
                allowedMentions: AllowedMentions.None);
        }
    }

    private static IReadOnlyList<string> BuildLeaderboardPages(
        IReadOnlyList<InternalXpEntry> entries)
    {
        var pages = new List<string>();
        var current = new System.Text.StringBuilder();

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var line =
                $"{index + 1}. <@{entry.UserId}> — **Level {entry.CurrentLevel}** | " +
                $"**{entry.TotalXp:N0} XP** | Fortschritt: " +
                $"{entry.CurrentLevelProgress:N0}/" +
                $"{LevelCalculator.GetXpForNextLevel(entry.CurrentLevel):N0} | " +
                $"Text: {entry.MessageXp:N0} | Voice: {entry.VoiceXp:N0} | " +
                $"Invites: {entry.InviteXp:N0} | Nachrichten: {entry.MessageCount:N0}\n";

            if (current.Length > 0 && current.Length + line.Length > 1450)
            {
                pages.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            current.Append(line);
        }

        if (current.Length > 0)
        {
            pages.Add(current.ToString().TrimEnd());
        }

        if (pages.Count == 0)
        {
            pages.Add("Noch keine XP vorhanden.");
        }

        return pages;
    }

    private async Task<ITextChannel> GetOrCreateBotTextChannelAsync()
    {
        if (_guild is null)
        {
            throw new InvalidOperationException("Der Discord-Server ist noch nicht geladen.");
        }

        if (_options.BotChannel.ChannelId != 0 &&
            _guild.GetTextChannel(_options.BotChannel.ChannelId) is { } configuredChannel)
        {
            return configuredChannel;
        }

        var existingChannel = _guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(
                channel.Name,
                _options.BotChannel.ChannelName,
                StringComparison.OrdinalIgnoreCase));
        if (existingChannel is not null)
        {
            return existingChannel;
        }

        if (!_options.BotChannel.CreateChannelIfMissing)
        {
            throw new InvalidOperationException(
                "Der konfigurierte Bot-Textkanal wurde nicht gefunden.");
        }

        var createdChannel = await _guild.CreateTextChannelAsync(
            _options.BotChannel.ChannelName,
            properties =>
            {
                properties.Topic =
                    "Statusmeldungen, Scan-Fortschritt und interne XP-Auswertungen des Bots.";
                if (_options.BotChannel.CategoryId != 0)
                {
                    properties.CategoryId = _options.BotChannel.CategoryId;
                }
            });

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Bot-Textkanal #{createdChannel.Name} erstellt.");
        return createdChannel;
    }

    private async Task<ITextChannel?> GetOrCreateLevelUpChannelAsync()
    {
        if (_guild is null || !_options.Levels.Enabled)
        {
            return null;
        }

        if (_options.Levels.LevelUpChannelId != 0 &&
            _guild.GetTextChannel(_options.Levels.LevelUpChannelId) is { } configuredChannel)
        {
            return configuredChannel;
        }

        var existingChannel = _guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(
                channel.Name,
                _options.Levels.LevelUpChannelName,
                StringComparison.OrdinalIgnoreCase));
        if (existingChannel is not null)
        {
            return existingChannel;
        }

        if (!_options.Levels.CreateChannelIfMissing)
        {
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Level-up channel was not found. " +
                "Level-up notifications are disabled.");
            return null;
        }

        var createdChannel = await _guild.CreateTextChannelAsync(
            _options.Levels.LevelUpChannelName,
            properties =>
            {
                properties.Topic = "Automatic level-up notifications.";
                if (_options.Levels.CategoryId != 0)
                {
                    properties.CategoryId = _options.Levels.CategoryId;
                }
            });

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Level-up channel #{createdChannel.Name} created.");
        return createdChannel;
    }

    private async Task NotifyLevelUpAsync(XpMovementResult? movement)
    {
        if (!_options.Levels.Enabled ||
            _levelUpChannel is null ||
            movement is null ||
            !movement.LeveledUp)
        {
            return;
        }

        await _levelUpChannel.SendMessageAsync(
            $"""
            🎉 <@{movement.UserId}> leveled up!
            **Level:** {movement.OldLevel} → {movement.NewLevel}
            **Total XP:** {movement.NewXp:N0}
            **Level progress:** {movement.CurrentLevelProgress:N0}/{movement.XpForNextLevel:N0}
            **XP needed for next level:** {Math.Max(
                0,
                movement.XpForNextLevel - movement.CurrentLevelProgress):N0}
            """);
    }

    private void LogXpMovement(XpMovementResult? movement)
    {
        if (!_options.Debug.Enabled ||
            movement is null ||
            !movement.Applied ||
            movement.Amount == 0)
        {
            return;
        }

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [DEBUG] [XP] " +
            $"User={movement.UserId} | Amount={movement.Amount:+#;-#;0} | " +
            $"Reason={movement.Reason} | XP={movement.OldXp}->{movement.NewXp} | " +
            $"Level={movement.OldLevel}->{movement.NewLevel}");
    }

    private async Task AnnounceAsync(bool featureEnabled, string message)
    {
        if (!_options.Announcements.Enabled ||
            !featureEnabled ||
            _options.Announcements.ChannelId == 0 ||
            _guild?.GetTextChannel(_options.Announcements.ChannelId) is not { } channel ||
            string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await channel.SendMessageAsync(message);
    }

    private static string FormatMessage(
        string template,
        ulong inviterId,
        ulong memberId,
        int xp,
        int minutes = 0)
    {
        return template
            .Replace("{inviter}", $"<@{inviterId}>", StringComparison.OrdinalIgnoreCase)
            .Replace("{member}", $"<@{memberId}>", StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", $"<@{memberId}>", StringComparison.OrdinalIgnoreCase)
            .Replace("{xp}", xp.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{minutes}", minutes.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private Task OnLogAsync(LogMessage message)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:O}] [{message.Severity}] {message}");
        return Task.CompletedTask;
    }

    private static Task LogExceptionAsync(string context, Exception exception)
    {
        Console.Error.WriteLine(
            $"[{DateTimeOffset.Now:O}] [{context}] {exception}");
        return Task.CompletedTask;
    }

    private static async Task WaitForBackgroundTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _httpClient.Dispose();
        _inviteGate.Dispose();
        _inviteRecalculateGate.Dispose();
        _messageScanGate.Dispose();
        _liveMessageGate.Dispose();
    }

    private sealed record InviteSnapshot(int Uses, ulong? InviterId);
    private sealed record DetectedInvite(string Code, ulong? InviterId);
    private sealed record MessageScanResult(
        int Scanned,
        int Rewardable,
        int Inserted,
        int ChannelsScanned,
        int ChannelsSkipped)
    {
        public static MessageScanResult Empty { get; } = new(0, 0, 0, 0, 0);

        public static MessageScanResult operator +(
            MessageScanResult left,
            MessageScanResult right) =>
            new(
                left.Scanned + right.Scanned,
                left.Rewardable + right.Rewardable,
                left.Inserted + right.Inserted,
                left.ChannelsScanned + right.ChannelsScanned,
                left.ChannelsSkipped + right.ChannelsSkipped);
    }

    private sealed record MessageScanSummary(
        int ChannelsScanned,
        int ChannelsSkipped,
        int MessagesScanned,
        int RewardableMessages,
        int NewMessages,
        int MatchedInviteMembers,
        int ImportedInviteMembers,
        int PendingInviteMembers,
        int RewardedInviteMembers,
        int InviteXpAwarded,
        string? InviteBackfillError);

    private sealed record MessageChannelProgress(
        int Scanned,
        int Rewardable,
        int Inserted);

    private enum MessageScanPhase
    {
        Historical,
        CatchUp
    }
}
