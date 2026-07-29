namespace RokidControl.Core.Processes;

public enum StandardSessionExitAction
{
    Quit,
    Reconnect,
}

public static class StandardSessionExitPolicy
{
    public static StandardSessionExitAction Decide(
        int exitCode,
        bool connectionAlive) =>
        exitCode == 0 && connectionAlive
            ? StandardSessionExitAction.Quit
            : StandardSessionExitAction.Reconnect;
}
