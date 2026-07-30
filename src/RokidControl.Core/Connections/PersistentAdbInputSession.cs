using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RokidControl.Core.Connections;

public sealed class PersistentAdbInputSession : IRokidInputSession
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LauncherCacheDuration = TimeSpan.FromSeconds(2);

    private readonly string _adbPath;
    private readonly string _serial;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Action<string>? _log;
    private Process? _process;
    private (bool Value, DateTimeOffset CheckedAt)? _launcherCache;
    private long _commandSequence;
    private int _disposed;

    public PersistentAdbInputSession(
        string adbPath,
        string serial,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);

        _adbPath = adbPath;
        _serial = serial;
        _environment = environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                environment,
                StringComparer.OrdinalIgnoreCase);
        _log = log;
        _process = StartProcess();
    }

    public Task VerifyReadyAsync(CancellationToken cancellationToken = default)
    {
        return SendCommandAsync(":", cancellationToken);
    }

    public Task SendKeyEventAsync(
        string androidKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(androidKey);
        if (androidKey.Length > 64 ||
            androidKey.Any(character =>
                character is not (>= 'A' and <= 'Z') &&
                character is not (>= '0' and <= '9') &&
                character != '_'))
        {
            throw new ArgumentException(
                "Androidキー名の形式が正しくありません。",
                nameof(androidKey));
        }

        if (androidKey is not (
                "KEYCODE_DPAD_LEFT" or
                "KEYCODE_DPAD_RIGHT" or
                "KEYCODE_DPAD_UP" or
                "KEYCODE_DPAD_DOWN"))
        {
            _launcherCache = null;
        }

        return SendCommandAsync(
            $"input keyevent {androidKey}",
            cancellationToken);
    }

    public Task TapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        ValidatePoint(x, y);
        _launcherCache = null;
        return SendCommandAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"input tap {x} {y}"),
            cancellationToken);
    }

    public Task WakeHomeAndTapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        ValidatePoint(x, y);
        _launcherCache = null;
        return SendCommandAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"input keyevent KEYCODE_WAKEUP && input keyevent KEYCODE_HOME && sleep 0.35 && input tap {x} {y}"),
            cancellationToken);
    }

    public Task WakeHomeAsync(
        CancellationToken cancellationToken = default)
    {
        _launcherCache = null;
        return SendCommandAsync(
            "input keyevent KEYCODE_WAKEUP && " +
            "input keyevent KEYCODE_HOME",
            cancellationToken);
    }

    public async Task<bool> IsLauncherActiveAsync(
        CancellationToken cancellationToken = default)
    {
        var cache = _launcherCache;
        if (cache is not null &&
            DateTimeOffset.UtcNow - cache.Value.CheckedAt < LauncherCacheDuration)
        {
            return cache.Value.Value;
        }

        try
        {
            var output = await ExecuteCommandAsync(
                "dumpsys activity activities | grep 'ResumedActivity:' || true",
                cancellationToken).ConfigureAwait(false);
            var isLauncher = output.Contains(
                "com.rokid.os.sprite.launcher/",
                StringComparison.Ordinal);
            _launcherCache = (isLauncher, DateTimeOffset.UtcNow);
            return isLauncher;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Invoke(
                $"ホーム画面を確認できませんでした。前回値を使用します: {exception.Message}");
            return cache?.Value ?? false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var process = Interlocked.Exchange(ref _process, null);
        StopAndDisposeProcess(process);
    }

    private async Task SendCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        _ = await ExecuteCommandAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> ExecuteCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Process? process = null;
        try
        {
            using var timeoutSource = new CancellationTokenSource(CommandTimeout);
            using var commandSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutSource.Token);
            var commandToken = commandSource.Token;

            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            process = GetOrStartProcess();

            var marker = string.Create(
                CultureInfo.InvariantCulture,
                $"__ROKID_CONTROL_END_{Interlocked.Increment(ref _commandSequence)}__");
            await process.StandardInput.WriteLineAsync(
                $"{command}; __rokid_status=$?; echo {marker}:$__rokid_status"
                    .AsMemory(),
                commandToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(commandToken)
                .ConfigureAwait(false);

            var output = new StringBuilder();
            var statusPrefix = $"{marker}:";
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(
                    commandToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new IOException(
                        "高速入力用のADB接続から応答がありません。");
                }

                if (line.StartsWith(statusPrefix, StringComparison.Ordinal))
                {
                    var statusText = line[statusPrefix.Length..];
                    if (!int.TryParse(
                            statusText,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var status))
                    {
                        throw new IOException(
                            "高速入力用のADB接続から不正な応答を受信しました。");
                    }

                    if (status != 0)
                    {
                        throw new InvalidOperationException(
                            $"Rokidへの入力命令が失敗しました（終了コード {status}）。");
                    }

                    return output.ToString();
                }

                output.AppendLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            InvalidateProcess(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                "Rokidへのキー操作が時間内に完了しませんでした。");
        }
        catch (IOException exception)
        {
            InvalidateProcess(process);
            throw new InvalidOperationException(
                "Rokidへキー操作を送信できませんでした。",
                exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Process GetOrStartProcess()
    {
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    return process;
                }
            }
            catch (InvalidOperationException)
            {
                // Replace a process that could not report its state.
            }

            InvalidateProcess(process);
        }

        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        process = StartProcess();
        _process = process;
        _log?.Invoke("高速入力用のADB接続を再開しました。");
        return process;
    }

    private Process StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(_serial);
        startInfo.ArgumentList.Add("shell");

        foreach (var item in _environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        process.ErrorDataReceived += Process_ErrorDataReceived;
        process.Exited += Process_Exited;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "高速入力用のADB接続を開始できませんでした。");
            }

            process.StandardInput.NewLine = "\n";
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            process.ErrorDataReceived -= Process_ErrorDataReceived;
            process.Exited -= Process_Exited;
            process.Dispose();
            throw;
        }
    }

    private void InvalidateProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        _ = Interlocked.CompareExchange(ref _process, null, process);
        StopAndDisposeProcess(process);
    }

    private void StopAndDisposeProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        process.ErrorDataReceived -= Process_ErrorDataReceived;
        process.Exited -= Process_Exited;
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The ADB shell closed its input first.
                }

                if (!process.WaitForExit(500))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(500);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The ADB shell already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void ValidatePoint(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
    }

    private void Process_ErrorDataReceived(
        object sender,
        DataReceivedEventArgs eventArguments)
    {
        if (!string.IsNullOrWhiteSpace(eventArguments.Data))
        {
            _log?.Invoke($"高速入力ADBエラー {eventArguments.Data}");
        }
    }

    private void Process_Exited(object? sender, EventArgs eventArguments)
    {
        if (Volatile.Read(ref _disposed) == 0 && sender is Process process)
        {
            try
            {
                _log?.Invoke(
                    $"高速入力ADB終了 exitCode={process.ExitCode}");
            }
            catch (InvalidOperationException)
            {
                _log?.Invoke("高速入力ADBが終了しました。");
            }
        }
    }
}
