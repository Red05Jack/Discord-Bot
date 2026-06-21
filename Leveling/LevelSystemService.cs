using DiscordXpBot.Persistence;

namespace DiscordXpBot.Leveling;

public sealed class LevelSystemService
{
    private readonly BotDatabase _database;
    private ulong _guildId;

    public LevelSystemService(BotDatabase database)
    {
        _database = database;
    }

    public void ConfigureGuild(ulong guildId)
    {
        _guildId = guildId;
    }

    public int GetXpForNextLevel(int level) =>
        LevelCalculator.GetXpForNextLevel(level);

    public LevelState CalculateLevelFromTotalXp(long totalXp) =>
        LevelCalculator.CalculateLevelFromTotalXp(totalXp);

    public Task<XpMovementResult> AddXp(
        ulong userId,
        int amount,
        string reason)
    {
        EnsureConfigured();
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must not be negative.");
        }

        return _database.AddXpAsync(
            _guildId,
            userId,
            amount,
            reason,
            $"manual-add:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow);
    }

    public Task<XpMovementResult> RemoveXp(
        ulong userId,
        int amount,
        string reason)
    {
        EnsureConfigured();
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must not be negative.");
        }

        return _database.RemoveXpAsync(
            _guildId,
            userId,
            amount,
            reason,
            $"manual-remove:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow);
    }

    private void EnsureConfigured()
    {
        if (_guildId == 0)
        {
            throw new InvalidOperationException(
                "The level system must be configured with a guild before it can change XP.");
        }
    }
}
