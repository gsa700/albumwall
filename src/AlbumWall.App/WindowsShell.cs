// AlbumWall — who Windows thinks this program is, and where to find it.
//
// A program that arrives through the Store is given an identity. One that is
// just an exe in a folder has none, and Windows makes one up from the file
// name: the media flyout says "AlbumWall.App.exe", and the taskbar cannot pin
// the running window and a shortcut to it as the same thing.
//
// The identity is an AppUserModelID, and it is said in two places:
//
//   * by the PROCESS, at startup, before any window exists; and
//   * on a Start Menu SHORTCUT, as a property of the .lnk file.
//
// I wrote at first that either half alone does nothing. That was wrong, and he
// found it by not doing what I said: with only the first half in place the
// media flyout already showed the right name ("flyout already has the right
// name, no shortcut required I guess" — Windows 11, build 26200). So the
// process's claim is what names it there. The shortcut is for the other things
// an identity buys: being found by Start search, and a pinned taskbar button
// that the running window groups with instead of sitting beside. It carries
// the same ID so Windows knows they are the same program, which is why it
// cannot be made with WScript.Shell — that can set a target and an icon, but
// not the property. It takes IShellLink and IPropertyStore.
//
// Nothing here throws outward. No shortcut is an inconvenience and must never be
// the reason a music player does not start.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace AlbumWall.App;

public static partial class WindowsShell
{
    /// Permanent. Shortcuts already made carry this string, so changing it
    /// orphans them. The form is Microsoft's: Company.Product — his GitHub
    /// account standing in for the company he does not have.
    public const string AppId = "gsa700.AlbumWall";

    private const string Description =
        "A player for a music library you own, built around seeing the whole collection at once";

    /// Called first thing in Main. Harmless anywhere but Windows.
    public static void ClaimIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(AppId);
            if (hr != 0) Console.WriteLine($"[shell] could not set the app id: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shell] could not set the app id: {ex.Message}");
        }
    }

    /// %AppData%\Microsoft\Windows\Start Menu\Programs\AlbumWall.lnk — per user,
    /// no elevation, and where the Start Menu's own search looks.
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), App.DisplayName + ".lnk");

    public static bool HasShortcut => OperatingSystem.IsWindows() && File.Exists(ShortcutPath);

    /// True when the shortcut exists and points at THIS executable. It can exist
    /// and point somewhere else: the app was moved, or built to a new folder —
    /// as it was the day media keys changed the target framework.
    public static bool ShortcutIsCurrent
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(ShortcutPath)) return false;
            try
            {
                return string.Equals(Long(ReadTarget(ShortcutPath)), Long(Environment.ProcessPath ?? ""),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// True when the shortcut points at the INSTALLED copy and that copy exists
    /// — in which case it is the installer's shortcut, not a stray one, and a
    /// development or loose copy has no business re-pointing or removing it. The
    /// installed copy re-asserts it at every start anyway, so taking it would
    /// not even last.
    public static bool ShortcutBelongsToInstalledCopy
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(ShortcutPath)) return false;
            try
            {
                var installed = Install.InstallService.InstalledExePath;
                return File.Exists(installed)
                    && string.Equals(Long(ReadTarget(ShortcutPath)), Long(installed), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// Creates or replaces the Start Menu shortcut. Returns null on success, or
    /// a sentence that can be shown to him as it is.
    public static string? AddShortcut()
    {
        if (!OperatingSystem.IsWindows()) return "Only Windows has a Start Menu.";
        if (Environment.ProcessPath is not { } exe) return "Could not tell where the program is running from.";
        try
        {
            Write(ShortcutPath, exe);
            Console.WriteLine($"[shell] shortcut -> {exe}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shell] shortcut failed: {ex.Message}");
            return $"Could not create the shortcut: {ex.Message}";
        }
    }

    /// Writes a shortcut at `lnkPath` pointing at `exePath`, carrying the app's
    /// AppUserModelID. For the installer, which points shortcuts at the INSTALLED
    /// copy rather than at whatever is running. Null on success, or a sentence.
    public static string? WriteShortcut(string lnkPath, string exePath)
    {
        if (!OperatingSystem.IsWindows()) return "Only Windows has shortcuts of this kind.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            Write(lnkPath, exePath);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string? RemoveShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not remove the shortcut: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Write(string lnk, string exe)
    {
        var link = CreateShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription(Description);
            link.SetIconLocation(exe, 0);           // the icon compiled into the exe

            // The property that makes it THE shortcut for this app.
            var store = (IPropertyStore)link;
            var key = AppUserModelIdKey;
            var value = new PropVariant { vt = VT_LPWSTR, pointer = Marshal.StringToCoTaskMemUni(AppId) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.pointer);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally
        {
            ((ComObject)(object)link).FinalRelease();
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string ReadTarget(string lnk)
    {
        var link = CreateShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0);
            var buffer = new char[1024];
            fixed (char* p = buffer) link.GetPath(p, buffer.Length, 0, 0);
            return new string(buffer).TrimEnd('\0');
        }
        finally
        {
            ((ComObject)(object)link).FinalRelease();
        }
    }

    // ---- The shell's interfaces, declared only as far as they are used. Method
    // ---- ORDER is the vtable, so the unused ones stay as placeholders.
    //
    // Source-generated COM ([GeneratedComInterface] and ComWrappers), because the
    // built-in kind ([ComImport] and `new CShellLink()`) does not exist under
    // Native AOT: 0.8.0 on Windows logged "Common Language Runtime detected an
    // invalid program" for CShellLink's constructor at every install and wrote no
    // shortcut (Mobilenode, 2026-10-29). The buffers the shell fills are plain
    // pointers, so nothing is left for the marshaller to guess.

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private const uint CLSCTX_INPROC_SERVER = 1;

    [SupportedOSPlatform("windows")]
    private static IShellLinkW CreateShellLink()
    {
        var iid = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(CLSID_ShellLink, 0, CLSCTX_INPROC_SERVER, iid, out var unknown));
        try
        {
            return (IShellLinkW)ComWrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(unknown);           // the wrapper holds its own reference
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint unknown);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, char[] longPath, uint size);

    /// The same file can be named two ways on Windows, a short 8.3 form
    /// (DAVIDE~1) and the long one (David Erickson), and the shell stores
    /// whichever it was given. Compared as text, a shortcut to this very exe then
    /// looked like a shortcut to "a different copy".
    private static string Long(string path)
    {
        var buffer = new char[1024];
        var n = GetLongPathName(path, buffer, (uint)buffer.Length);
        return n > 0 && n < buffer.Length ? new string(buffer, 0, (int)n) : path;
    }

    /// System.AppUserModel.ID
    private static PropertyKey AppUserModelIdKey =>
        new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public IntPtr pointer;
        public IntPtr padding;
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214F9-0000-0000-C000-000000000046")]
    internal unsafe partial interface IShellLinkW
    {
        void GetPath(char* file, int max, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription(char* name, int max);
        void SetDescription(string name);
        void GetWorkingDirectory(char* dir, int max);
        void SetWorkingDirectory(string dir);
        void GetArguments(char* args, int max);
        void SetArguments(string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(char* path, int max, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath(string file);
    }

    [GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load(string file, uint mode);
        void Save(string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted(string file);
        void GetCurFile(out nint file);
    }
}
