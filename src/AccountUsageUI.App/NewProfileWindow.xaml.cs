using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AccountUsageUI.Core.Profiles;

namespace AccountUsageUI.App;

public partial class NewProfileWindow : Window
{
    private readonly CreateProfileLoginUseCase _createProfileLogin;
    private bool _loginStarted;
    private bool _isBusy;
    private bool _isCompleted;

    public NewProfileWindow(CreateProfileLoginUseCase createProfileLogin)
    {
        ArgumentNullException.ThrowIfNull(createProfileLogin);
        _createProfileLogin = createProfileLogin;
        InitializeComponent();
        Loaded += (_, _) => ProfileNameTextBox.Focus();
    }

    public CreateProfileLoginResult? Result { get; private set; }

    private async void OnCaptureCurrentClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var profileName = ProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            StatusTextBlock.Text = "프로필 이름을 먼저 입력해 주세요.";
            ProfileNameTextBox.Focus();
            return;
        }

        SetBusy(true, "현재 로그인된 Antigravity 계정을 캡처하는 중...");
        try
        {
            var result = await _createProfileLogin.CaptureCurrentAsync(
                profileName,
                CancellationToken.None).ConfigureAwait(true);
            Result = result;
            if (result.Status == CreateProfileLoginStatus.Created)
            {
                _isCompleted = true;
                DialogResult = true;
                Close();
                return;
            }

            StatusTextBlock.Text = DescribeCreateLogin(result.Status);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStartNewLoginClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var profileName = ProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            StatusTextBlock.Text = "프로필 이름을 먼저 입력해 주세요.";
            ProfileNameTextBox.Focus();
            return;
        }

        SetBusy(true, "Antigravity 새 계정 로그인 세션을 준비하는 중...");
        try
        {
            var result = await _createProfileLogin.StartAsync(
                profileName,
                forceCloseApproved: true,
                CancellationToken.None).ConfigureAwait(true);
            Result = result;
            if (result.Status == CreateProfileLoginStatus.WaitingForLogin)
            {
                _loginStarted = true;
                ProfileNameTextBox.IsEnabled = false;
                InitialSelectionPanel.Visibility = Visibility.Collapsed;
                AwaitingLoginPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = DescribeCreateLogin(result.Status);
                return;
            }

            StatusTextBlock.Text = DescribeCreateLogin(result.Status);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCompleteNewLoginClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !_loginStarted)
        {
            return;
        }

        SetBusy(true, "로그인 완료 상태를 확인하고 프로필을 저장하는 중...");
        try
        {
            var result = await _createProfileLogin.CompleteAsync(
                forceCloseApproved: true,
                CancellationToken.None).ConfigureAwait(true);
            Result = result;
            if (result.Status == CreateProfileLoginStatus.Created)
            {
                _isCompleted = true;
                DialogResult = true;
                Close();
                return;
            }

            StatusTextBlock.Text = DescribeCreateLogin(result.Status);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_loginStarted && !_isCompleted)
        {
            SetBusy(true, "로그인 세션을 취소하고 이전 인증 상태를 복구하는 중...");
            try
            {
                Result = await _createProfileLogin.CancelAsync(
                    forceCloseApproved: true,
                    CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                _loginStarted = false;
                SetBusy(false);
            }
        }

        DialogResult = false;
        Close();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_loginStarted && !_isCompleted && !_isBusy)
        {
            e.Cancel = true;
            SetBusy(true, "이전 인증 상태를 복구하는 중...");
            try
            {
                Result = await _createProfileLogin.CancelAsync(
                    forceCloseApproved: true,
                    CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                _loginStarted = false;
                SetBusy(false);
            }

            Close();
        }
    }

    private void OnProfileNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_loginStarted && !_isBusy)
        {
            OnCaptureCurrentClicked(sender, e);
            e.Handled = true;
        }
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        CaptureCurrentButton.IsEnabled = !isBusy;
        StartNewLoginButton.IsEnabled = !isBusy;
        CompleteNewLoginButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !isBusy;

        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusTextBlock.Text = message;
        }
    }

    public static string DescribeCreateLogin(CreateProfileLoginStatus status) =>
        status switch
        {
            CreateProfileLoginStatus.WaitingForLogin =>
                "Antigravity에서 새 Google 계정 로그인을 완료한 뒤 '로그인 완료 및 프로필 저장'을 눌러 주세요.",
            CreateProfileLoginStatus.Created =>
                "새 프로필을 성공적으로 등록했습니다.",
            CreateProfileLoginStatus.Canceled =>
                "프로필 등록을 취소하고 이전 인증 상태를 복구했습니다.",
            CreateProfileLoginStatus.InvalidName =>
                "유효하지 않은 프로필 이름입니다. (1~64자)",
            CreateProfileLoginStatus.DuplicateName =>
                "이미 같은 이름의 프로필이 존재합니다.",
            CreateProfileLoginStatus.LoginNotCompleted =>
                "로그인된 Antigravity 계정 정보를 찾지 못했습니다. 먼저 Antigravity에 로그인되어 있는지 확인해 주세요.",
            CreateProfileLoginStatus.InstallationNotFound =>
                "Antigravity 실행 파일을 찾지 못했습니다.",
            CreateProfileLoginStatus.RecoveryRequired =>
                "이전 인증 복구가 필요합니다.",
            _ => "프로필 등록 중 오류가 발생했습니다."
        };
}
