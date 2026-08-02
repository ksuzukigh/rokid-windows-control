namespace RokidControl.Core.Navigation;

public sealed class FocusRestoringPointerState
{
    public bool IsPending { get; private set; }

    public void MarkWindowDeactivated()
    {
        IsPending = true;
    }

    public void Expire()
    {
        IsPending = false;
    }

    public bool TryConsume()
    {
        if (!IsPending)
        {
            return false;
        }

        IsPending = false;
        return true;
    }
}
