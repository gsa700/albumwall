namespace AlbumWall.Tests;

public class AppInfoTests
{
    /// The version the update check compares is this app's own (found
    /// 2026-11-02 before release: from Spindle it said 1.0.0, so no copy would
    /// have been offered an update).
    [Fact]
    public void The_update_check_compares_this_apps_version() =>
        Assert.Equal(typeof(AlbumWall.App.Program).Assembly.GetName().Version!.ToString(3), Spindle.Install.InstallService.CurrentVersion);

    [Fact]
    public void The_identity_follows_the_apps_conventions()
    {
        var app = Spindle.Install.AppInfo.Current;
        Assert.Equal("AlbumWall", app.Name);
        Assert.Equal("gsa700/albumwall", app.Repo);
        Assert.Equal("io.github.gsa700.AlbumWall", app.BundleId);
        Assert.Equal("gsa700.AlbumWall", app.WindowsAppId);
        Assert.Equal("ALBUMWALL_SUPERVISED", app.SupervisedVariable);    // the Pi kiosk's launcher sets it
        Assert.Equal("ALBUMWALL_UPDATE_FEED", app.FeedVariable);
        Assert.Equal("albumwall.desktop", Spindle.Install.DesktopEntry.FileName);
        Assert.NotNull(app.AppAssembly.GetManifestResourceStream("app-icon.png"));     // the Linux install's icon
    }

    /// The libraries beside the program: the same list as scripts/release.sh's,
    /// with SQLite's, which only AlbumWall carries.
    [Fact]
    public void The_native_libraries_include_sqlite()
    {
        Assert.Contains("libe_sqlite3.so", Spindle.Install.InstallLayout.NativeLibraries(windows: false));
        Assert.Contains("e_sqlite3.dll", Spindle.Install.InstallLayout.NativeLibraries(windows: true));
    }
}
