namespace RokidControl.Core.Connections;

public enum RokidConnectionError
{
    NoDevice,
    WatchdogFailed,
    MissingResource,
    SafetyUnverified,
    PlaintextListenerRemains,
}

public sealed class RokidConnectionException : Exception
{
    public RokidConnectionException(
        RokidConnectionError error,
        string? detail = null,
        Exception? innerException = null)
        : base(GetMessage(error, detail), innerException)
    {
        Error = error;
    }

    public RokidConnectionError Error { get; }

    private static string GetMessage(
        RokidConnectionError error,
        string? detail) =>
        error switch
        {
            RokidConnectionError.NoDevice =>
                "Rokidへ接続できませんでした。Rokidで「Wi-Fi ON」を開くか、開発用5ピンケーブルを接続してください。",
            RokidConnectionError.WatchdogFailed =>
                "Windows操作中のWi-Fi監視を開始できませんでした。",
            RokidConnectionError.MissingResource =>
                $"アプリに必要なファイルが見つかりません: {detail}",
            RokidConnectionError.SafetyUnverified =>
                "Rokidの安全確認ができなかったため、起動を中止しました。暗号化されていないADB接続の入口が残っている可能性があります。Rokidを再起動し、開発用5ピンケーブルでWindows PCにつないでから、もう一度起動してください。",
            RokidConnectionError.PlaintextListenerRemains =>
                "暗号化されていないADB接続の入口がRokidに残っています。Rokidを再起動し、開発用5ピンケーブルでWindows PCにつないでから、もう一度起動してください。",
            _ => detail ?? "Rokidへ接続できませんでした。",
        };
}
