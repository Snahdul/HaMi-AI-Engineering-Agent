using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using HaMi.Agentic.Cli.Runtime;

namespace HaMi.Agentic.Cli.Features.Development;

// Fixed OS-account location, deliberately unrelated to checkout or results
// directories. Only the trusted host may create/read claims here. This is
// not protection against a compromised host or same-account process.
internal static class DevelopmentJobStateRoot
{
    private const string WindowsName = "HaMiDeveloperJobState-v1";
    private const string LinuxName = ".hami-developer-job-state-v1";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead
        | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static DevelopmentJobStateStore Open(string repo, string resultsRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            string parent = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Resolve(repo, resultsRoot, parent, WindowsName);
            string driveRoot = Path.GetPathRoot(root)
                ?? throw new InvalidOperationException("Recovery drive missing.");
            var drive = new DriveInfo(driveRoot);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat is not ("NTFS" or "ReFS"))
                throw new InvalidOperationException("Recovery storage requires local NTFS or ReFS.");
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User
                ?? throw new InvalidOperationException("Recovery account SID missing.");
            ValidateWindowsParent(parent, user);
            EnsureWindowsRoot(root, user);
            return new DevelopmentJobStateStore(root,
                (path, directory) =>
                {
                    if (!OperatingSystem.IsWindows())
                        throw new PlatformNotSupportedException("Windows recovery storage requires Windows.");
                    ValidateWindowsEntry(path, directory, user);
                });
        }

        if (OperatingSystem.IsLinux())
        {
            uint uid = EffectiveLinuxUid();
            string home = LinuxHome(uid);
            string root = Resolve(repo, resultsRoot, home, LinuxName);
            ValidateLinuxParents(home, uid);
            if (LinuxStat(["-f", "--format=%T", "--", home]) is not ("ext2/ext3" or "xfs" or "btrfs"))
                throw new InvalidOperationException("Recovery storage requires a supported local Linux filesystem.");
            EnsureLinuxRoot(root, uid);
            if (LinuxStat(["-f", "--format=%T", "--", root]) is not ("ext2/ext3" or "xfs" or "btrfs"))
                throw new InvalidOperationException("Recovery root filesystem rejected.");
            return new DevelopmentJobStateStore(root,
                (path, directory) =>
                {
                    if (!OperatingSystem.IsLinux())
                        throw new PlatformNotSupportedException("Linux recovery storage requires Linux.");
                    ValidateLinuxEntry(path, directory, uid);
                });
        }

        throw new InvalidOperationException("Recovery storage platform not supported.");
    }

    private static string Resolve(string repo, string resultsRoot, string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathFullyQualified(parent)
            || !Directory.Exists(parent))
            throw new InvalidOperationException("Recovery account directory unavailable.");
        string root = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), name);
        // Reuse the existing symmetric overlap and reparse-point gate; the
        // root is checked against all writable job/worktree locations.
        DevelopmentIsolatedRunner.ValidateStoragePaths(repo, resultsRoot, root);
        DevelopmentIsolatedRunner.ValidateStoragePaths(repo, RuntimeSettings.GetWorktreeRoot(repo), root);
        return root;
    }

    private static void RejectLink(string path, bool directory)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Recovery storage link rejected.");
    }

    [SupportedOSPlatform("windows")]
    private static bool TrustedWindowsSid(SecurityIdentifier sid, SecurityIdentifier user) =>
        sid.Equals(user) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsParent(string parent, SecurityIdentifier user)
    {
        RejectLink(parent, true);
        DirectorySecurity security = new DirectoryInfo(parent).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !TrustedWindowsSid(owner, user)
            || !security.AreAccessRulesCanonical
            || new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
            throw new InvalidOperationException("Recovery account directory owner rejected.");
        const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && (rule.FileSystemRights & writes) != 0
                && !TrustedWindowsSid((SecurityIdentifier)rule.IdentityReference, user))
                throw new InvalidOperationException("Recovery account directory allows unrelated writers.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsRoot(string root, SecurityIdentifier user)
    {
        if (!Directory.Exists(root))
        {
            if (new DirectoryInfo(root).LinkTarget is not null || File.Exists(root))
                throw new InvalidOperationException("Recovery root path already occupied.");
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (SecurityIdentifier sid in new[]
            {
                user,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        ValidateWindowsEntry(root, true, user);
    }

    // A default Windows token owner can be the Administrators group, not
    // WindowsIdentity.User. Assign the approved owner at creation, not by
    // repairing an existing claim. Use the platform ACL API.
    [SupportedOSPlatform("windows")]
    internal static FileStream CreateWindowsPrivateFile(string path, FileOptions options)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("Recovery account SID missing.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in new[]
        {
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, options, security);
        try
        {
            ValidateWindowsSecurity(stream.GetAccessControl(), false, user);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsEntry(string path, bool directory, SecurityIdentifier user)
    {
        RejectLink(path, directory);
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateWindowsSecurity(security, directory, user);
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateWindowsSecurity(FileSystemSecurity security, bool directory, SecurityIdentifier user)
    {
        string kind = directory ? "directory" : "file";
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
            throw new InvalidOperationException("Recovery Windows " + kind + " owner missing.");
        if (!owner.Equals(user))
        {
            // Report only the fixed classification, never account names or SIDs.
            string classification = owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                ? "administrators" : owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    ? "system" : "other";
            throw new InvalidOperationException("Recovery Windows " + kind + " owner rejected: " + classification + ".");
        }
        if (!security.AreAccessRulesCanonical)
            throw new InvalidOperationException("Recovery Windows " + kind + " ACL is not canonical.");
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
            throw new InvalidOperationException("Recovery Windows " + kind + " ACL missing.");
        if (directory && !security.AreAccessRulesProtected)
            throw new InvalidOperationException("Recovery Windows directory ACL is not protected.");
        bool userFullControl = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType != AccessControlType.Allow || !TrustedWindowsSid(sid, user))
                throw new InvalidOperationException("Recovery ACL is not private.");
            if (sid.Equals(user) && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl
                && (!directory || (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)))
                userFullControl = true;
        }
        if (!userFullControl)
            throw new InvalidOperationException("Recovery account requires explicit full control.");
    }

    private static uint EffectiveLinuxUid()
    {
        string line = File.ReadLines("/proc/self/status")
            .Single(x => x.StartsWith("Uid:", StringComparison.Ordinal));
        string[] fields = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 4 || !uint.TryParse(fields[1], NumberStyles.None,
            CultureInfo.InvariantCulture, out uint uid))
            throw new InvalidOperationException("Recovery effective UID unavailable.");
        return uid;
    }

    private static string LinuxHome(uint uid)
    {
        // Local accounts only: no HOME/XDG override, remote NSS lookup or
        // caller-selected root. Unknown/ambiguous account entries fail closed.
        string[][] matches = File.ReadLines("/etc/passwd").Select(x => x.Split(':'))
            .Where(x => x.Length == 7 && uint.TryParse(x[2], NumberStyles.None,
                CultureInfo.InvariantCulture, out uint value) && value == uid).ToArray();
        if (matches.Length != 1 || !Path.IsPathFullyQualified(matches[0][5]))
            throw new InvalidOperationException("Recovery local account home unavailable.");
        return matches[0][5];
    }

    private static string LinuxStat(string[] args)
    {
        // Fixed trusted host executable, read-only metadata, no shell,
        // no PATH lookup or credentials inherited from the parent process.
        var start = new ProcessStartInfo("/usr/bin/stat")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.Environment["LC_ALL"] = "C";
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Recovery filesystem metadata unavailable.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Recovery filesystem metadata timed out.");
        }
        if (!Task.WaitAll([output, error], TimeSpan.FromSeconds(2))
            || process.ExitCode != 0 || error.Result.Length != 0 || output.Result.Length > 128)
            throw new InvalidOperationException("Recovery filesystem metadata rejected.");
        return output.Result.Trim();
    }

    private static (uint Owner, uint Links, int Mode) LinuxMetadata(string path)
    {
        string[] fields = LinuxStat(["--format=%u:%h:%f", "--", path]).Split(':');
        if (fields.Length != 3
            || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint owner)
            || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint links)
            || !int.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mode))
            throw new InvalidOperationException("Recovery filesystem metadata invalid.");
        return (owner, links, mode);
    }

    private static void ValidateLinuxParents(string home, uint uid)
    {
        for (DirectoryInfo? current = new(home); current is not null; current = current.Parent)
        {
            var metadata = LinuxMetadata(current.FullName);
            if ((metadata.Mode & 0xF000) != 0x4000 || (metadata.Mode & 0x12) != 0
                || (metadata.Owner != 0 && metadata.Owner != uid))
                throw new InvalidOperationException("Recovery account path is not private.");
        }
        if (LinuxMetadata(home).Owner != uid)
            throw new InvalidOperationException("Recovery home owner rejected.");
    }

    [SupportedOSPlatform("linux")]
    private static void EnsureLinuxRoot(string root, uint uid)
    {
        if (!Directory.Exists(root))
        {
            if (new DirectoryInfo(root).LinkTarget is not null || File.Exists(root))
                throw new InvalidOperationException("Recovery root path already occupied.");
            Directory.CreateDirectory(root, PrivateDirectory);
        }
        ValidateLinuxEntry(root, true, uid);
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateLinuxEntry(string path, bool directory, uint uid)
    {
        RejectLink(path, directory);
        var metadata = LinuxMetadata(path);
        if (metadata.Owner != uid
            || (metadata.Mode & 0xF000) != (directory ? 0x4000 : 0x8000)
            || (!directory && metadata.Links != 1)
            || File.GetUnixFileMode(path) != (directory ? PrivateDirectory : PrivateFile))
            throw new InvalidOperationException("Recovery owner, mode or link count rejected.");
    }

    internal static void SelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "hami-private-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (OperatingSystem.IsWindows())
                TestWindows(root);
            else if (OperatingSystem.IsLinux())
                TestLinux(root);
            else
                throw new InvalidOperationException("Recovery root self-test platform unsupported.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TestWindows(string root)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("Test SID missing.");
        EnsureWindowsRoot(root, user);
        var store = new DevelopmentJobStateStore(root,
            (path, directory) => ValidateWindowsEntry(path, directory, user));
        using (store.Acquire(62)) { }
        ValidateWindowsEntry(Path.Combine(root, "issue-62.lock"), false, user);
        var info = new DirectoryInfo(root);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
        Reject(() => store.Acquire(62).Dispose());
    }

    [SupportedOSPlatform("linux")]
    private static void TestLinux(string root)
    {
        uint uid = EffectiveLinuxUid();
        EnsureLinuxRoot(root, uid);
        var store = new DevelopmentJobStateStore(root,
            (path, directory) => ValidateLinuxEntry(path, directory, uid));
        using (store.Acquire(62)) { }
        string file = Path.Combine(root, "issue-62.lock");
        ValidateLinuxEntry(file, false, uid);
        File.SetUnixFileMode(file, PrivateFile | UnixFileMode.GroupRead);
        Reject(() => store.Acquire(62).Dispose());
        File.SetUnixFileMode(file, PrivateFile);
        File.SetUnixFileMode(root, PrivateDirectory | UnixFileMode.OtherRead);
        Reject(() => store.Acquire(62).Dispose());
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Insecure recovery storage accepted.");
    }
}
