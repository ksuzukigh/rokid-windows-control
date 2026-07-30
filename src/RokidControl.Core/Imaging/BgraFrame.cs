namespace RokidControl.Core.Imaging;

public sealed class BgraFrame
{
    public BgraFrame(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        var expectedLength = checked(width * height * 4);
        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Pixel length must be {expectedLength} bytes.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => checked(Width * 4);

    public byte[] Pixels { get; }

    public BgraFrame Clone() => new(Width, Height, (byte[])Pixels.Clone());
}
