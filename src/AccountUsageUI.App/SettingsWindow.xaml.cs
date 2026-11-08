using System.Windows;
using System.Windows.Input;
using AccountUsageUI.App.Presentation;

namespace AccountUsageUI.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Func<IReadOnlyList<string>, Task> _onSaveAsync;

    public SettingsWindow(
        SettingsViewModel viewModel,
        Func<IReadOnlyList<string>, Task> onSaveAsync)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(onSaveAsync);
        _viewModel = viewModel;
        _onSaveAsync = onSaveAsync;
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnAddCustomModelClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.AddCustomModel();
        StatusTextBlock.Text = "사용자 지정 모델을 추가하고 선택했습니다.";
    }

    private void OnCustomModelInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.AddCustomModel();
            StatusTextBlock.Text = "사용자 지정 모델을 추가하고 선택했습니다.";
            e.Handled = true;
        }
    }

    private void OnResetToDefaultsClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetToDefaults();
        StatusTextBlock.Text = "기본 주요 모델 선택으로 초기화했습니다.";
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.GetSelectedModels();
        if (selected.Count == 0)
        {
            StatusTextBlock.Text = "최소 1개 이상의 주요 모델을 선택해 주세요.";
            return;
        }

        await _onSaveAsync(selected).ConfigureAwait(true);
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
