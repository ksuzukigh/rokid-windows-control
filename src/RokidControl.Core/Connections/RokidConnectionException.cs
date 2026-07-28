namespace RokidControl.Core.Connections;

public enum RokidConnectionError
{
    NoDevice,
    WifiUnavailable,
    WatchdogFailed,
    MissingResource,
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
            RokidConnectionError.WifiUnavailable =>
                "RokidをWi-Fiへ接続できませんでした。",
            RokidConnectionError.WatchdogFailed =>
                "Windows操作中のWi-Fi監視を開始できませんでした。",
            RokidConnectionError.MissingResource =>
                $"アプリに必要なファイルが見つかりません: {detail}",
            _ => detail ?? "Rokidへ接続できませんでした。",
        };
}

