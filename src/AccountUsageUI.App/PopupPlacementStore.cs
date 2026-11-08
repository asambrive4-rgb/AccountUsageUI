using System.IO;
using System.Text.Json;

namespace AccountUsageUI.App;

public sealed class PopupPlacementStore
{
    private readonly string _settingsFilePath;

    public PopupPlacementStore(string? rootDirectoryPath = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectoryPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccountUsageUI")
            : rootDirectoryPath;
        _settingsFilePath = Path.Combine(root, "ui-settings.json");
    }

    public bool TryLoadPopupPosition(out double left, out double top)
    {
        left = 0;
        top = 0;

        if (!File.Exists(_settingsFilePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            var doc = JsonSerializer.Deserialize<PopupSettingsDocument>(json);
            if (doc?.PopupLeft is null || doc.PopupTop is null)
            {
                return false;
            }

            if (!double.IsFinite(doc.PopupLeft.Value) || !double.IsFinite(doc.PopupTop.Value))
            {
                return false;
            }

            left = doc.PopupLeft.Value;
            top = doc.PopupTop.Value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SavePopupPosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var doc = new PopupSettingsDocument
            {
                PopupLeft = Math.Round(left, 1),
                PopupTop = Math.Round(top, 1)
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Ignore non-critical UI placement write failures.
        }
    }

    private sealed class PopupSettingsDocument
    {
        public double? PopupLeft { get; set; }

        public double? PopupTop { get; set; }
    }
}
