using System.Diagnostics;

namespace HaMi.Agentic.Cli.Features.Development;

/// <summary>Host-side sandbox tooling receives only explicitly approved OS settings.
/// Values are never printed. No general-purpose environment or credential forwarding.</summary>
internal static class DevelopmentChildEnvironment
{
    private static readonly string[] WindowsNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive",
        "ComSpec", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN"
    ];

    private static readonly string[] UnixNames =
    [
        "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "LANG", "LC_ALL",
        "LC_CTYPE", "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
        "XDG_CACHE_HOME", "XDG_STATE_HOME"
    ];

    internal static void RestrictSandboxProcess(ProcessStartInfo start)
    {
        // Copy only named OS settings before clearing the inherited dictionary.
        // Preserve an explicitly absent value as absent.
        var names = OperatingSystem.IsWindows() ? WindowsNames : UnixNames;
        var allowed = names.Where(start.Environment.ContainsKey)
            .Select(name => (Name: name, Value: start.Environment[name])).ToArray();
        start.Environment.Clear();
        foreach (var item in allowed)
            start.Environment[item.Name] = item.Value;
    }
}
