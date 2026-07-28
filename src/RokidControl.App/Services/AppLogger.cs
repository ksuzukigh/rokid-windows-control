using System.Globalization;
using System.IO;
using System.Text;

namespace RokidControl.App.Services;

internal sealed class AppLogger : IDisposable
{
    private const long MaximumLogSize = 2_000_000;
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public AppLogger(string logFile)
    {
        var directory = Path.GetDirectoryName(logFile) ??
            throw new ArgumentException("ログの保存先が不正です。", nameof(logFile));
        Directory.CreateDirectory(directory);
        var rotatedFile = Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(logFile) + ".1.log");
        if (File.Exists(logFile) &&
            new FileInfo(logFile).Length > MaximumLogSize)
        {
            File.Delete(rotatedFile);
            File.Move(logFile, rotatedFile);
        }

        _writer = new StreamWriter(
            new FileStream(
                logFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public void Log(string message)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}"));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }
}
