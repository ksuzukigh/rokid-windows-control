namespace RokidControl.Core.Navigation;

public static class PointerSelectionPolicy
{
    public static readonly TimeSpan FocusRestoringClickWindow =
        TimeSpan.FromMilliseconds(500);

    public static bool IsFocusRestoringClick(
        TimeSpan elapsedSinceWindowActivation) =>
        elapsedSinceWindowActivation >= TimeSpan.Zero &&
        elapsedSinceWindowActivation <= FocusRestoringClickWindow;

    public static bool ShouldEndApplicationSelection(
        TimeSpan elapsedSinceWindowActivation) =>
        !IsFocusRestoringClick(elapsedSinceWindowActivation);
}
