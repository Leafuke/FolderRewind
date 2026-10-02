using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FolderRewind.Services;

[SupportedOSPlatform("windows")]
internal static class ShellShortcutIdentity
{
    internal const string MsiAppId = "Leafuke.FolderRewind.Msi";
    private static readonly Guid AppModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    internal static void SetWindowIdentity(IntPtr hwnd, string appId, string executable)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try { SetRelaunchIdentity(store, appId, executable); }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    internal static void SetShortcutIdentity(string path, string appId, string executable)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out var store));
        try { SetRelaunchIdentity(store, appId, executable); }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    private static void SetRelaunchIdentity(IPropertyStore store, string appId, string executable)
    {
        // Set the relaunch properties before the explicit AppID. A taskbar pin
        // must not depend on a version-specific Windows Installer icon cache.
        SetString(store, 2, $"\"{executable}\"");
        SetString(store, 3, executable + ",0");
        SetString(store, 4, "FolderRewind");
        SetString(store, 5, appId);
        store.Commit();
    }

    private static void SetString(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey { FormatId = AppModelFormat, Id = id };
        var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(text) };
        try { store.SetValue(ref key, ref value); }
        finally { Marshal.FreeCoTaskMem(value.Value); }
    }
    internal static string? GetAppUserModelId(string path) => GetShortcutProperty(path, 5);

    internal static string? GetShortcutProperty(string path, uint propertyId)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store));
        var key = new PropertyKey { FormatId = AppModelFormat, Id = propertyId };
        var value = default(PropVariant);
        try
        {
            store.GetValue(ref key, out value);
            return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : null;
        }
        finally { PropVariantClear(ref value); Marshal.FinalReleaseComObject(store); }
    }
    internal static void SetAppUserModelId(string path, string appId)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out var store));
        var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(appId) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally { Marshal.FreeCoTaskMem(value.Value); Marshal.FinalReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid FormatId; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, uint flags,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
