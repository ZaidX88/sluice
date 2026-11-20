using System.Text;

namespace Sluice.Platform.Linux;

/// <summary>
/// Start at login (#69): an XDG autostart entry, <c>$XDG_CONFIG_HOME/autostart/sluice.desktop</c> (by default
/// <c>~/.config/autostart/</c>), which GNOME, KDE, Xfce and the other desktops start at login. It runs this copy of
/// Sluice. One entry for all copies: turning autostart on in one points it there, turning it off removes it.
/// </summary>
internal sealed class LinuxAutostart
{
    public const string FileName = "sluice.desktop";

    private readonly string _entryPath;
    private readonly string? _binary;

    /// <param name="entryPath">The entry's path (see <see cref="EntryPathFor"/>).</param>
    /// <param name="binary">The binary to start; null when there is none that can be written into an entry.</param>
    public LinuxAutostart(string entryPath, string? binary)
    {
        _entryPath = entryPath;
        // No home folder (a service without HOME): no entry. A path with a line break can't be written into one.
        _binary = Path.IsPathRooted(entryPath) && binary is not null && binary.IndexOfAny(['\n', '\r', '\0']) < 0 ? binary : null;
    }

    /// <summary>The entry's path for these environment values (XDG Base Directory: a relative XDG_CONFIG_HOME is ignored).</summary>
    public static string EntryPathFor(string? xdgConfigHome, string home)
    {
        var config = !string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome)
            ? xdgConfigHome
            : Path.Combine(home, ".config");
        return Path.Combine(config, "autostart", FileName);
    }

    public string EntryPath => _entryPath;

    public bool Supported => _binary is not null;

    /// <summary>
    /// On when the entry exists, isn't switched off (<c>Hidden=true</c>, <c>X-GNOME-Autostart-enabled=false</c>) and
    /// starts this copy. Setting it writes or removes the entry; a failure leaves it as it was (read it again to see).
    /// </summary>
    public bool Enabled
    {
        get
        {
            if (_binary is null) return false;
            try { return File.Exists(_entryPath) && StartsBinary(File.ReadAllText(_entryPath), _binary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        set
        {
            if (_binary is null) return;
            try
            {
                if (value)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_entryPath)!);
                    var temp = _entryPath + ".new";
                    File.WriteAllText(temp, Entry(_binary));
                    File.Move(temp, _entryPath, overwrite: true);
                }
                else
                {
                    File.Delete(_entryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unchanged */ }
        }
    }

    /// <summary>The entry that starts <paramref name="binary"/> at login.</summary>
    internal static string Entry(string binary) => $"""
        [Desktop Entry]
        Type=Application
        Name=Sluice
        Comment=Route each app's connections direct, blocked or through a tunnel
        Exec={EscapeValue(QuoteExecArgument(binary))}
        Icon=sluice
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """.ReplaceLineEndings("\n");

    /// <summary>Whether <paramref name="entry"/> is switched on and its <c>Exec</c> starts <paramref name="binary"/>.</summary>
    internal static bool StartsBinary(string entry, string binary)
    {
        var values = ReadMainGroup(entry);
        if (values.TryGetValue("Hidden", out var hidden) && hidden.Trim() == "true") return false;
        if (values.TryGetValue("X-GNOME-Autostart-enabled", out var enabled) && enabled.Trim() == "false") return false;
        return values.TryGetValue("Exec", out var exec) && FirstExecArgument(exec) == binary;
    }

    /// <summary>The keys of the [Desktop Entry] group, with string escapes (\s \n \t \r \\) undone.</summary>
    private static Dictionary<string, string> ReadMainGroup(string entry)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inMain = false;
        foreach (var raw in entry.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[') { inMain = line == "[Desktop Entry]"; continue; }
            var eq = line.IndexOf('=');
            if (!inMain || eq <= 0) continue;
            values[line[..eq].Trim()] = UnescapeValue(line[(eq + 1)..].Trim());   // a repeated key: the last wins, as in GLib
        }
        return values;
    }

    // ---------- the Desktop Entry Specification's two levels of escaping ----------

    /// <summary>Characters that make an Exec argument need quotes.</summary>
    private const string Reserved = " \t\n\"'\\><~|&;$*?#()`";

    /// <summary>An Exec argument: quoted when needed, with <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> escaped inside the
    /// quotes, and <c>%</c> doubled (field codes).</summary>
    internal static string QuoteExecArgument(string arg)
    {
        var text = arg.Replace("%", "%%", StringComparison.Ordinal);
        if (text.Length > 0 && text.IndexOfAny(Reserved.ToCharArray()) < 0) return text;
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c is '"' or '`' or '$' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The string-level escaping of a value: a backslash is written as <c>\\</c>.</summary>
    private static string EscapeValue(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string UnescapeValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length) { sb.Append(value[i]); continue; }
            sb.Append(value[++i] switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', var c => c });
        }
        return sb.ToString();
    }

    /// <summary>The program an Exec value starts (its first argument, quotes and escapes undone); null when malformed.</summary>
    internal static string? FirstExecArgument(string exec)
    {
        var s = exec.TrimStart();
        if (s.Length == 0) return null;
        var sb = new StringBuilder();
        if (s[0] == '"')
        {
            for (var i = 1; i < s.Length; i++)
            {
                if (s[i] == '"') return sb.ToString().Replace("%%", "%", StringComparison.Ordinal);
                if (s[i] == '\\' && i + 1 < s.Length) i++;
                sb.Append(s[i]);
            }
            return null;   // no closing quote
        }
        var end = s.IndexOfAny([' ', '\t']);
        return (end < 0 ? s : s[..end]).Replace("%%", "%", StringComparison.Ordinal);
    }
}
