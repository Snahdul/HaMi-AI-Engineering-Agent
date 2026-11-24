using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

namespace HaMi.Agentic.Cli.Runtime;

/// <summary>
/// Dedicated host-only GitHub Issues credential in the signed-in Windows
/// user's Credential Manager. Never invokes gh auth or stores a PAT in Git.
/// </summary>
internal static class WindowsIssueCredentialStore
{
    internal const string Target = "HaMi-AI-Engineering-Agent/GitHub-Issues/Snahdul";
    private const int GenericCredential = 1;
    private const int LocalMachinePersistence = 2;
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

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);

    // ReadConsole line input supports terminal clipboard paste. Disable
    // console echo before reading and restore the original flags even if
    // validation or the native credential operation fails.
    private const int StandardInputHandle = -10;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    private static void ReadPastedToken(SecureString secret)
    {
        IntPtr input = GetStdHandle(StandardInputHandle);
        if (input == IntPtr.Zero || input == new IntPtr(-1)
            || !GetConsoleMode(input, out uint originalMode))
            throw new InvalidOperationException(
                "Interactive Windows console input unavailable; use Windows Terminal or PowerShell.");

        uint maskedMode = (originalMode | EnableLineInput) & ~EnableEchoInput;
        if (!SetConsoleMode(input, maskedMode))
            throw new InvalidOperationException(
                "Cannot disable console echo; token input refused for security.");

        try
        {
            // Console.ReadKey loses pasted text in some terminals.
            // Console.Read reads the entire terminal-edited line (including
            // pasted input) without echo; SecureString receives one char at
            // a time, avoiding an immutable managed plaintext token string.
            while (true)
            {
                int next = Console.Read();
                if (next is -1 or '\r' or '\n') break;
                if (next is < 33 or > 126 || secret.Length >= 512)
                    throw new InvalidOperationException(
                        "GitHub token contains invalid characters or exceeds the permitted length.");
                secret.AppendChar((char)next);
            }
        }
        finally
        {
            if (!SetConsoleMode(input, originalMode))
                Console.Error.WriteLine(
                    "WARNING: Could not restore original console mode. Close this terminal before further input.");
        }
    }

    public static string? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(Target, GenericCredential, 0, out IntPtr address))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new InvalidOperationException(
                "Windows Credential Manager failed to read the dedicated GitHub Issues token ("
                + error + ").");
        }

        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(address);
            if (credential.Type != GenericCredential
                || credential.CredentialBlob == IntPtr.Zero
                || credential.CredentialBlobSize is < 20 or > 2048
                || credential.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("Stored GitHub Issues credential has invalid metadata.");
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

    private static void Store(SecureString secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Credential Manager requires Windows.");
        if (secret.Length is < 40 or > 512)
            throw new InvalidOperationException("GitHub fine-grained token length is invalid.");

        // Marshal into temporary unmanaged memory, immediately zero it after
        // CredWrite; never create a managed token string during enrollment.
        IntPtr address = Marshal.SecureStringToCoTaskMemUnicode(secret);
        try
        {
            // Fine-grained PATs use the github_pat_ prefix. Refuse personal
            // classic tokens so the agent cannot silently reuse gh auth.
            const string prefix = "github_pat_";
            for (int i = 0; i < prefix.Length; i++)
                if (Marshal.ReadInt16(address, i * 2) != prefix[i])
                    throw new InvalidOperationException(
                        "Expected a dedicated GitHub fine-grained personal access token.");
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = Target,
                Comment = "HaMi Agent only: repository-scoped GitHub Issues token",
                CredentialBlobSize = checked((uint)(secret.Length * sizeof(char))),
                CredentialBlob = address,
                Persist = LocalMachinePersistence,
                UserName = "Snahdul"
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException(
                    "Windows Credential Manager could not store the dedicated Issues token ("
                    + Marshal.GetLastWin32Error() + ").");
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(address);
        }
    }

    public static int StoreInteractive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The dedicated credential store is available only on Windows.");
            return 2;
        }
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Token enrollment requires an interactive console.");
            return 2;
        }

        Console.Write("Paste the dedicated github_pat_ token (input hidden), then Enter: ");
        using var secret = new SecureString();
        try
        {
            ReadPastedToken(secret);
            Console.WriteLine();
            Store(secret);
            Console.WriteLine("Dedicated GitHub Issues token stored in Windows Credential Manager.");
            Console.WriteLine("Token value was not printed or stored in process/user environment variables.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception
            or ExternalException)
        {
            Console.Error.WriteLine("Credential enrollment failed: " + ex.GetType().Name);
            return 2;
        }
    }
}
