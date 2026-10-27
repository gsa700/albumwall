using System.Text;

namespace AlbumWall.Domain;

/// <summary>
/// How a value goes into a PowerShell script the updater and the uninstaller write (Deadwax's
/// PowerShell.Quote and CommandLine, from its review of 2026-10-24, L13; AlbumWall's review of
/// 2026-10-26 found the same gap here).
/// </summary>
public static class PowerShellText
{
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
