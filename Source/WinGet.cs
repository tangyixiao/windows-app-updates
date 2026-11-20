using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsAppUpdates;

public sealed record PackageUpdate(string Name, string Id, string Installed, string Available);

public sealed record CommandResult(int ExitCode, string Output)
{
    public string ExitCodeText => $"0x{ExitCode:X8}";
}

public interface IUpdateProvider
{
    Task<ScanResult> ScanAsync(CancellationToken cancellationToken);
    Task<CommandResult> UpgradeAsync(PackageUpdate package);
}

public sealed record ScanResult(IReadOnlyList<PackageUpdate> Packages, string Output, int Omitted = 0);

public sealed partial class WinGet : IUpdateProvider
{
    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var result = await RunAsync(["list", "--upgrade-available"], timeout.Token);
            if (result.ExitCode != 0 && result.ExitCode != unchecked((int)0x8A150014))
                throw new InvalidOperationException($"WinGet 检查失败（{result.ExitCodeText}）。\n{result.Output}");

            return ParseScan(result.Output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("WinGet 检查超过两分钟。请检查网络后重试。");
        }
    }

    public Task<CommandResult> UpgradeAsync(PackageUpdate package) =>
        RunAsync(UpgradeArguments(package), CancellationToken.None);

    public static string[] UpgradeArguments(PackageUpdate package)
    {
        if (!PackageIdPattern().IsMatch(package.Id) || !IsVersion(package.Available))
            throw new ArgumentException("WinGet 包 ID 或目标版本无效。");

        return ["upgrade", "--id", package.Id, "--exact", "--version", package.Available];
    }

    private static async Task<CommandResult> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        var start = new ProcessStartInfo(winget)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (string argument in new[] { "--source", "winget", "--disable-interactivity" })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("无法启动 WinGet。请检查 Windows 的应用安装程序（App Installer）和 winget.exe 执行别名。", ex);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
            throw;
        }

        string output = CleanOutput(string.Concat(await stdout, "\n", await stderr));
        return new CommandResult(process.ExitCode, output);
    }

    public static ScanResult ParseScan(string output)
    {
        string[] lines = output.Split('\n');
        var packages = new List<PackageUpdate>();
        int omitted = 0;
        bool table = false;

        for (int index = 1; index < lines.Length; index++)
        {
            if (lines[index].Length < 10 || !lines[index].All(character => character == '-'))
                continue;

            string header = lines[index - 1];
            var match = HeaderPattern().Match(header);
            if (!match.Success)
                throw new InvalidOperationException($"无法识别 WinGet 的更新表格。\n{output}");

            table = true;
            var columns = new[] { "id", "installed", "available" }
                .Select(field => DisplayWidth(header[..match.Groups[field].Index])).ToArray();
            bool requiresExplicitUpgrade = index >= 2 && lines.Take(index - 1).Reverse()
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) is string heading &&
                (heading.Contains("explicit targeting", StringComparison.OrdinalIgnoreCase) ||
                 heading.Contains("显式目标") || heading.Contains("明確顯示目標"));

            while (index + 1 < lines.Length && DisplayWidth(lines[index + 1]) > columns[2])
            {
                if (lines[index + 1].EndsWith('.') && !lines[index + 1].Contains("  "))
                    break;
                string row = lines[++index];
                if (requiresExplicitUpgrade || !TryReadRow(row, columns, out var package))
                    omitted++;
                else
                    packages.Add(package!);
            }
        }

        if (!table && !IsEmptyResult(lines))
            throw new InvalidOperationException($"WinGet 未返回可识别的检查结果。\n{output}");

        var unique = packages.GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.DistinctBy(package => (package.Installed, package.Available)).Count() == 1)
            .Select(group => group.First()).OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var accepted = unique.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        omitted += packages.Count(package => !accepted.Contains(package.Id));
        return new ScanResult(unique, output, omitted);
    }

    private static bool TryReadRow(string row, int[] columns, out PackageUpdate? package)
    {
        package = null;
        var offsets = new int[4];
        int column = 0;
        int field = 0;
        var elements = StringInfo.GetTextElementEnumerator(row);
        while (field < columns.Length && elements.MoveNext())
        {
            int offset = elements.ElementIndex;
            if (column == columns[field])
            {
                if (offset == 0 || row[offset - 1] != ' ' || char.IsWhiteSpace(row[offset]))
                    return false;
                offsets[++field] = offset;
            }
            column += CharacterWidth(Rune.GetRuneAt(row, offset));
        }
        if (field != columns.Length)
            return false;

        string name = row[..offsets[1]].Trim();
        string id = row[offsets[1]..offsets[2]].Trim();
        string installed = row[offsets[2]..offsets[3]].Trim();
        string available = row[offsets[3]..].Trim();
        if (!PackageIdPattern().IsMatch(id) || installed.Length == 0 || !IsVersion(available))
            return false;

        package = new PackageUpdate(name, id, installed, available);
        return true;
    }

    private static bool IsVersion(string value) => value.Length is > 0 and <= 128 &&
        !value.StartsWith('-') && value.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|', '…', '\uFFFD']) < 0 &&
        !value.Any(char.IsControl);

    private static bool IsEmptyResult(string[] lines) => lines.Any(line =>
        line.StartsWith("No applicable upgrade", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("No installed package found", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("找不到适用的升级") || line.Contains("找不到可用的升级") ||
        line.Contains("找不到与输入条件匹配的已安装程序包") || line.Contains("未找到符合輸入條件的已安裝套件") ||
        line.Contains("找不到適用的升級") || line.Contains("找不到可用的升級") ||
        line.Contains("--include-unknown") || line.Contains("--include-pinned"));

    private static int DisplayWidth(string value)
    {
        int width = 0;
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
            width += CharacterWidth(Rune.GetRuneAt(value, elements.ElementIndex));
        return width;
    }

    private static int CharacterWidth(Rune character) => character.Value is
        >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or
        >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE19 or >= 0xFE30 and <= 0xFE6F or
        >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or
        >= 0x20000 and <= 0x3FFFD ? 2 : 1;

    private static string CleanOutput(string output) => string.Join('\n', AnsiPattern().Replace(output, "")
        .Replace("\r\n", "\n").Split('\n', '\r')
        .Select(line => line.TrimEnd())
        .Where(line => line.Trim() is not ("-" or "\\" or "|" or "/"))).Trim();

    [GeneratedRegex(@"^(?:Name|名称|名稱) +(?<id>Id|ID|識別碼) +(?<installed>Version|版本) +(?<available>Available|可用) *$")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9+_-]*(?:\.[A-Za-z0-9+_-]+){1,7}\z")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiPattern();
}
