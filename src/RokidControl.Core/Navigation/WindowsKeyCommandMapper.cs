namespace RokidControl.Core.Navigation;

public static class WindowsKeyCommandMapper
{
    public static KeyboardCommand? Map(
        int virtualKey,
        bool controlPressed,
        bool altPressed)
    {
        if (controlPressed && virtualKey is 'Q')
        {
            return KeyboardCommand.Quit;
        }

        if (altPressed && virtualKey == 0x73)
        {
            return KeyboardCommand.Quit;
        }

        return virtualKey switch
        {
            0x25 => KeyboardCommand.Left,
            0x27 => KeyboardCommand.Right,
            0x0D => KeyboardCommand.Enter,
            0x1B => KeyboardCommand.Back,
            'H' => KeyboardCommand.Home,
            'M' => KeyboardCommand.Memo,
            'A' => KeyboardCommand.Applications,
            _ => null,
        };
    }
}
