using Terminal.Gui;

namespace Dispatch.Cli;

internal static class TerminalGuiTheme
{
    public static void Apply()
    {
        Colors.Base.Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black);
        Colors.Base.Focus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan);
        Colors.Base.HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black);
        Colors.Base.HotFocus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan);
        Colors.Menu.Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black);
        Colors.Menu.Focus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan);
        Colors.Menu.HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black);
        Colors.Menu.HotFocus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan);
        Colors.Dialog.Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black);
        Colors.Dialog.Focus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan);
        Colors.Dialog.HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black);
        Colors.Dialog.HotFocus = Application.Driver.MakeAttribute(Color.Black, Color.BrightCyan);
    }
}
