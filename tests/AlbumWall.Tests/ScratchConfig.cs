using System.Runtime.CompilerServices;

namespace AlbumWall.Tests;

/// Settings.Path and Session.Path are fixed the first time Settings is used,
/// from ALBUMWALL_CONFIG_DIR: pointed at a scratch folder before any test
/// runs, so no test ever reads or writes the settings of whoever runs them.
internal static class ScratchConfig
{
    public static readonly string Dir =
        Path.Combine(Path.GetTempPath(), "albumwall-tests-config-" + Guid.NewGuid().ToString("N")[..8]);

    [ModuleInitializer]
    internal static void Point() => Environment.SetEnvironmentVariable("ALBUMWALL_CONFIG_DIR", Dir);
}
