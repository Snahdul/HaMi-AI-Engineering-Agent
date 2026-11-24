using System.Runtime.InteropServices;

namespace HaMi.Agentic.Cli.Runtime;

/// <summary>
/// Dedicated host-only GitHub Issues credential in the signed-in Windows
/// user's Credential Manager. Never invokes gh auth or stores a PAT in Git.
/// </summary>
internal static class WindowsIssueCredentialStore
{
    internal const string Target = "HaMi-AI-Engineering-Agent/GitHub-Issues/Snahdul";
    private const int GenericCredential = 1;
    private const int NotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);

    public static string? Read() => ReadDedicated(Target);

    internal const string PublicationTarget = "HaMi-AI-Engineering-Agent/GitHub-Publication/Snahdul";

    internal static string? ReadPublication() => ReadDedicated(PublicationTarget);

    private static string? ReadDedicated(string target)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(target, GenericCredential, 0, out IntPtr address))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new InvalidOperationException(
                "Windows Credential Manager failed to read the dedicated GitHub token ("
                + error + ").");
        }

        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(address);
            if (credential.Type != GenericCredential
                || credential.CredentialBlob == IntPtr.Zero
                || credential.CredentialBlobSize is < 20 or > 2048
                || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("Stored GitHub credential has invalid metadata.");
            return Marshal.PtrToStringUni(credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / 2));
        }
        finally
        {
            CredFree(address);
        }
    }

    public static bool Exists()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!CredRead(Target, GenericCredential, 0, out IntPtr address))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return false;
            throw new InvalidOperationException(
                "Windows Credential Manager could not inspect the GitHub Issues credential ("
                + error + ").");
        }
        CredFree(address);
        return true;
    }

    // Token enrollment is deliberately performed through Windows
    // Credential Manager UI. The earlier terminal-based enrollment was
    // unreliable on HaMi11 and has been removed. The host only reads the
    // dedicated generic credential; it never invokes personal gh auth.
}
