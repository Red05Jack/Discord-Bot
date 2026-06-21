using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace DiscordXpBot.Services;

public sealed class RankCardRenderer
{
    private const int Width = 1090;
    private const int Height = 250;
    private static readonly Color Accent = Color.FromArgb(99, 255, 243);
    private static readonly Color TextColor = Color.FromArgb(246, 246, 248);
    private static readonly Color MutedTextColor = Color.FromArgb(140, 255, 255, 255);

    public MemoryStream Render(RankCardData data, Stream? avatarStream)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var background = new LinearGradientBrush(
            new Rectangle(0, 0, Width, Height),
            Color.FromArgb(53, 59, 91),
            Color.FromArgb(21, 23, 31),
            LinearGradientMode.Horizontal);
        using var cardPath = CreateRoundedRectangle(
            new RectangleF(2, 2, Width - 4, Height - 4),
            20);
        graphics.FillPath(background, cardPath);
        using var borderPen = new Pen(Accent, 4);
        graphics.DrawPath(borderPen, cardPath);

        DrawAvatar(graphics, avatarStream, data.Username);
        DrawText(graphics, data);
        DrawProgress(graphics, data.ProgressRatio);

        var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        return stream;
    }

    private static void DrawAvatar(
        Graphics graphics,
        Stream? avatarStream,
        string displayName)
    {
        const float outerX = 18;
        const float outerY = 18;
        const float outerSize = 190;
        const float innerX = 34;
        const float innerY = 34;
        const float innerSize = 158;

        using var avatarBackground = new SolidBrush(Color.FromArgb(17, 19, 26));
        graphics.FillEllipse(avatarBackground, outerX, outerY, outerSize, outerSize);
        using var avatarBorder = new Pen(Accent, 6);
        graphics.DrawEllipse(avatarBorder, outerX + 3, outerY + 3, outerSize - 6, outerSize - 6);

        var previousClip = graphics.Clip;
        using var avatarClip = new GraphicsPath();
        avatarClip.AddEllipse(innerX, innerY, innerSize, innerSize);
        graphics.SetClip(avatarClip);

        if (avatarStream is not null)
        {
            using var avatar = Image.FromStream(avatarStream);
            var source = GetCenteredSquare(avatar.Width, avatar.Height);
            graphics.DrawImage(
                avatar,
                new RectangleF(innerX, innerY, innerSize, innerSize),
                source,
                GraphicsUnit.Pixel);
        }
        else
        {
            using var fallback = new LinearGradientBrush(
                new RectangleF(innerX, innerY, innerSize, innerSize),
                Color.FromArgb(67, 74, 112),
                Color.FromArgb(24, 27, 39),
                LinearGradientMode.ForwardDiagonal);
            graphics.FillEllipse(fallback, innerX, innerY, innerSize, innerSize);
            using var initialsFont = new Font(
                "Arial",
                64,
                FontStyle.Bold,
                GraphicsUnit.Pixel);
            using var initialsBrush = new SolidBrush(TextColor);
            var initial = string.IsNullOrWhiteSpace(displayName)
                ? "?"
                : displayName.Trim()[0].ToString().ToUpperInvariant();
            var size = graphics.MeasureString(initial, initialsFont);
            graphics.DrawString(
                initial,
                initialsFont,
                initialsBrush,
                innerX + (innerSize - size.Width) / 2,
                innerY + (innerSize - size.Height) / 2);
        }

        graphics.Clip = previousClip;
    }

    private static void DrawText(Graphics graphics, RankCardData data)
    {
        using var playerFont = new Font("Arial", 38, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelFont = new Font("Arial", 29, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font("Arial", 66, FontStyle.Regular, GraphicsUnit.Pixel);
        using var xpFont = new Font("Arial", 31, FontStyle.Regular, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(TextColor);
        using var mutedBrush = new SolidBrush(MutedTextColor);

        var username = TrimToWidth(graphics, data.Username, playerFont, 430);
        graphics.DrawString(username, playerFont, textBrush, 278, 102);

        var segments = new[]
        {
            new TextSegment("RANG", labelFont, mutedBrush, 16),
            new TextSegment($"#{data.Rank}", valueFont, textBrush, 16),
            new TextSegment("LEVEL", labelFont, mutedBrush, 16),
            new TextSegment(data.Level.ToString(), valueFont, textBrush, 0)
        };
        var totalWidth = segments.Sum(segment =>
            graphics.MeasureString(segment.Text, segment.Font).Width + segment.Gap);
        var x = Width - 24 - totalWidth;
        foreach (var segment in segments)
        {
            var size = graphics.MeasureString(segment.Text, segment.Font);
            graphics.DrawString(
                segment.Text,
                segment.Font,
                segment.Brush,
                x,
                22 + (66 - size.Height) / 2);
            x += size.Width + segment.Gap;
        }

        var xpText =
            $"{FormatCompact(data.CurrentLevelProgress)} / {FormatCompact(data.XpForNextLevel)} XP";
        var xpSize = graphics.MeasureString(xpText, xpFont);
        graphics.DrawString(xpText, xpFont, mutedBrush, Width - 24 - xpSize.Width, 116);
    }

    private static void DrawProgress(Graphics graphics, double progressRatio)
    {
        var outer = new RectangleF(268, 158, 794, 58);
        using var outerPath = CreateRoundedRectangle(outer, 18);
        using var border = new Pen(Accent, 4);
        graphics.DrawPath(border, outerPath);

        var ratio = Math.Clamp(progressRatio, 0, 1);
        var fillWidth = (float)((outer.Width - 10) * ratio);
        if (fillWidth <= 0)
        {
            return;
        }

        var fill = new RectangleF(outer.X + 5, outer.Y + 5, fillWidth, outer.Height - 10);
        using var fillPath = CreateRoundedRectangle(fill, Math.Min(10, fill.Width / 2));
        using var fillBrush = new SolidBrush(Accent);
        graphics.FillPath(fillBrush, fillPath);
    }

    private static Rectangle GetCenteredSquare(int width, int height)
    {
        var size = Math.Min(width, height);
        return new Rectangle((width - size) / 2, (height - size) / 2, size, size);
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(
            rectangle.Right - diameter,
            rectangle.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string TrimToWidth(
        Graphics graphics,
        string value,
        Font font,
        float maxWidth)
    {
        if (graphics.MeasureString(value, font).Width <= maxWidth)
        {
            return value;
        }

        var trimmed = value;
        while (trimmed.Length > 1 &&
               graphics.MeasureString($"{trimmed}…", font).Width > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return $"{trimmed}…";
    }

    private static string FormatCompact(long value)
    {
        return value switch
        {
            >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
            >= 1_000 => $"{value / 1_000d:0.##}K",
            _ => value.ToString("N0")
        };
    }

    private sealed record TextSegment(
        string Text,
        Font Font,
        Brush Brush,
        float Gap);
}

public sealed record RankCardData(
    string Username,
    int Rank,
    int Level,
    long CurrentLevelProgress,
    int XpForNextLevel)
{
    public double ProgressRatio =>
        XpForNextLevel <= 0
            ? 0
            : (double)CurrentLevelProgress / XpForNextLevel;
}
