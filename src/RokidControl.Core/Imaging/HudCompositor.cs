namespace RokidControl.Core.Imaging;

public static class HudCompositor
{
    private const double MinimumHudIntensity = 0.5;
    private const double MaximumHudIntensity = 4.3;

    public static BgraFrame Compose(
        BgraFrame camera,
        BgraFrame hud,
        double visibility,
        double thickness,
        int? outputWidth = null,
        int? outputHeight = null)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(hud);

        var targetWidth = outputWidth ?? camera.Width;
        var targetHeight = outputHeight ?? camera.Height;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        var normalizedCamera = AspectFill(camera, targetWidth, targetHeight);
        var normalizedHud = AspectFill(hud, targetWidth, targetHeight);
        var normalizedVisibility = Math.Clamp(visibility, 0, 1);
        var intensity =
            MinimumHudIntensity +
            normalizedVisibility *
            (MaximumHudIntensity - MinimumHudIntensity);
        var radius = Math.Clamp(
            (int)Math.Round(
                Math.Max(thickness, 0),
                MidpointRounding.AwayFromZero),
            0,
            2);
        var foregroundPixels = radius == 0
            ? normalizedHud.Pixels
            : Thicken(normalizedHud, radius);

        var result = normalizedCamera.Clone();
        for (var index = 0;
             index < normalizedCamera.Pixels.Length;
             index += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var foreground = Math.Clamp(
                    (int)Math.Round(
                        foregroundPixels[index + channel] * intensity),
                    0,
                    255);
                var background = normalizedCamera.Pixels[index + channel];
                result.Pixels[index + channel] = ScreenBlend(
                    background,
                    foreground);
            }

            result.Pixels[index + 3] = 255;
        }

        return result;
    }

    public static BgraFrame ShowDeviceScreen(
        BgraFrame screen,
        int outputWidth,
        int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        return AspectFill(screen, outputWidth, outputHeight).Clone();
    }

    private static BgraFrame AspectFill(
        BgraFrame source,
        int targetWidth,
        int targetHeight)
    {
        if (source.Width == targetWidth && source.Height == targetHeight)
        {
            return source;
        }

        var scale = Math.Max(
            targetWidth / (double)source.Width,
            targetHeight / (double)source.Height);
        var sourceWidth = targetWidth / scale;
        var sourceHeight = targetHeight / scale;
        var sourceLeft = (source.Width - sourceWidth) / 2;
        var sourceTop = (source.Height - sourceHeight) / 2;
        var pixels = new byte[checked(targetWidth * targetHeight * 4)];

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Clamp(
                (int)Math.Floor(sourceTop + (y + 0.5) / scale),
                0,
                source.Height - 1);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Clamp(
                    (int)Math.Floor(sourceLeft + (x + 0.5) / scale),
                    0,
                    source.Width - 1);
                var sourceIndex = ((sourceY * source.Width) + sourceX) * 4;
                var targetIndex = ((y * targetWidth) + x) * 4;
                source.Pixels.AsSpan(sourceIndex, 4)
                    .CopyTo(pixels.AsSpan(targetIndex, 4));
            }
        }

        return new BgraFrame(targetWidth, targetHeight, pixels);
    }

    private static byte[] Thicken(BgraFrame frame, int radius)
    {
        var horizontal = new byte[frame.Pixels.Length];
        var result = new byte[frame.Pixels.Length];
        for (var y = 0; y < frame.Height; y++)
        {
            var rowStart = y * frame.Width * 4;
            for (var x = 0; x < frame.Width; x++)
            {
                var outputIndex = rowStart + (x * 4);
                var left1 = rowStart + (Math.Max(0, x - 1) * 4);
                var right1 =
                    rowStart + (Math.Min(frame.Width - 1, x + 1) * 4);
                var left2 = radius == 2
                    ? rowStart + (Math.Max(0, x - 2) * 4)
                    : left1;
                var right2 = radius == 2
                    ? rowStart + (Math.Min(frame.Width - 1, x + 2) * 4)
                    : right1;
                for (var channel = 0; channel < 3; channel++)
                {
                    horizontal[outputIndex + channel] = radius == 2
                        ? Max5(
                            frame.Pixels[left2 + channel],
                            frame.Pixels[left1 + channel],
                            frame.Pixels[outputIndex + channel],
                            frame.Pixels[right1 + channel],
                            frame.Pixels[right2 + channel])
                        : Max3(
                            frame.Pixels[left1 + channel],
                            frame.Pixels[outputIndex + channel],
                            frame.Pixels[right1 + channel]);
                }
            }
        }

        for (var y = 0; y < frame.Height; y++)
        {
            var rowStart = y * frame.Width * 4;
            var rowAbove1 = Math.Max(0, y - 1) * frame.Width * 4;
            var rowBelow1 =
                Math.Min(frame.Height - 1, y + 1) * frame.Width * 4;
            var rowAbove2 = radius == 2
                ? Math.Max(0, y - 2) * frame.Width * 4
                : rowAbove1;
            var rowBelow2 = radius == 2
                ? Math.Min(frame.Height - 1, y + 2) * frame.Width * 4
                : rowBelow1;
            for (var x = 0; x < frame.Width; x++)
            {
                var columnOffset = x * 4;
                var outputIndex = rowStart + columnOffset;
                for (var channel = 0; channel < 3; channel++)
                {
                    result[outputIndex + channel] = radius == 2
                        ? Max5(
                            horizontal[rowAbove2 + columnOffset + channel],
                            horizontal[rowAbove1 + columnOffset + channel],
                            horizontal[outputIndex + channel],
                            horizontal[rowBelow1 + columnOffset + channel],
                            horizontal[rowBelow2 + columnOffset + channel])
                        : Max3(
                            horizontal[rowAbove1 + columnOffset + channel],
                            horizontal[outputIndex + channel],
                            horizontal[rowBelow1 + columnOffset + channel]);
                }

                result[outputIndex + 3] = 255;
            }
        }

        return result;
    }

    private static byte Max3(byte first, byte second, byte third) =>
        Math.Max(first, Math.Max(second, third));

    private static byte Max5(
        byte first,
        byte second,
        byte third,
        byte fourth,
        byte fifth) =>
        Math.Max(
            Math.Max(first, second),
            Math.Max(third, Math.Max(fourth, fifth)));

    private static byte ScreenBlend(byte background, int foreground)
    {
        var value = 255 - (((255 - background) * (255 - foreground) + 127) / 255);
        return (byte)Math.Clamp(value, 0, 255);
    }
}
