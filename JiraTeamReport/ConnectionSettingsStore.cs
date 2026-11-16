using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;

namespace JiraTeamReport;

internal sealed record SavedConnection(string Url, string Login, string Password);

internal static class ConnectionSettingsStore
{
    private const string CredentialTarget = "JiraTeamReport.Connection";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JiraTeamReport", "connection.json");

    public static SavedConnection? Load()
    {
        Settings? settings;
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
        }
        catch { return null; }
        if (settings is null || !settings.Remember) return null;
        string password;
        try { password = ReadPassword(); }
        catch { password = ""; }
        return new SavedConnection(settings.Url ?? "", settings.Login ?? "", password);
    }

    public static void Save(string url, string login, string password)
    {
        WritePassword(login, password);
        var settings = new Settings { Remember = true, Url = url, Login = login };
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
    }

    public static void Clear()
    {
        if (CredDelete(CredentialTarget, GenericCredential, 0)) { }
        else
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error);
        }
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }

    private static void WritePassword(string login, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = IntPtr.Zero;
        try
        {
            if (bytes.Length > 0)
            {
                blob = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, blob, bytes.Length);
            }
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = CredentialTarget,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = login
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    private static string ReadPassword()
    {
        if (!CredRead(CredentialTarget, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return "";
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
            var length = checked((int)credential.CredentialBlobSize);
            var bytes = new byte[length];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, length);
            try { return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    [Serializable]
    private sealed class Settings
    {
        public bool Remember { get; set; }
        public string? Url { get; set; }
        public string? Login { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}


