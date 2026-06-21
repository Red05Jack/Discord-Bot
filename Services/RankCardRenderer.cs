using SkiaSharp;

namespace DiscordXpBot.Services;

public sealed class RankCardRenderer
{
    private const int Width = 1090;
    private const int Height = 250;
    private static readonly SKColor TextColor = new(246, 246, 248);
    private static readonly SKColor MutedTextColor = new(255, 255, 255, 140);
    private static readonly SKTypeface NormalTypeface =
        SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Normal) ??
        SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal) ??
        SKTypeface.Default;
    private static readonly SKTypeface BoldTypeface =
        SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold) ??
        SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ??
        SKTypeface.Default;

    public MemoryStream Render(RankCardData data, Stream? avatarStream)
    {
        var accent = ParseColor(data.AccentColor);
        using var bitmap = new SKBitmap(
            new SKImageInfo(
                Width,
                Height,
                SKColorType.Rgba8888,
                SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        DrawBackground(canvas, accent);
        DrawAvatar(canvas, avatarStream, data.Username, accent);
        DrawText(canvas, data);
        DrawProgress(canvas, data.ProgressRatio, accent);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var stream = new MemoryStream();
        encoded.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }

    private static void DrawBackground(SKCanvas canvas, SKColor accent)
    {
        var card = new SKRoundRect(
            new SKRect(2, 2, Width - 2, Height - 2),
            20,
            20);
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(Width, 0),
                [new SKColor(53, 59, 91), new SKColor(21, 23, 31)],
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRoundRect(card, fill);

        using var border = new SKPaint
        {
            IsAntialias = true,
            Color = accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4
        };
        canvas.DrawRoundRect(card, border);
    }

    private static void DrawAvatar(
        SKCanvas canvas,
        Stream? avatarStream,
        string username,
        SKColor accent)
    {
        const float outerX = 18;
        const float outerY = 18;
        const float outerSize = 190;
        const float innerX = 34;
        const float innerY = 34;
        const float innerSize = 158;

        using var background = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(17, 19, 26)
        };
        canvas.DrawCircle(
            outerX + outerSize / 2,
            outerY + outerSize / 2,
            outerSize / 2,
            background);

        using var border = new SKPaint
        {
            IsAntialias = true,
            Color = accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 6
        };
        canvas.DrawCircle(
            outerX + outerSize / 2,
            outerY + outerSize / 2,
            outerSize / 2 - 3,
            border);

        canvas.Save();
        using var clip = new SKPath();
        clip.AddCircle(
            innerX + innerSize / 2,
            innerY + innerSize / 2,
            innerSize / 2);
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);

        using var avatar = TryDecodeAvatar(avatarStream);
        if (avatar is not null)
        {
            var source = GetCenteredSquare(avatar.Width, avatar.Height);
            var destination = new SKRect(
                innerX,
                innerY,
                innerX + innerSize,
                innerY + innerSize);
            using var imagePaint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(avatar, source, destination, imagePaint);
        }
        else
        {
            using var fallback = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(innerX, innerY),
                    new SKPoint(innerX + innerSize, innerY + innerSize),
                    [new SKColor(67, 74, 112), new SKColor(24, 27, 39)],
                    null,
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(
                innerX + innerSize / 2,
                innerY + innerSize / 2,
                innerSize / 2,
                fallback);

            var initial = string.IsNullOrWhiteSpace(username)
                ? "?"
                : username.Trim()[0].ToString().ToUpperInvariant();
            using var initialStyle = CreateTextStyle(64, bold: true, TextColor);
            var width = initialStyle.Font.MeasureText(initial);
            var metrics = initialStyle.Font.Metrics;
            var baseline =
                innerY + innerSize / 2 - (metrics.Ascent + metrics.Descent) / 2;
            canvas.DrawText(
                initial,
                innerX + (innerSize - width) / 2,
                baseline,
                SKTextAlign.Left,
                initialStyle.Font,
                initialStyle.Paint);
        }

        canvas.Restore();
    }

    private static void DrawText(SKCanvas canvas, RankCardData data)
    {
        using var playerStyle = CreateTextStyle(38, bold: true, TextColor);
        using var labelStyle = CreateTextStyle(29, bold: false, MutedTextColor);
        using var valueStyle = CreateTextStyle(66, bold: false, TextColor);
        using var xpStyle = CreateTextStyle(31, bold: false, MutedTextColor);

        var username = TrimToWidth(data.Username, playerStyle.Font, 430);
        canvas.DrawText(
            username,
            278,
            140,
            SKTextAlign.Left,
            playerStyle.Font,
            playerStyle.Paint);

        var segments = new[]
        {
            new TextSegment("RANG", labelStyle, 16),
            new TextSegment($"#{data.Rank}", valueStyle, 16),
            new TextSegment("LEVEL", labelStyle, 16),
            new TextSegment(data.Level.ToString(), valueStyle, 0)
        };
        var totalWidth = segments.Sum(segment =>
            segment.Style.Font.MeasureText(segment.Text) + segment.Gap);
        var x = Width - 24 - totalWidth;
        foreach (var segment in segments)
        {
            var baseline = segment.Style.Font.Size >= 60 ? 76 : 63;
            canvas.DrawText(
                segment.Text,
                x,
                baseline,
                SKTextAlign.Left,
                segment.Style.Font,
                segment.Style.Paint);
            x += segment.Style.Font.MeasureText(segment.Text) + segment.Gap;
        }

        var xpText =
            $"{FormatCompact(data.CurrentLevelProgress)} / " +
            $"{FormatCompact(data.XpForNextLevel)} XP";
        canvas.DrawText(
            xpText,
            Width - 24 - xpStyle.Font.MeasureText(xpText),
            143,
            SKTextAlign.Left,
            xpStyle.Font,
            xpStyle.Paint);
    }

    private static void DrawProgress(
        SKCanvas canvas,
        double progressRatio,
        SKColor accent)
    {
        var outer = new SKRoundRect(
            new SKRect(268, 158, 1062, 216),
            18,
            18);
        using var border = new SKPaint
        {
            IsAntialias = true,
            Color = accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4
        };
        canvas.DrawRoundRect(outer, border);

        var ratio = Math.Clamp(progressRatio, 0, 1);
        var fillWidth = (float)((outer.Rect.Width - 10) * ratio);
        if (fillWidth <= 0)
        {
            return;
        }

        var fill = new SKRoundRect(
            new SKRect(273, 163, 273 + fillWidth, 211),
            Math.Min(10, fillWidth / 2),
            Math.Min(10, fillWidth / 2));
        using var fillPaint = new SKPaint
        {
            IsAntialias = true,
            Color = accent
        };
        canvas.DrawRoundRect(fill, fillPaint);
    }

    private static TextStyle CreateTextStyle(
        float size,
        bool bold,
        SKColor color)
    {
        var font = new SKFont(bold ? BoldTypeface : NormalTypeface, size);
        var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color
        };
        return new TextStyle(font, paint);
    }

    private static SKBitmap? TryDecodeAvatar(Stream? avatarStream)
    {
        if (avatarStream is null)
        {
            return null;
        }

        try
        {
            avatarStream.Position = 0;
            return SKBitmap.Decode(avatarStream);
        }
        catch
        {
            return null;
        }
    }

    private static SKRect GetCenteredSquare(int width, int height)
    {
        var size = Math.Min(width, height);
        var left = (width - size) / 2f;
        var top = (height - size) / 2f;
        return new SKRect(left, top, left + size, top + size);
    }

    private static string TrimToWidth(
        string value,
        SKFont font,
        float maxWidth)
    {
        if (font.MeasureText(value) <= maxWidth)
        {
            return value;
        }

        var trimmed = value;
        while (trimmed.Length > 1 &&
               font.MeasureText($"{trimmed}…") > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return $"{trimmed}…";
    }

    private static SKColor ParseColor(string value)
    {
        if (SKColor.TryParse(value, out var color))
        {
            return color;
        }

        return SKColors.White;
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
        TextStyle Style,
        float Gap);

    private sealed class TextStyle(SKFont font, SKPaint paint) : IDisposable
    {
        public SKFont Font { get; } = font;
        public SKPaint Paint { get; } = paint;

        public void Dispose()
        {
            Font.Dispose();
            Paint.Dispose();
        }
    }
}

public sealed record RankCardData(
    string Username,
    int Rank,
    int Level,
    long CurrentLevelProgress,
    int XpForNextLevel,
    string AccentColor = "#FFFFFF")
{
    public double ProgressRatio =>
        XpForNextLevel <= 0
            ? 0
            : (double)CurrentLevelProgress / XpForNextLevel;
}
