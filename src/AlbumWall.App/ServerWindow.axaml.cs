using Avalonia.Controls;

namespace AlbumWall.App;

/// <summary>
/// ServerSignIn in a dialog, for Preferences. Use via
/// <c>await new ServerWindow().ShowDialog&lt;IReadOnlyList&lt;Library&gt;?&gt;(owner)</c>;
/// null means he thought better of it.
///
/// It does not close until the server has said yes: a library that cannot sign
/// in is not worth adding, and the moment to find out is while the address and
/// the password are still in front of him.
/// </summary>
public partial class ServerWindow : Window
{
    public ServerWindow()
    {
        InitializeComponent();
        SignIn.Finished += chosen => Close(chosen);
        Opened += (_, _) => SignIn.FocusServer();
    }
}
