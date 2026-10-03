using Avalonia.Controls;

namespace AlbumWall.App;

/// <summary>
/// Asks for a Navidrome server and a sign-in, tries it, and hands back the
/// library to add. Use via <c>await new ServerWindow().ShowDialog&lt;Library?&gt;(owner)</c>;
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
        CancelButton.Click += (_, _) => Close(null);
        SignInButton.Click += async (_, _) => await SignIn();
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
        try
        {
            await Task.Run(() => new Domain.Navidrome("", server, user, salt, token).Ping());
        }
        catch (Domain.NavidromeException ex)
        {
            Say(ex.Unreachable ? $"{server} is not answering ({ex.Message})."
                               : $"{server} would not sign {user} in: {ex.Message}.");
            SignInButton.IsEnabled = true;
            return;
        }
        Close(Library.Navidrome(server, user, salt, token));
    }

    private void Say(string text)
    {
        ResultText.Text = text;
        ResultText.IsVisible = true;
    }
}
