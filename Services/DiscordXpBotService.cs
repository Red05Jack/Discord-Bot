using System.Collections.Concurrent;
using System.Security.Cryptography;
using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using DiscordXpBot.Configuration;
using DiscordXpBot.Data;

namespace DiscordXpBot.Services;

public sealed class DiscordXpBotService : IAsyncDisposable
{
    private readonly BotOptions _options;
    private readonly BotDatabase _database;
    private readonly DiscordSocketClient _client;
    private readonly ConcurrentDictionary<string, InviteSnapshot> _inviteCache = [];
    private readonly SemaphoreSlim _inviteGate = new(1, 1);
    private readonly SemaphoreSlim _xpDispatchGate = new(1, 1);
    private readonly SemaphoreSlim _messageScanGate = new(1, 1);
    private CancellationToken _runToken;
    private SocketGuild? _guild;
    private ITextChannel? _mee6CommandChannel;
    private Task? _inviteRewardLoop;
    private Task? _voiceCheckpointLoop;
    private Task? _xpDispatchLoop;
    private Task? _messageHistoryTask;
    private int _readyInitialized;

    public DiscordXpBotService(BotOptions options, BotDatabase database)
    {
        _options = options;
        _database = database;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMembers |
                GatewayIntents.GuildMessages |
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
            if (!_options.Messages.ScanOnly && _options.Voice.Enabled)
            {
                await CheckpointAllVoiceUsersAsync(announce: false);
            }

            if (!_options.Messages.ScanOnly && _options.Mee6Commands.Enabled)
            {
                await DispatchPendingXpCommandsAsync();
            }

            await _client.StopAsync();
            await WaitForBackgroundTaskAsync(_inviteRewardLoop);
            await WaitForBackgroundTaskAsync(_voiceCheckpointLoop);
            await WaitForBackgroundTaskAsync(_xpDispatchLoop);
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

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Verbunden als {_client.CurrentUser} mit {_guild.Name}.");
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Verwendete GuildId: {_guild.Id}.");
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] Debug-Protokollierung: " +
            $"{(_options.Debug.Enabled ? "aktiv" : "inaktiv")}.");

        if (_options.Mee6Commands.Enabled || _options.Messages.Enabled)
        {
            _mee6CommandChannel = await GetOrCreateMee6CommandChannelAsync();
        }

        if (_options.Messages.Enabled && _options.Messages.ScanOnly)
        {
            Console.WriteLine(
                $"[{DateTimeOffset.Now:O}] Scan-only-Modus aktiv: " +
                "Voice, Einladungen, MEE6-Versand, Live-Nachrichten und Slash-Commands sind pausiert.");
            if (_options.Discord.RegisterSlashCommands)
            {
                await _guild.BulkOverwriteApplicationCommandAsync([]);
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] Slash-Commands für den Scan-only-Modus entfernt.");
            }

            _messageHistoryTask = ScanMessageHistoryAndPublishAsync(_runToken);
            return;
        }

        if (_options.Mee6Commands.Enabled)
        {
            await DispatchPendingXpCommandsAsync();
            _xpDispatchLoop = RunXpDispatchLoopAsync(_runToken);
        }

        if (_options.InviteTracking.Enabled)
        {
            await RefreshInviteCacheAsync();
            _inviteRewardLoop = RunInviteRewardLoopAsync(_runToken);
        }

        if (_options.Voice.Enabled)
        {
            await ResetVoiceSessionsFromDiscordAsync();
            _voiceCheckpointLoop = RunVoiceCheckpointLoopAsync(_runToken);
        }

        if (_options.Discord.RegisterSlashCommands)
        {
            await RegisterSlashCommandsAsync();
        }

        if (_options.Messages.Enabled && _options.Messages.ScanHistoryOnStartup)
        {
            _messageHistoryTask = ScanMessageHistoryAndPublishAsync(_runToken);
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
        if (_options.Messages.ScanOnly ||
            !_options.InviteTracking.Enabled ||
            member.Guild.Id != _options.Discord.GuildId)
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
        if (_options.Messages.ScanOnly ||
            !_options.InviteTracking.Enabled ||
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

            var deleted = await _database.RevokeInviteAndDeleteAsync(
                invite,
                DateTimeOffset.UtcNow);

            if (deleted && invite.RewardGiven)
            {
                await DispatchPendingXpCommandsAsync();
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
        if (_options.Messages.ScanOnly ||
            user is not SocketGuildUser guildUser ||
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
                    _options.Voice.MinXpPerMinute,
                    _options.Voice.MaxXpPerMinute,
                    _options.Voice.MinimumRewardableMinutes,
                    deleteSession: true);

                await DispatchPendingXpCommandsAsync();
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
        if (_options.Messages.ScanOnly)
        {
            return Task.CompletedTask;
        }

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
        if (_options.Messages.ScanOnly)
        {
            return Task.CompletedTask;
        }

        if (channel.Guild.Id == _options.Discord.GuildId)
        {
            _inviteCache.TryRemove(code, out _);
        }

        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (_options.Messages.ScanOnly ||
            !_options.Messages.Enabled ||
            message.Channel is not SocketGuildChannel guildChannel ||
            guildChannel.Guild.Id != _options.Discord.GuildId ||
            !IsRewardableMessage(message))
        {
            return;
        }

        try
        {
            var xp = MessageXpCalculator.Calculate(
                message.Id,
                _options.Messages.MinXp,
                _options.Messages.MaxXp);
            var inserted = await _database.RegisterMessageXpAsync(
                guildChannel.Guild.Id,
                message.Channel.Id,
                message.Id,
                message.Author.Id,
                xp,
                message.Timestamp);

            if (_options.Debug.Enabled && inserted)
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [DEBUG] [NACHRICHT-XP] " +
                    $"Benutzer={message.Author} ({message.Author.Id}) | " +
                    $"Kanal={message.Channel.Name} ({message.Channel.Id}) | " +
                    $"Nachricht={message.Id} | XP={xp}");
            }
        }
        catch (Exception exception)
        {
            await LogExceptionAsync($"MessageReceived:{message.Id}", exception);
        }
    }

    private async Task OnMessageDeletedAsync(
        Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel)
    {
        if (_options.Messages.ScanOnly ||
            !_options.Messages.Enabled ||
            !channel.HasValue ||
            channel.Value is not IGuildChannel guildChannel ||
            guildChannel.GuildId != _options.Discord.GuildId)
        {
            return;
        }

        try
        {
            var removed = await _database.RemoveMessageXpAsync(
                guildChannel.Guild.Id,
                message.Id);
            if (_options.Debug.Enabled && removed)
            {
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] [DEBUG] [NACHRICHT-GELÖSCHT] " +
                    $"Kanal={channel.Value.Name} ({channel.Id}) | Nachricht={message.Id}");
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
        if (_options.Messages.ScanOnly)
        {
            return;
        }

        if (command.GuildId != _options.Discord.GuildId)
        {
            await command.RespondAsync("Dieser Bot ist für einen anderen Server konfiguriert.", ephemeral: true);
            return;
        }

        try
        {
            switch (command.Data.Name)
            {
                case "xp-admin":
                    await HandleAdminXpCommandAsync(command);
                    break;
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

    private async Task HandleAdminXpCommandAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser guildUser ||
            !guildUser.GuildPermissions.ManageGuild)
        {
            await command.RespondAsync(
                "Dafür brauchst du die Berechtigung „Server verwalten“.",
                ephemeral: true);
            return;
        }

        var user = command.Data.Options
            .First(option => option.Name == "benutzer")
            .Value as IUser;
        var amount = Convert.ToInt32(
            command.Data.Options.First(option => option.Name == "betrag").Value);
        var reason = command.Data.Options
            .FirstOrDefault(option => option.Name == "grund")
            ?.Value?.ToString() ?? "manuelle Anpassung";

        if (user is null || amount == 0)
        {
            await command.RespondAsync("Benutzer oder Betrag ist ungültig.", ephemeral: true);
            return;
        }

        await _database.QueueManualXpCommandAsync(
            _options.Discord.GuildId,
            user.Id,
            amount,
            reason,
            DateTimeOffset.UtcNow);
        await DispatchPendingXpCommandsAsync();
        await command.RespondAsync(
            $"MEE6-Befehl für {user.Mention} mit **{amount:+#;-#;0} XP** vorgemerkt.",
            ephemeral: true);
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

        var admin = new SlashCommandBuilder()
            .WithName("xp-admin")
            .WithDescription("Sendet eine manuelle XP-Anpassung an MEE6.")
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                "benutzer",
                ApplicationCommandOptionType.User,
                "Mitglied",
                isRequired: true)
            .AddOption(
                "betrag",
                ApplicationCommandOptionType.Integer,
                "Positive oder negative XP",
                isRequired: true,
                minValue: -1_000_000,
                maxValue: 1_000_000)
            .AddOption(
                "grund",
                ApplicationCommandOptionType.String,
                "Grund für die Anpassung",
                isRequired: false,
                maxLength: 200)
            .Build();

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

        await _guild.BulkOverwriteApplicationCommandAsync([admin, backfill, xpList]);
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
                    await _database.RevokeInviteAndDeleteAsync(invite, DateTimeOffset.UtcNow);
                    continue;
                }

                var rewarded = await _database.MarkInviteRewardGivenAsync(
                    invite,
                    DateTimeOffset.UtcNow);
                if (!rewarded)
                {
                    continue;
                }

                rewardCount++;
                await AnnounceAsync(
                    _options.Announcements.AnnounceInviteRewards,
                    FormatMessage(
                        _options.Announcements.InviteRewardMessage,
                        invite.InviterId,
                        invite.MemberId,
                        invite.RewardXp));
            }

            await DispatchPendingXpCommandsAsync();
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
                await CheckpointAllVoiceUsersAsync(
                    _options.Announcements.AnnounceVoiceRewards);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ResetVoiceSessionsFromDiscordAsync()
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

        await _database.ResetVoiceSessionsAsync(
            _guild.Id,
            activeUsers,
            DateTimeOffset.UtcNow);
    }

    private async Task CheckpointAllVoiceUsersAsync(bool announce)
    {
        if (_guild is null)
        {
            return;
        }

        foreach (var user in _guild.Users.Where(user =>
                     (!user.IsBot || _options.Voice.RewardBots) &&
                     IsEligibleVoiceChannel(user.VoiceChannel)))
        {
            try
            {
                var reward = await _database.RewardVoiceTimeAsync(
                    _guild.Id,
                    user.Id,
                    DateTimeOffset.UtcNow,
                    _options.Voice.MinXpPerMinute,
                    _options.Voice.MaxXpPerMinute,
                    _options.Voice.MinimumRewardableMinutes,
                    deleteSession: false);

                await DispatchPendingXpCommandsAsync();
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
               (_options.Messages.RewardBots || !message.Author.IsBot);
    }

    private async Task ScanMessageHistoryAndPublishAsync(CancellationToken cancellationToken)
    {
        if (_guild is null || _mee6CommandChannel is null)
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
                statusMessage = await _mee6CommandChannel.SendMessageAsync(
                    """
                    ## Nachrichten-Scan läuft …
                    Der Bot liest jetzt alle erreichbaren Nachrichten. Bei großen Kanälen kann das wegen Discord-Rate-Limits einige Zeit dauern.
                    """,
                    allowedMentions: AllowedMentions.None);
                Console.WriteLine(
                    $"[{DateTimeOffset.Now:O}] Scan-Statusnachricht in " +
                    $"#{_mee6CommandChannel.Name} ({_mee6CommandChannel.Id}) gesendet.");
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
                            - Beim Lauf neu in SQL: {currentInserted:N0}
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
                $"[{DateTimeOffset.Now:O}] Nachrichten-XP: Scan beendet. " +
                $"{scannedChannels} Kanäle/Threads gelesen, {skippedChannels} übersprungen, " +
                $"{scannedMessages} Nachrichten geprüft, {rewardableMessages} User-Nachrichten, " +
                $"{newXpMessages} erstmals verbucht.");

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
                    newXpMessages),
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
            _messageScanGate.Release();
        }
    }

    private async Task<MessageScanResult> ScanThreadsAsync(
        IThreadContainerChannel container,
        HashSet<ulong> scannedChannelIds,
        CancellationToken cancellationToken,
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

                foreach (var message in messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                    if (await _database.RegisterMessageXpAsync(
                            _options.Discord.GuildId,
                            channel.Id,
                            message.Id,
                            message.Author.Id,
                            xp,
                            message.Timestamp))
                    {
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
                    $"Kanal=#{channel.Name} | Batch={batchNumber} | " +
                    $"Geladen={messages.Length} | GesamtGeprüft={scanned} | " +
                    $"User-Nachrichten={rewardable} | Neu={inserted}");

                var nextBeforeMessageId = messages.Min(message => message.Id);
                if (messages.Length < 100 || nextBeforeMessageId == beforeMessageId)
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
        if (_guild is null || _mee6CommandChannel is null)
        {
            return;
        }

        var databaseEntries = await _database.GetInternalXpLeaderboardAsync(_guild.Id);
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [SCAN] RANGLISTE DATEN | " +
            $"SQL-Einträge={databaseEntries.Count} | Server-Mitglieder={_guild.Users.Count}");
        var entriesByUser = databaseEntries.ToDictionary(entry => entry.UserId);
        var userIds = _guild.Users
            .Where(user => _options.Messages.RewardBots || !user.IsBot)
            .Select(user => user.Id)
            .Union(entriesByUser.Keys)
            .Distinct()
            .Select(userId => entriesByUser.GetValueOrDefault(userId) ??
                              new InternalXpEntry(userId, 0, 0, 0))
            .OrderByDescending(entry => entry.TotalXp)
            .ThenBy(entry => entry.UserId)
            .ToArray();

        var pages = BuildLeaderboardPages(userIds);
        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] [SCAN] RANGLISTE SEITEN | " +
            $"Benutzer={userIds.Length} | Seiten={pages.Count}");
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
                   - Beim Lauf neu in SQL: {scanSummary.NewMessages:N0}

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
                $"Seite={index + 1}/{pages.Count} | Kanal=#{_mee6CommandChannel.Name}");
            await _mee6CommandChannel.SendMessageAsync(
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
                $"{index + 1}. <@{entry.UserId}> — **{entry.TotalXp:N0} XP** " +
                $"(Nachrichten: {entry.MessageXp:N0} XP / {entry.MessageCount:N0})\n";

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

    private async Task<ITextChannel> GetOrCreateMee6CommandChannelAsync()
    {
        if (_guild is null)
        {
            throw new InvalidOperationException("Der Discord-Server ist noch nicht geladen.");
        }

        if (_options.Mee6Commands.ChannelId != 0 &&
            _guild.GetTextChannel(_options.Mee6Commands.ChannelId) is { } configuredChannel)
        {
            return configuredChannel;
        }

        var existingChannel = _guild.TextChannels.FirstOrDefault(channel =>
            string.Equals(
                channel.Name,
                _options.Mee6Commands.ChannelName,
                StringComparison.OrdinalIgnoreCase));
        if (existingChannel is not null)
        {
            return existingChannel;
        }

        if (!_options.Mee6Commands.CreateChannelIfMissing)
        {
            throw new InvalidOperationException(
                "Der konfigurierte MEE6-Befehlskanal wurde nicht gefunden.");
        }

        var createdChannel = await _guild.CreateTextChannelAsync(
            _options.Mee6Commands.ChannelName,
            properties =>
            {
                properties.Topic =
                    "Automatische XP-Befehle und interne XP-Auswertungen. Nicht für normale Nachrichten verwenden.";
                if (_options.Mee6Commands.CategoryId != 0)
                {
                    properties.CategoryId = _options.Mee6Commands.CategoryId;
                }
            });

        Console.WriteLine(
            $"[{DateTimeOffset.Now:O}] MEE6-Befehlskanal #{createdChannel.Name} erstellt.");
        return createdChannel;
    }

    private async Task RunXpDispatchLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.Mee6Commands.DispatchIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await DispatchPendingXpCommandsAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task DispatchPendingXpCommandsAsync()
    {
        if (!_options.Mee6Commands.Enabled ||
            _mee6CommandChannel is null ||
            _guild is null)
        {
            return;
        }

        if (!await _xpDispatchGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var pending = await _database.GetPendingXpDispatchesAsync(_guild.Id);
            foreach (var dispatch in pending)
            {
                if (!await _database.TryClaimXpDispatchAsync(dispatch.Id, DateTimeOffset.UtcNow))
                {
                    continue;
                }

                try
                {
                    var template = dispatch.Amount >= 0
                        ? _options.Mee6Commands.GiveXpCommand
                        : _options.Mee6Commands.RemoveXpCommand;
                    var commandText = FormatMee6Command(
                        template,
                        dispatch.UserId,
                        Math.Abs(dispatch.Amount));
                    var message = await _mee6CommandChannel.SendMessageAsync(commandText);
                    await _database.MarkXpDispatchSentAsync(
                        dispatch.Id,
                        message.Id,
                        DateTimeOffset.UtcNow);
                }
                catch (Exception exception)
                {
                    await _database.MarkXpDispatchFailedAsync(dispatch.Id, exception.Message);
                    await LogExceptionAsync($"Mee6Dispatch:{dispatch.ReferenceId}", exception);
                }
            }
        }
        finally
        {
            _xpDispatchGate.Release();
        }
    }

    private static string FormatMee6Command(string template, ulong userId, int xp)
    {
        return template
            .Replace("{user}", $"<@{userId}>", StringComparison.OrdinalIgnoreCase)
            .Replace("{userId}", userId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{xp}", xp.ToString(), StringComparison.OrdinalIgnoreCase);
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
        _inviteGate.Dispose();
        _xpDispatchGate.Dispose();
        _messageScanGate.Dispose();
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
        int NewMessages);

    private sealed record MessageChannelProgress(
        int Scanned,
        int Rewardable,
        int Inserted);
}
