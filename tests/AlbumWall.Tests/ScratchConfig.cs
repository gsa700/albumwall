using System.Runtime.CompilerServices;

namespace AlbumWall.Tests;

/// Before any test: settings pointed at a scratch folder, THEN Spindle's
/// install and update code told this is AlbumWall. One initializer, in that
/// order: Settings.Path and Session.Path are fixed the first time Settings is
/// used, from ALBUMWALL_CONFIG_DIR, and Program.Identity uses Settings.Path.
/// Two initializers ran in the wrong order (2026-11-02): the settings tests'
/// own check stopped them before anything was written.
internal static class ScratchConfig
{
    public static readonly string Dir =
        Path.Combine(Path.GetTempPath(), "albumwall-tests-config-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Point()
    {
        Environment.SetEnvironmentVariable("ALBUMWALL_CONFIG_DIR", Dir);
        Spindle.Install.AppInfo.Use(AlbumWall.App.Program.Identity);
    }
}
