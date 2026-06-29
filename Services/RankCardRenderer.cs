using System.Globalization;
using SkiaSharp;

namespace DiscordXpBot.Services;

public sealed class RankCardRenderer
{
    private const int Width = 1062;
    private const int Height = 180;

    private const float CardX = 80;
    private const float CardY = 5;
    private const float CardWidth = 980;
    private const float CardHeight = 170;
    private const float CardCornerRadius = 86;

    private const float AvatarX = 2;
    private const float AvatarY = 0;
    private const float AvatarSize = 180;

    private static readonly SKColor CardColor = new(5, 7, 12);
    private static readonly SKColor AvatarFallbackColor = new(17, 17, 17);
    private static readonly SKColor TextColor = SKColors.White;
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

        using var cardPath = CreateRightRoundedPath(
            new SKRect(CardX, CardY, CardX + CardWidth, CardY + CardHeight),
            CardCornerRadius);

        DrawCardBase(canvas, cardPath);
        DrawProgressGlow(canvas, cardPath, data.ProgressRatio, accent);
        DrawReadabilityStrip(canvas);
        DrawCardBorder(canvas, accent);
        DrawText(canvas, data);
        DrawAvatar(canvas, avatarStream, data.Username);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var stream = new MemoryStream();
        encoded.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }

    private static void DrawCardBase(SKCanvas canvas, SKPath cardPath)
    {
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Color = CardColor,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawPath(cardPath, fill);
    }

    private static void DrawProgressGlow(
        SKCanvas canvas,
        SKPath cardPath,
        double progressRatio,
        SKColor accent)
    {
        var ratio = Math.Clamp(progressRatio, 0, 1);
        if (ratio <= 0)
        {
            return;
        }

        canvas.Save();
        canvas.ClipPath(cardPath, SKClipOperation.Intersect, antialias: true);

        using var blur = SKImageFilter.CreateBlur(26, 26);
        using var glow = new SKPaint
        {
            IsAntialias = true,
            Color = accent.WithAlpha(230),
            ImageFilter = blur
        };

        var glowWidth = (float)(CardWidth * ratio);
        canvas.DrawRect(
            new SKRect(
                CardX,
                CardY - CardHeight,
                CardX + glowWidth,
                CardY + CardHeight * 2),
            glow);

        canvas.Restore();
    }

    private static void DrawReadabilityStrip(SKCanvas canvas)
    {
        using var stripPath = CreateRightRoundedPath(
            new SKRect(
                CardX + 72,
                CardY + 21,
                CardX + CardWidth - 24,
                CardY + 143),
            64);
        using var stripPaint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 128),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawPath(stripPath, stripPaint);
    }

    private static void DrawCardBorder(SKCanvas canvas, SKColor accent)
    {
        using var border = new SKPaint
        {
            IsAntialias = true,
            Color = accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var borderPath = new SKPath();
        var left = CardX;
        var top = CardY + 2;
        var right = CardX + CardWidth - 2;
        var bottom = CardY + CardHeight - 2;
        var radius = CardCornerRadius - 2;

        borderPath.MoveTo(left, top);
        borderPath.LineTo(right - radius, top);
        borderPath.ArcTo(
            new SKRect(right - radius * 2, top, right, top + radius * 2),
            -90,
            90,
            false);
        borderPath.LineTo(right, bottom - radius);
        borderPath.ArcTo(
            new SKRect(right - radius * 2, bottom - radius * 2, right, bottom),
            0,
            90,
            false);
        borderPath.LineTo(left, bottom);

        canvas.DrawPath(borderPath, border);
    }

    private static void DrawAvatar(
        SKCanvas canvas,
        Stream? avatarStream,
        string username)
    {
        using var background = new SKPaint
        {
            IsAntialias = true,
            Color = AvatarFallbackColor
        };
        canvas.DrawCircle(
            AvatarX + AvatarSize / 2,
            AvatarY + AvatarSize / 2,
            AvatarSize / 2,
            background);

        canvas.Save();
        using var clip = new SKPath();
        clip.AddCircle(
            AvatarX + AvatarSize / 2,
            AvatarY + AvatarSize / 2,
            AvatarSize / 2);
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);

        using var avatar = TryDecodeAvatar(avatarStream);
        if (avatar is not null)
        {
            var source = GetCenteredSquare(avatar.Width, avatar.Height);
            var destination = new SKRect(
                AvatarX,
                AvatarY,
                AvatarX + AvatarSize,
                AvatarY + AvatarSize);
            using var imagePaint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(avatar, source, destination, imagePaint);
        }
        else
        {
            using var fallback = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(AvatarX, AvatarY),
                    new SKPoint(AvatarX + AvatarSize, AvatarY + AvatarSize),
                    [new SKColor(45, 48, 65), new SKColor(12, 14, 20)],
                    null,
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(
                AvatarX + AvatarSize / 2,
                AvatarY + AvatarSize / 2,
                AvatarSize / 2,
                fallback);

            var initial = string.IsNullOrWhiteSpace(username)
                ? "?"
                : username.Trim()[0].ToString().ToUpperInvariant();
            using var initialStyle = CreateTextStyle(64, bold: true, TextColor);
            var width = initialStyle.Font.MeasureText(initial);
            var metrics = initialStyle.Font.Metrics;
            var baseline =
                AvatarY + AvatarSize / 2 - (metrics.Ascent + metrics.Descent) / 2;
            canvas.DrawText(
                initial,
                AvatarX + (AvatarSize - width) / 2,
                baseline,
                SKTextAlign.Left,
                initialStyle.Font,
                initialStyle.Paint);
        }

        canvas.Restore();
    }

    private static void DrawText(SKCanvas canvas, RankCardData data)
    {
        using var playerStyle = CreateTextStyle(42, bold: true, TextColor);
        using var xpStyle = CreateTextStyle(32, bold: false, MutedTextColor);
        using var rankLabelStyle = CreateTextStyle(30, bold: false, MutedTextColor);
        using var rankValueStyle = CreateTextStyle(44, bold: true, TextColor);

        var rankSegments = new[]
        {
            new TextSegment("RANG", rankLabelStyle, 8),
            new TextSegment($"#{data.Rank}", rankValueStyle, 8),
            new TextSegment("LEVEL", rankLabelStyle, 8),
            new TextSegment(data.Level.ToString(CultureInfo.InvariantCulture), rankValueStyle, 0)
        };

        var rankRight = CardX + CardWidth - 58;
        var rankWidth = MeasureSegments(rankSegments);
        var rankX = rankRight - rankWidth;
        var rankBaseline = GetCenteredBaseline(
            CardY + CardHeight / 2,
            rankValueStyle.Font);

        var playerX = CardX + 135;
        var availableNameWidth = Math.Max(220, rankX - playerX - 24);
        var username = TrimToWidth(data.Username, playerStyle.Font, availableNameWidth);
        DrawTextAtTop(
            canvas,
            username,
            playerX,
            CardY + 38,
            playerStyle);

        var xpText =
            $"{FormatCompact(data.CurrentLevelProgress)} / " +
            $"{FormatCompact(data.XpForNextLevel)} XP";
        var availableXpWidth = Math.Max(220, rankX - playerX - 24);
        xpText = TrimToWidth(xpText, xpStyle.Font, availableXpWidth);
        DrawTextAtTop(
            canvas,
            xpText,
            playerX,
            CardY + 98,
            xpStyle);

        var x = rankX;
        foreach (var segment in rankSegments)
        {
            canvas.DrawText(
                segment.Text,
                x,
                rankBaseline,
                SKTextAlign.Left,
                segment.Style.Font,
                segment.Style.Paint);
            x += segment.Style.Font.MeasureText(segment.Text) + segment.Gap;
        }
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

    private static void DrawTextAtTop(
        SKCanvas canvas,
        string text,
        float x,
        float top,
        TextStyle style)
    {
        var baseline = top - style.Font.Metrics.Ascent;
        canvas.DrawText(
            text,
            x,
            baseline,
            SKTextAlign.Left,
            style.Font,
            style.Paint);
    }

    private static float GetCenteredBaseline(float centerY, SKFont font)
    {
        var metrics = font.Metrics;
        return centerY - (metrics.Ascent + metrics.Descent) / 2;
    }

    private static float MeasureSegments(IEnumerable<TextSegment> segments) =>
        segments.Sum(segment =>
            segment.Style.Font.MeasureText(segment.Text) + segment.Gap);

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

    private static SKPath CreateRightRoundedPath(SKRect rect, float radius)
    {
        var path = new SKPath();
        using var roundRect = new SKRoundRect();
        roundRect.SetRectRadii(
            rect,
            [
                new SKPoint(0, 0),
                new SKPoint(radius, radius),
                new SKPoint(radius, radius),
                new SKPoint(0, 0)
            ]);
        path.AddRoundRect(roundRect);
        return path;
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

        const string suffix = "...";
        var trimmed = value;
        while (trimmed.Length > 1 &&
               font.MeasureText($"{trimmed}{suffix}") > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return $"{trimmed}{suffix}";
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
            >= 1_000_000 => $"{(value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture)}M",
            >= 1_000 => $"{(value / 1_000d).ToString("0.##", CultureInfo.InvariantCulture)}K",
            _ => value.ToString("N0", CultureInfo.InvariantCulture)
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
