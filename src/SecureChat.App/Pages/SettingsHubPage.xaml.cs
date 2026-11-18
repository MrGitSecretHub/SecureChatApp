namespace SecureChat.App.Pages;

/// <summary>
/// Главное меню настроек: категории открываются как отдельные страницы.
/// Каждая категория — тот же SettingsPage с соответствующим разделом.
/// </summary>
public partial class SettingsHubPage : ContentPage
{
    public SettingsHubPage()
    {
        InitializeComponent();
    }

    private Task OpenAsync(string section) =>
        Navigation.PushAsync(new SettingsPage(section));

    private void OnProfile(object? sender, EventArgs e) => _ = OpenAsync("profile");
    private void OnPrivacy(object? sender, EventArgs e) => _ = OpenAsync("privacy");
    private void OnAppearance(object? sender, EventArgs e) => _ = OpenAsync("appearance");
    private void OnWallpaper(object? sender, EventArgs e) => _ = OpenAsync("wallpaper");
    private void OnServer(object? sender, EventArgs e) => _ = OpenAsync("server");
    private void OnMedia(object? sender, EventArgs e) => _ = OpenAsync("media");
    private void OnAccount(object? sender, EventArgs e) => _ = OpenAsync("account");
}
