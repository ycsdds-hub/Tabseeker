using System.IO;
using System.Text.Json;

namespace Tabseeker;

public sealed class AppSettings
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tabseeker");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public List<string> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public List<string> Favorites { get; set; } = new();
    public bool ShowHidden { get; set; }
    /// <summary>Pop the memo bubble also over names without a memo (so one can be written).</summary>
    public bool MemoHoverForEmpty { get; set; } = true;
    public bool TileView { get; set; }
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
    public double NavWidth { get; set; } = 210;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
