namespace RokidControl.Core.Navigation;

public sealed class KeyboardNavigationState
{
    public LowerNavigationItem? LowerItem { get; private set; }

    public bool IsLowerRow => LowerItem is not null;

    public LowerNavigationItem EnterLowerRow()
    {
        LowerItem = LowerNavigationItem.Home;
        return LowerItem.Value;
    }

    public LowerNavigationItem? MoveLowerRow(int offset)
    {
        if (LowerItem is null)
        {
            return null;
        }

        var next = Math.Clamp(
            (int)LowerItem.Value + offset,
            (int)LowerNavigationItem.Memo,
            (int)LowerNavigationItem.Applications);
        LowerItem = (LowerNavigationItem)next;
        return LowerItem;
    }

    public bool LeaveLowerRow()
    {
        var wasLowerRow = LowerItem is not null;
        LowerItem = null;
        return wasLowerRow;
    }
}

