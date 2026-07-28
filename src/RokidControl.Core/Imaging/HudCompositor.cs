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

        var result = camera.Clone();
        for (var y = 0; y < camera.Height; y++)
        {
            for (var x = 0; x < camera.Width; x++)
            {
                var index = ((y * camera.Width) + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var hudValue = GetThickenedChannel(hud, x, y, channel, radius);
                    var foreground = (int)Math.Round(hudValue * intensity);
                    var background = camera.Pixels[index + channel];
                    result.Pixels[index + channel] = ScreenBlend(background, foreground);
                }

                result.Pixels[index + 3] = 255;
            }
        }

        return result;
    }

    private static byte GetThickenedChannel(
        BgraFrame frame,
        int centerX,
        int centerY,
        int channel,
        int radius)
    {
        byte maximum = 0;
        for (var y = Math.Max(0, centerY - radius);
             y <= Math.Min(frame.Height - 1, centerY + radius);
             y++)
        {
            for (var x = Math.Max(0, centerX - radius);
                 x <= Math.Min(frame.Width - 1, centerX + radius);
                 x++)
            {
                var value = frame.Pixels[((y * frame.Width) + x) * 4 + channel];
                maximum = Math.Max(maximum, value);
            }
        }

        return maximum;
    }

    private static byte ScreenBlend(byte background, int foreground)
    {
        var value = 255 - (((255 - background) * (255 - foreground) + 127) / 255);
        return (byte)Math.Clamp(value, 0, 255);
    }
}
