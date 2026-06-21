namespace DiscordXpBot.Services;

public static class MessageXpCalculator
{
    public static int Calculate(ulong messageId, int minXp, int maxXp)
    {
        if (minXp < 0 || maxXp < minXp)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minXp),
                "Der XP-Bereich ist ungültig.");
        }

        // SplitMix64: eine feste mathematische Abbildung der Discord-Nachrichten-ID.
        // Gleiche ID + gleicher Bereich ergeben immer exakt denselben XP-Wert.
        var value = unchecked(messageId + 0x9E3779B97F4A7C15UL);
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        value ^= value >> 31;

        var range = (ulong)(maxXp - minXp + 1);
        return minXp + (int)(value % range);
    }
}
