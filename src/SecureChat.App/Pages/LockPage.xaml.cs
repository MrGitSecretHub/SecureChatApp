using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>Экран блокировки: открывается при запуске, если установлен код-пароль.</summary>
public partial class LockPage : ContentPage
{
    private readonly AccountService _account = AppServices.Get<AccountService>();

    public LockPage()
    {
        InitializeComponent();
    }

    private async void OnUnlockClicked(object sender, EventArgs e)
    {
        var secrets = AppServices.Get<SecretStore>();
        if (!await PrivacyStore.VerifyPasscodeAsync(secrets, PasscodeEntry.Text ?? string.Empty))
        {
            ErrorLabel.IsVisible = true;
            PasscodeEntry.Text = string.Empty;
            return;
        }

        ErrorLabel.IsVisible = false;
        App.SetRoot(App.CreateMainPage());
    }

    private async void OnLogoutClicked(object sender, EventArgs e)
    {
        await _account.LogoutAsync();
        App.SetRoot(new NavigationPage(AppServices.Get<LoginPage>()));
    }
}
