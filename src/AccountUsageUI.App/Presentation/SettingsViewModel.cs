using System.Collections.ObjectModel;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.App.Presentation;

public sealed class FavoriteModelChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    public FavoriteModelChoiceViewModel(string modelName, bool isSelected)
    {
        ModelName = modelName;
        _isSelected = isSelected;
    }

    public string ModelName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private string _customModelInput = "";

    public SettingsViewModel(
        IReadOnlyList<string> catalogModels,
        IReadOnlyList<string> selectedFavorites)
    {
        var selectedSet = new HashSet<string>(
            selectedFavorites,
            StringComparer.OrdinalIgnoreCase);

        var ordered = new List<string>();
        foreach (var fav in selectedFavorites)
        {
            if (!string.IsNullOrWhiteSpace(fav) &&
                !ordered.Contains(fav, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(fav.Trim());
            }
        }

        foreach (var cat in catalogModels)
        {
            if (!string.IsNullOrWhiteSpace(cat) &&
                !ordered.Contains(cat, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(cat.Trim());
            }
        }

        foreach (var item in ordered)
        {
            Models.Add(
                new FavoriteModelChoiceViewModel(
                    item,
                    selectedSet.Contains(item)));
        }
    }

    public ObservableCollection<FavoriteModelChoiceViewModel> Models { get; } = [];

    public string CustomModelInput
    {
        get => _customModelInput;
        set => SetField(ref _customModelInput, value);
    }

    public void AddCustomModel()
    {
        var trimmed = CustomModelInput?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        var existing = Models.FirstOrDefault(
            m => StringComparer.OrdinalIgnoreCase.Equals(m.ModelName, trimmed));
        if (existing is not null)
        {
            existing.IsSelected = true;
            CustomModelInput = "";
            return;
        }

        Models.Insert(
            0,
            new FavoriteModelChoiceViewModel(trimmed, isSelected: true));
        CustomModelInput = "";
    }

    public void ResetToDefaults()
    {
        var defaultSet = new HashSet<string>(
            FavoriteModelsPolicy.DefaultFavoriteModels,
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in Models)
        {
            item.IsSelected = defaultSet.Contains(item.ModelName);
        }
    }

    public IReadOnlyList<string> GetSelectedModels()
    {
        return Models
            .Where(m => m.IsSelected)
            .Select(m => m.ModelName)
            .ToArray();
    }
}
