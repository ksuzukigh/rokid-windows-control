namespace RokidControl.Core.Connections;

public static class CameraAppPolicy
{
    private static readonly string[] OriginalCameraPackages =
    [
        "com.rokid.os.sprite.assistserver",
        "com.android.camera2",
    ];

    public static bool IsOriginalCameraForeground(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var isCurrentActivity =
                line.Contains("topResumedActivity=", StringComparison.Ordinal) ||
                line.StartsWith("mResumedActivity:", StringComparison.Ordinal) ||
                line.StartsWith("ResumedActivity:", StringComparison.Ordinal);
            if (isCurrentActivity && OriginalCameraPackages.Any(
                    packageName => line.Contains(
                        packageName + "/",
                        StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
