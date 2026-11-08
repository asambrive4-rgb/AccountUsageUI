using System.Windows;

namespace AccountUsageUI.App;

public partial class DeleteProfileConfirmationWindow : Window
{
    public DeleteProfileConfirmationWindow(string profileName)
    {
        InitializeComponent();
        ConfirmationText.Text = $"'{profileName}' 프로필을 정말 삭제하시겠습니까?";
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
