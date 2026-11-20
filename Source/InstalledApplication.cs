using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WindowsAppUpdates;

internal sealed partial record InstalledApplication(string Directory, string? Executable, string? Version)
{
    public static InstalledApplication? FromDetails(PackageUpdate package, string details)
    {
        var lines = details.Split('\n');
        if (lines.Count(line => line.EndsWith($"[{package.Id}]", StringComparison.OrdinalIgnoreCase)) != 1)
            return null;

        string? location = ReadField(lines, "Installed Location", "安装位置", "安裝位置");
        string? identifier = ReadField(lines, "Local Identifier", "本地标识符", "本機識別碼");
        var registration = ReadRegistration(identifier);
        string? icon = LocalPath(IconIndexPattern().Replace(registration.Icon ?? "", ""));
        var uninstallMatch = UninstallerPattern().Match(registration.Uninstall ?? "");
        string? uninstaller = LocalPath(uninstallMatch.Groups["path"].Value);

        string? directory = LocalPath(location ?? registration.Location);
        bool inferred = false;
        if (directory is null && location is null && registration.Location is null &&
            File.Exists(icon) && File.Exists(uninstaller) &&
            !string.Equals(icon, uninstaller, StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(icon) is string iconDirectory &&
            string.Equals(iconDirectory, Path.GetDirectoryName(uninstaller), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(uninstaller) is string uninstallName && UninstallNamePattern().IsMatch(uninstallName))
        {
            directory = iconDirectory;
            inferred = true;
        }

        if (directory is null || !System.IO.Directory.Exists(directory) || directory == Path.GetPathRoot(directory) ||
            string.Equals(directory, Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return null;

        string? executable = IsApplicationExecutable(icon, directory, uninstaller) ? icon : null;
        if (executable is null && package.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            string namedExecutable = Path.Combine(directory, package.Name + ".exe");
            if (IsApplicationExecutable(namedExecutable, directory, uninstaller))
                executable = namedExecutable;
        }

        return inferred && executable is null ? null :
            new InstalledApplication(directory, executable, ReadField(lines, "Version", "版本"));
    }

    internal static string? LocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!DrivePathPattern().IsMatch(path) || path.AsSpan(2).Contains(':') ||
            path.IndexOfAny(['"', '<', '>', '?', '*', '|']) >= 0 || path.Any(char.IsControl))
            return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
    }

    private static bool IsApplicationExecutable(string? candidate, string directory, string? uninstaller) =>
        candidate is not null && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        candidate.StartsWith(directory + '\\', StringComparison.OrdinalIgnoreCase) &&
        !UninstallNamePattern().IsMatch(Path.GetFileName(candidate)) &&
        !string.Equals(candidate, uninstaller, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate);

    internal static string? ReadField(string[] lines, params string[] labels)
    {
        foreach (var line in lines)
        {
            int separator = line.IndexOfAny([':', '：']);
            if (separator > 0 && labels.Contains(line[..separator].Trim(), StringComparer.OrdinalIgnoreCase))
                return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private static (string? Location, string? Icon, string? Uninstall) ReadRegistration(string? identifier)
    {
        var match = RegistryIdentifierPattern().Match(identifier ?? "");
        if (!match.Success)
            return default;

        RegistryHive hive = match.Groups["scope"].Value == "Machine" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        RegistryView view = match.Groups["arch"].Value == "X86" ? RegistryView.Registry32 : RegistryView.Registry64;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var registration = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + match.Groups["key"].Value);
            return (registration?.GetValue("InstallLocation") as string,
                registration?.GetValue("DisplayIcon") as string,
                registration?.GetValue("UninstallString") as string);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return default;
        }
    }

    [GeneratedRegex(@"^ARP\\(?<scope>Machine|User)\\(?<arch>X86|X64|Arm64)\\(?<key>[^\\]+)$")]
    private static partial Regex RegistryIdentifierPattern();

    [GeneratedRegex(@",\s*-?\d+\s*$")]
    private static partial Regex IconIndexPattern();

    [GeneratedRegex("^\\s*\"?(?<path>[^\"]+?\\.exe)(?:\"|\\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallerPattern();

    [GeneratedRegex("unins|卸载|解除安裝", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallNamePattern();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DrivePathPattern();
}
