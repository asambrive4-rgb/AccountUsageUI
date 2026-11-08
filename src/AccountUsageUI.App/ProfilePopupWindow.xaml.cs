using System.Windows;
using System.Windows.Controls;
using AccountUsageUI.App.Presentation;

namespace AccountUsageUI.App;

public partial class ProfilePopupWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Func<ProfileListItemViewModel, Task> _onSwitchProfileAsync;
    private readonly Action _onReturnToMain;

    public ProfilePopupWindow(
        MainWindowViewModel viewModel,
        Func<ProfileListItemViewModel, Task> onSwitchProfileAsync,
        Action onReturnToMain)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(onSwitchProfileAsync);
        ArgumentNullException.ThrowIfNull(onReturnToMain);

        _viewModel = viewModel;
        _onSwitchProfileAsync = onSwitchProfileAsync;
        _onReturnToMain = onReturnToMain;

        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnReturnToMainClicked(object sender, RoutedEventArgs e)
    {
        _onReturnToMain();
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private async void OnQuickSwitchClicked(object sender, RoutedEventArgs e)
    {
        var candidates = _viewModel.GetOverlaySwitchCandidates();
        if (candidates.Count == 0)
        {
            return;
        }

        if (candidates.Count == 1)
        {
            await _onSwitchProfileAsync(candidates[0]).ConfigureAwait(true);
            return;
        }

        var menu = new ContextMenu();
        foreach (var candidate in candidates)
        {
            var header = candidate.HasAccountSubtext
                ? $"{candidate.Name} ({candidate.AccountSubtext})"
                : candidate.Name;
            var item = new MenuItem
            {
                Header = header,
                FontWeight = FontWeights.SemiBold
            };
            item.Click += async (_, _) => await _onSwitchProfileAsync(candidate).ConfigureAwait(true);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = QuickSwitchButton;
        menu.IsOpen = true;
    }
}
