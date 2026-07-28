namespace RokidControl.Core.Imaging;

public static class HudCompositor
{
    public static BgraFrame Compose(
        BgraFrame camera,
        BgraFrame hud,
        double visibility,
        double thickness)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(hud);

        if (camera.Width != hud.Width || camera.Height != hud.Height)
        {
            throw new ArgumentException("Camera and HUD frames must have the same dimensions.");
        }

        var intensity = Math.Clamp(visibility, 0, 1);
        var radius = thickness switch
        {
            >= 0.67 => 2,
            >= 0.01 => 1,
            _ => 0,
        };
        var foregroundPixels = radius == 0
            ? hud.Pixels
            : Thicken(hud, radius);

        var result = camera.Clone();
        for (var index = 0; index < camera.Pixels.Length; index += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var foreground = (int)Math.Round(
                    foregroundPixels[index + channel] * intensity);
                var background = camera.Pixels[index + channel];
                result.Pixels[index + channel] = ScreenBlend(
                    background,
                    foreground);
            }

            result.Pixels[index + 3] = 255;
        }

        return result;
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
