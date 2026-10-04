using Avalonia.Controls;

namespace AlbumWall.App;

/// <summary>
/// Asks for a Navidrome server and a sign-in, tries it, and hands back the
/// libraries to add. Use via
/// <c>await new ServerWindow().ShowDialog&lt;IReadOnlyList&lt;Library&gt;?&gt;(owner)</c>;
/// null means he thought better of it.
///
/// It does not close until the server has said yes: a library that cannot sign
/// in is not worth adding, and the moment to find out is while the address and
/// the password are still in front of him.
///
/// A server may keep more than one library of its own (his has a lossless one
/// and a lossy one). Then the window asks which, after the sign-in and before
/// closing, and each one ticked comes back as a library: two walls, not one
/// with both formats of an album folded together.
/// </summary>
public partial class ServerWindow : Window
{
    public ServerWindow()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(null);
        SignInButton.Click += async (_, _) =>
        {
            if (_signedIn is null) await SignIn();
            else Add();
        };
        Opened += (_, _) => ServerBox.Focus();
    }

    private async Task SignIn()
    {
        var server = Domain.Navidrome.NormalizeServer(ServerBox.Text ?? "");
        var user = (UserBox.Text ?? "").Trim();
        var password = PasswordBox.Text ?? "";
        if (server.Length == 0 || user.Length == 0 || password.Length == 0)
        {
            Say("All three are needed.");
            return;
        }

        SignInButton.IsEnabled = false;
        Say("Signing in…");
        var (salt, token) = Domain.Navidrome.Credentials(password);
        IReadOnlyList<Domain.Navidrome.Folder> folders;
        try
        {
            folders = await Task.Run(() =>
            {
                var navidrome = new Domain.Navidrome("", server, user, salt, token);
                navidrome.Ping();
                return navidrome.Folders();
            });
        }
        catch (Domain.NavidromeException ex)
        {
            Say(ex.Unreachable ? $"{server} is not answering ({ex.Message})."
                               : $"{server} would not sign {user} in: {ex.Message}.");
            SignInButton.IsEnabled = true;
            return;
        }

        // Nothing to choose between: its one library, or a server that does
        // not say what it has, which is then taken whole as before.
        if (folders.Count < 2)
        {
            Close(new List<Library> { Library.Navidrome(server, user, salt, token, folders.FirstOrDefault()) });
            return;
        }

        _signedIn = (server, user, salt, token);
        ServerBox.IsEnabled = UserBox.IsEnabled = PasswordBox.IsEnabled = false;
        foreach (var folder in folders)
            FolderList.Children.Add(new CheckBox { Content = folder.Name, Tag = folder, IsChecked = true, FontSize = 13 });
        FoldersPanel.IsVisible = true;
        ResultText.IsVisible = false;
        SignInButton.Content = "Add";
        SignInButton.IsEnabled = true;
    }

    /// The sign-in that worked, once it has: what is left is which libraries.
    private (string Server, string User, string Salt, string Token)? _signedIn;

    private void Add()
    {
        if (_signedIn is not { } s) return;
        var chosen = FolderList.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true)
            .Select(box => Library.Navidrome(s.Server, s.User, s.Salt, s.Token,
                                             (Domain.Navidrome.Folder)box.Tag!, named: true))
            .ToList();
        if (chosen.Count == 0)
        {
            Say("Tick at least one.");
            return;
        }
        Close(chosen);
    }

    private void Say(string text)
    {
        ResultText.Text = text;
        ResultText.IsVisible = true;
    }
}
