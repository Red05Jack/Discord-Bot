using System.Text.Json;

namespace DiscordXpBot.Configuration;

public sealed class BotOptions
{
    public DiscordOptions Discord { get; set; } = new();
    public InviteTrackingOptions InviteTracking { get; set; } = new();
    public VoiceXpOptions Voice { get; set; } = new();
    public MessageXpOptions Messages { get; set; } = new();
    public LevelSystemOptions Levels { get; set; } = new();
    public XpOptions Xp { get; set; } = new();
    public BotChannelOptions BotChannel { get; set; } = new();
    public AnnouncementOptions Announcements { get; set; } = new();
    public DebugOptions Debug { get; set; } = new();

    public static BotOptions Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Konfigurationsdatei nicht gefunden: {fullPath}",
                fullPath);
        }

        var json = File.ReadAllText(fullPath);
        var options = JsonSerializer.Deserialize<BotOptions>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("Die Konfiguration konnte nicht gelesen werden.");

        var tokenFromEnvironment = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (!string.IsNullOrWhiteSpace(tokenFromEnvironment))
        {
            options.Discord.Token = tokenFromEnvironment;
        }

        var guildFromEnvironment = Environment.GetEnvironmentVariable("DISCORD_GUILD_ID");
        if (ulong.TryParse(guildFromEnvironment, out var guildId))
        {
            options.Discord.GuildId = guildId;
        }

        var databaseFromEnvironment = Environment.GetEnvironmentVariable("BOT_DATABASE_PATH");
        if (!string.IsNullOrWhiteSpace(databaseFromEnvironment))
        {
            options.Xp.DatabasePath = databaseFromEnvironment;
        }

        if (!Path.IsPathRooted(options.Xp.DatabasePath))
        {
            var configDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            options.Xp.DatabasePath = Path.GetFullPath(
                Path.Combine(configDirectory, options.Xp.DatabasePath));
        }

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Discord.Token))
        {
            throw new InvalidOperationException(
                "Discord.Token fehlt. Setze ihn in appsettings.json oder über DISCORD_BOT_TOKEN.");
        }

        ValidateRange(
            InviteTracking.RewardMinXp,
            InviteTracking.RewardMaxXp,
            "InviteTracking.RewardMinXp",
            "InviteTracking.RewardMaxXp");
        ValidateRange(
            Voice.MinXpPerFiveMinutes,
            Voice.MaxXpPerFiveMinutes,
            "Voice.MinXpPerFiveMinutes",
            "Voice.MaxXpPerFiveMinutes");
        ValidateRange(
            Messages.MinXp,
            Messages.MaxXp,
            "Messages.MinXp",
            "Messages.MaxXp");

        if (InviteTracking.RetentionDays <= 0)
        {
            throw new InvalidOperationException("InviteTracking.RetentionDays muss größer als 0 sein.");
        }

        if (InviteTracking.CheckIntervalMinutes <= 0)
        {
            throw new InvalidOperationException(
                "InviteTracking.CheckIntervalMinutes muss größer als 0 sein.");
        }

        if (Voice.CheckpointIntervalMinutes <= 0)
        {
            throw new InvalidOperationException(
                "Voice.CheckpointIntervalMinutes muss größer als 0 sein.");
        }

        if (Voice.RewardBlockMinutes <= 0)
        {
            throw new InvalidOperationException(
                "Voice.RewardBlockMinutes muss größer als 0 sein.");
        }

        if (BotChannel.Enabled)
        {
            if (BotChannel.ChannelId == 0 &&
                string.IsNullOrWhiteSpace(BotChannel.ChannelName))
            {
                throw new InvalidOperationException(
                    "BotChannel.ChannelName muss gesetzt sein, wenn keine ChannelId angegeben ist.");
            }
        }

        if (Levels.Enabled &&
            Levels.LevelUpChannelId == 0 &&
            string.IsNullOrWhiteSpace(Levels.LevelUpChannelName))
        {
            throw new InvalidOperationException(
                "Levels.LevelUpChannelName must be set when no LevelUpChannelId is configured.");
        }
    }

    private static void ValidateRange(int min, int max, string minName, string maxName)
    {
        if (min < 0 || max < min)
        {
            throw new InvalidOperationException(
                $"{minName} und {maxName} bilden keinen gültigen Bereich.");
        }
    }

}

public sealed class DiscordOptions
{
    public string Token { get; set; } = string.Empty;
    public ulong GuildId { get; set; }
    public bool RegisterSlashCommands { get; set; }
}

public sealed class InviteTrackingOptions
{
    public bool Enabled { get; set; }
    public int RewardMinXp { get; set; }
    public int RewardMaxXp { get; set; }
    public double RetentionDays { get; set; }
    public double CheckIntervalMinutes { get; set; }
    public bool RewardBotInvites { get; set; }
    public bool AllowSelfInvites { get; set; }
}

public sealed class VoiceXpOptions
{
    public bool Enabled { get; set; }
    public int MinXpPerFiveMinutes { get; set; } = 5;
    public int MaxXpPerFiveMinutes { get; set; } = 15;
    public int RewardBlockMinutes { get; set; } = 5;
    public double CheckpointIntervalMinutes { get; set; }
    public bool RewardBots { get; set; }
    public List<ulong> EligibleChannelIds { get; set; } = [];
    public List<ulong> ExcludedChannelIds { get; set; } = [];
}

public sealed class MessageXpOptions
{
    public bool Enabled { get; set; } = true;
    public int MinXp { get; set; } = 15;
    public int MaxXp { get; set; } = 25;
    public bool RewardBots { get; set; }
    public bool IncludeThreads { get; set; } = true;
    public bool PublishLeaderboardAfterScan { get; set; } = true;
}

public sealed class XpOptions
{
    public string DatabasePath { get; set; } = string.Empty;
}

public sealed class LevelSystemOptions
{
    public bool Enabled { get; set; } = true;
    public ulong LevelUpChannelId { get; set; }
    public string LevelUpChannelName { get; set; } = "level-ups";
    public ulong CategoryId { get; set; }
    public bool CreateChannelIfMissing { get; set; } = true;
}

public sealed class BotChannelOptions
{
    public bool Enabled { get; set; } = true;
    public ulong ChannelId { get; set; }
    public string ChannelName { get; set; } = "mee6-xp-befehle";
    public ulong CategoryId { get; set; }
    public bool CreateChannelIfMissing { get; set; } = true;
}

public sealed class AnnouncementOptions
{
    public bool Enabled { get; set; }
    public ulong ChannelId { get; set; }
    public bool AnnounceInviteRewards { get; set; }
    public bool AnnounceInviteRevocations { get; set; }
    public bool AnnounceVoiceRewards { get; set; }
    public string InviteRewardMessage { get; set; } = string.Empty;
    public string InviteRevocationMessage { get; set; } = string.Empty;
    public string VoiceRewardMessage { get; set; } = string.Empty;
}

public sealed class DebugOptions
{
    public bool Enabled { get; set; }
}
