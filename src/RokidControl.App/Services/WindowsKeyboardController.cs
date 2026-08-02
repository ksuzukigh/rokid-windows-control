using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using RokidControl.Core.Connections;
using RokidControl.Core.Navigation;

namespace RokidControl.App.Services;

internal sealed class WindowsKeyboardController : IDisposable
{
    private static readonly KeyboardShortcutDebouncer DirectShortcutDebouncer =
        new(TimeSpan.FromMilliseconds(750));
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private readonly IRokidInputSession _input;
    private readonly AppLogger _logger;
    private readonly KeyboardCommandProcessor _commandProcessor;
    private readonly Channel<QueuedAction> _actions =
        Channel.CreateUnbounded<QueuedAction>(
            new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<uint> _heldKeys = [];
    private readonly HashSet<uint> _swallowedKeys = [];
    private readonly NativeMethods.HookProcedure _keyboardProcedure;
    private readonly Task _actionTask;
    private IntPtr _keyboardHook;
    private int _targetProcessId;
    private bool _disposed;

    public WindowsKeyboardController(
        IRokidInputSession input,
        AppLogger logger,
        int screenWidth,
        int screenHeight)
    {
        _input = input;
        _logger = logger;
        _commandProcessor = new KeyboardCommandProcessor(
            input,
            screenWidth,
            screenHeight,
            logger.Log);
        _commandProcessor.ApplicationMenuModeChanged +=
            CommandProcessor_ApplicationMenuModeChanged;
        _keyboardProcedure = KeyboardHook;
        _actionTask = ProcessActionsAsync(_cancellation.Token);
    }

    public event Action? QuitRequested;

    public event Action<bool>? ApplicationMenuModeChanged;

    public void Start(int targetProcessId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetProcessId);
        if (_keyboardHook != IntPtr.Zero)
        {
            throw new InvalidOperationException("入力監視はすでに開始しています。");
        }

        _targetProcessId = targetProcessId;
        var module = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            WhKeyboardLl,
            _keyboardProcedure,
            module,
            0);
        if (_keyboardHook == IntPtr.Zero)
        {
            DisposeHooks();
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windowsの入力監視を開始できませんでした。");
        }

        _logger.Log($"Windows入力監視開始 targetPid={targetProcessId}");
    }

    public void EndApplicationMenuSelection()
    {
        if (!_disposed)
        {
            _actions.Writer.TryWrite(
                new QueuedAction(ResetApplicationMenu: true));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeHooks();
        _actions.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            _actionTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException exception)
            when (exception.InnerExceptions.All(
                item => item is OperationCanceledException))
        {
            // Normal shutdown.
        }

        _cancellation.Dispose();
        _commandProcessor.ApplicationMenuModeChanged -=
            CommandProcessor_ApplicationMenuModeChanged;
        _input.Dispose();
        _logger.Log("Windows入力監視終了");
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(
                _keyboardHook,
                code,
                message,
                data);
        }

        var keyboardInput =
            Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardInput>(data);
        var virtualKey = keyboardInput.VirtualKey;
        var messageValue = message.ToInt32();
        if (messageValue is WmKeyUp or WmSysKeyUp)
        {
            _heldKeys.Remove(virtualKey);
            if (_swallowedKeys.Remove(virtualKey))
            {
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(
                _keyboardHook,
                code,
                message,
                data);
        }

        if (messageValue is not (WmKeyDown or WmSysKeyDown) ||
            !TargetIsActive())
        {
            return NativeMethods.CallNextHookEx(
                _keyboardHook,
                code,
                message,
                data);
        }

        if (!_heldKeys.Add(virtualKey))
        {
            return _swallowedKeys.Contains(virtualKey)
                ? (IntPtr)1
                : NativeMethods.CallNextHookEx(
                    _keyboardHook,
                    code,
                    message,
                    data);
        }

        var controlPressed =
            (NativeMethods.GetAsyncKeyState(VkControl) & 0x8000) != 0;
        var altPressed =
            (NativeMethods.GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        var command = WindowsKeyCommandMapper.Map(
            (int)virtualKey,
            controlPressed,
            altPressed);
        if (command is null)
        {
            return NativeMethods.CallNextHookEx(
                _keyboardHook,
                code,
                message,
                data);
        }

        _swallowedKeys.Add(virtualKey);
        if (command is KeyboardCommand.Home or
                KeyboardCommand.Memo or
                KeyboardCommand.Applications &&
            !DirectShortcutDebouncer.ShouldAccept(
                command.Value,
                keyboardInput.Time))
        {
            _actions.Writer.TryWrite(
                new QueuedAction(
                    LogMessage:
                        $"Duplicate shortcut suppressed command={command} " +
                        $"eventTime={keyboardInput.Time} " +
                        $"flags=0x{keyboardInput.Flags:X}"));
            return (IntPtr)1;
        }

        _actions.Writer.TryWrite(
            new QueuedAction(
                LogMessage:
                    $"Keyboard command accepted command={command} " +
                    $"eventTime={keyboardInput.Time} " +
                    $"flags=0x{keyboardInput.Flags:X}"));
        if (command == KeyboardCommand.Quit)
        {
            QuitRequested?.Invoke();
        }
        else
        {
            _actions.Writer.TryWrite(new QueuedAction(command.Value));
        }

        return (IntPtr)1;
    }

    private bool TargetIsActive()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(
            foreground,
            out var processId);
        return processId == (uint)_targetProcessId;
    }

    private async Task ProcessActionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var action in _actions.Reader.ReadAllAsync(
                               cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (action.LogMessage is not null)
                    {
                        _logger.Log(action.LogMessage);
                        continue;
                    }

                    if (action.ResetApplicationMenu)
                    {
                        _commandProcessor.ResetApplicationMenu();
                        continue;
                    }

                    await _commandProcessor.HandleAsync(
                        action.Command!.Value,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.Log(
                        $"入力操作を処理できませんでした。次の操作を待ちます: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void CommandProcessor_ApplicationMenuModeChanged(bool active)
    {
        ApplicationMenuModeChanged?.Invoke(active);
    }

    private void DisposeHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

    }

    private sealed record QueuedAction(
        KeyboardCommand? Command = null,
        bool ResetApplicationMenu = false,
        string? LogMessage = null);

    private static class NativeMethods
    {
        internal delegate IntPtr HookProcedure(
            int code,
            IntPtr message,
            IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct LowLevelKeyboardInput
        {
            internal uint VirtualKey;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal nuint ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(
            int hook,
            HookProcedure callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr message,
            IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);
    }
}
