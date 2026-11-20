using System.IO;
using System.Text.Json;

namespace WindowsAppUpdates;

public sealed record UpdateEntry(DateTimeOffset Time, string Name, string Id, string Installed,
    string Available, string Result, string Details)
{
    public string TimeText => Time.ToLocalTime().ToString("MM-dd HH:mm");
}

public sealed class HistoryStore(string? directory = null)
{
    private const int Limit = 100;
    private readonly string file = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsAppUpdates"), "history.json");

    public IReadOnlyList<UpdateEntry> Read()
    {
        if (!File.Exists(file))
            return [];
        if (new FileInfo(file).Length > 8 * 1024 * 1024)
            throw new IOException("更新记录文件过大。");
        return (JsonSerializer.Deserialize<UpdateEntry[]>(File.ReadAllBytes(file)) ?? []).Take(Limit).ToArray();
    }

    public void Save(IEnumerable<UpdateEntry> history)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string draft = file + ".tmp";
        File.WriteAllBytes(draft, JsonSerializer.SerializeToUtf8Bytes(history.Take(Limit)));
        File.Move(draft, file, true);
    }
}
