namespace RokidControl.Core.Processes;

public sealed record CommandResult(
    int ExitCode,
    string Output,
    string Error,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    public string CombinedOutput =>
        string.Join(
            Environment.NewLine,
            new[] { Output, Error }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

