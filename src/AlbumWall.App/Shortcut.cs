// AlbumWall — which modifier the shortcuts use, and how they are written.
//
// Ctrl on Windows and Linux, Cmd on the Mac, where Ctrl+arrows belong to the
// system (Spaces) and every other app's shortcuts are Cmd. Spelled the way each
// platform spells its own: "Ctrl + F" there, "⌘F" here. Asked for on 2026-10-14,
// the first day on the Mac, after the window buttons ("lets do the cmd keys").
//
// One key differs, not just the modifier: COMPACT. Cmd+M is Minimize on every
// Mac window, so compact is Option+Cmd+M there, which is also what Music.app
// uses for its MiniPlayer.

using Avalonia.Input;

namespace AlbumWall.App;

internal static class Shortcut
{
    private static bool Mac => OperatingSystem.IsMacOS();

    /// The modifier the app's shortcuts are made with.
    public static KeyModifiers Primary => Mac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// What has to be held, besides Primary, for compact.
    public static KeyModifiers CompactExtra => Mac ? KeyModifiers.Alt : KeyModifiers.None;

    /// A shortcut as the platform writes it: "Ctrl + F", "⌘F".
    public static string Name(string key) => Mac ? "⌘" + key : "Ctrl + " + key;

    public static string Compact => Mac ? "⌥⌘M" : "Ctrl + M";
}
