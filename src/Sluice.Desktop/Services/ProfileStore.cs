using System.IO;
using Sluice.Core.Profiles;

namespace Sluice.Desktop.Services;

/// <summary>
/// Sluice's own profiles folder (#134): one <c>&lt;name&gt;.sluice</c> per profile in
/// <see cref="AppSettings.ProfilesFolder"/>. The profile's name is its file name, so the user never deals with paths.
/// </summary>
public sealed class ProfileStore(string folder)
{
    public static ProfileStore Default { get; } = new(AppSettings.ProfilesFolder);

    public string Folder { get; } = folder;

    /// <summary>The profiles' names, sorted.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder, "*" + ProfileFile.Extension)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public string PathFor(string name) => Path.Combine(Folder, name + ProfileFile.Extension);

    public bool Contains(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(Folder), PathComparison);

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Why <paramref name="name"/> can't be a profile's name, or null when it can. <paramref name="current"/>
    /// is the profile being renamed, which may keep its own name in another case.</summary>
    public string? CheckName(string? name, string? current = null)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return "Enter a name.";
        if (name.Length > 80) return "Use a name of 80 characters or fewer.";
        if (name.IndexOfAny(InvalidChars) >= 0 || name is "." or "..")
            return "A name can't contain / \\ : * ? \" < > | or control characters.";
        if (name.EndsWith('.')) return "A name can't end with a dot.";
        if (ReservedOnWindows.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            return $"\"{name}\" is reserved by Windows; choose another name.";
        if (current is not null && string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) return null;
        if (Exists(name)) return $"A profile called \"{name}\" already exists.";
        return null;
    }

    /// <summary><paramref name="name"/>, or "name (2)", "name (3)"… — the first one not taken.</summary>
    public string UniqueName(string name)
    {
        if (!Exists(name)) return name;
        for (var i = 2; ; i++)
            if (!Exists($"{name} ({i})")) return $"{name} ({i})";
    }

    /// <summary>Copies a profile file from anywhere into the folder under a free name; returns the copy's path. The
    /// original is left as it is.</summary>
    public string CopyIn(string source)
    {
        Directory.CreateDirectory(Folder);
        var raw = Path.GetFileNameWithoutExtension(source);
        var name = CheckName(raw, current: raw) is null ? raw : "Imported profile";   // a taken name gets " (2)"
        var target = PathFor(UniqueName(name));
        File.Copy(source, target);
        return target;
    }

    private static readonly char[] InvalidChars =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|', .. Enumerable.Range(0, 32).Select(i => (char)i)];

    private static readonly string[] ReservedOnWindows =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
