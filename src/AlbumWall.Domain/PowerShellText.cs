using System.Text;

namespace AlbumWall.Domain;

/// <summary>
/// How a value goes into a PowerShell script the updater and the uninstaller write (Deadwax's
/// PowerShell.Quote and CommandLine, from its review of 2026-10-24, L13; AlbumWall's review of
/// 2026-10-26 found the same gap here).
/// </summary>
public static class PowerShellText
{
    /// <summary>How a .ps1 the app writes is encoded: UTF-8 WITH its byte-order mark. Windows
    /// PowerShell 5.1 reads a file without one as the ANSI code page, so a path holding anything
    /// past ASCII (an O’Brien in the user's name, which every path under %LOCALAPPDATA% has) came
    /// out mangled: the update helper copied nothing, started nothing and left its marker in a
    /// folder it made up (found by Mobilenode's session running the helper for real, 2026-10-27).</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>A single-quoted literal. PowerShell takes the curly single quotes (U+2018 to
    /// U+201B) as quotes too, so a path like "Sean O’Brien" ended a literal escaped for ' alone;
    /// each is doubled, as PowerShell's own EscapeSingleQuotedStringContent does.</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (c is '\'' or (char)0x2018 or (char)0x2019 or (char)0x201A or (char)0x201B) sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>Arguments as one Windows command line (the rules CommandLineToArgvW reads back), for
    /// Start-Process: given a list, it joins the items with spaces and no quoting, so an argument
    /// with a space in it was split.</summary>
    public static string CommandLine(IEnumerable<string> args) => string.Join(" ", args.Select(Argument));

    private static string Argument(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')) return arg;
        var sb = new StringBuilder().Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
