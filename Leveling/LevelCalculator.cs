namespace DiscordXpBot.Leveling;

public static class LevelCalculator
{
    public static int GetXpForNextLevel(int level)
    {
        if (level < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "Level must not be negative.");
        }

        // The curve is indexed by the target level so level 0 can exist.
        var requiredXp = Math.Round(
            20.0 * Math.Pow(level + 1, 1.9),
            MidpointRounding.ToEven);
        return checked((int)requiredXp);
    }

    public static LevelState CalculateLevelFromTotalXp(long totalXp)
    {
        var remainingXp = Math.Max(0, totalXp);
        var level = 0;

        while (true)
        {
            var xpForNextLevel = GetXpForNextLevel(level);
            if (remainingXp < xpForNextLevel)
            {
                return new LevelState(
                    Math.Max(0, totalXp),
                    level,
                    remainingXp,
                    xpForNextLevel);
            }

            remainingXp -= xpForNextLevel;
            level++;
        }
    }
}

public sealed record LevelState(
    long TotalXp,
    int Level,
    long CurrentLevelProgress,
    int XpForNextLevel);
