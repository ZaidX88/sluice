using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Sluice.Platform.Windows;

/// <summary>
/// Windows titles a desktop app's notifications (name and icon) from a Start Menu shortcut that carries the
/// app's AppUserModelID. Without one it shows a placeholder ID. This keeps a per-user shortcut in place.
/// </summary>
public static class StartMenuShortcut
{
    public const string AppId = "Sluice";

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Sluice.lnk");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Sets this process's app ID and makes sure a shortcut with the same ID exists.</summary>
    public static void Ensure()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { /* cosmetic */ }
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            // Test and scratch builds must not take over the user's shortcut.
            if (exe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            // Keep a working shortcut (e.g. to an installed copy); replace a missing or broken one.
            if (File.Exists(ShortcutPath) && ReadTarget(ShortcutPath) is { } target && File.Exists(target) &&
                ReadAppId(ShortcutPath) == AppId) return;
            Write(ShortcutPath, exe);
        }
        catch
        {
            // Never stop Sluice from starting over a shortcut.
        }
    }

    /// <summary>Developer tool: (re)write the shortcut for this executable, wherever it runs from.</summary>
    public static void WriteForThisExe() => Write(ShortcutPath, Environment.ProcessPath!);

    private static void Write(string path, string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        link.SetIconLocation(exe, 0);
        link.SetDescription("Sluice â€” route applications through tunnels");
        var store = (IPropertyStore)link;
        var key = AppUserModelIdKey;
        var value = PropVariant.FromString(AppId);
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            value.Clear();
        }
        ((IPersistFile)link).Save(path, true);
    }

    private static string? ReadTarget(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(path, 0);
        var sb = new char[1024];
        link.GetPath(sb, sb.Length, IntPtr.Zero, 0);
        var s = new string(sb).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static string? ReadAppId(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(path, 0);
        var key = AppUserModelIdKey;
        ((IPropertyStore)link).GetValue(ref key, out var value);
        try { return value.Vt == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { value.Clear(); }
    }

    // ---------------- COM interop ----------------

    private static PropertyKey AppUserModelIdKey => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray)] char[] file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray)] char[] name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray)] char[] dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray)] char[] args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray)] char[] iconPath, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT holding a VT_LPWSTR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort R1, R2, R3;
        public IntPtr Pointer;
        public IntPtr Pad;

        public static PropVariant FromString(string s) => new() { Vt = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(s) };

        public void Clear()
        {
            if (Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(Pointer);
            Pointer = IntPtr.Zero;
        }
    }
}
