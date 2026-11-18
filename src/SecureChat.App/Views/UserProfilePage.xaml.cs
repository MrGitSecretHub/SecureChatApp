using SecureChat.App.Pages;
using SecureChat.App.Services;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Views;

/// <summary>
/// Публичный профиль участника: аватар, имя, статус, «о себе», отпечаток
/// ключа и кнопка «Начать чат». Открывается по нажатию на @упоминание
/// в сообщениях пространств и комментариях.
/// </summary>
public partial class UserProfilePage : ContentPage
{
    private readonly ApiClient _api = AppServices.Get<ApiClient>();
    private readonly string _username;
    private UserProfileDto? _profile;

    public UserProfilePage(string username)
    {
        InitializeComponent();
        _username = username.Trim();
        Title = "Профиль";
        UsernameLabel.Text = "@" + _username;
        InitialLabel.Text = _username.Length == 0 ? "?" : _username[..1].ToUpperInvariant();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StartChatButton.IsEnabled = false;

        try
        {
            var session = AppServices.Get<AppSession>();
            if (string.IsNullOrWhiteSpace(session.Token))
            {
                ErrorLabel.Text = Loc.T("uc_052", "Нет подключения к серверу — данные профиля недоступны.");
                ErrorLabel.IsVisible = true;
                StartChatButton.IsEnabled = true;
                return;
            }

            var profile = await _api.GetUserAsync(session.ServerUrl, session.Token, _username);
            ApplyProfile(profile);
        }
        catch (Exception ex)
        {
            // Профиль не загрузился (нет сети и т.п.) — чат всё равно доступен.
            ErrorLabel.Text = string.Format(Loc.T("uc_028"), ex.Message);
            ErrorLabel.IsVisible = true;
            StatusLabel.Text = Loc.T("uc_050", "Неизвестно");
            StatusDot.IsVisible = false;
        }
        finally
        {
            StartChatButton.IsEnabled = true;
        }
    }

    private void ApplyProfile(UserProfileDto profile)
    {
        _profile = profile;

        var displayName = !string.IsNullOrWhiteSpace(profile.Nickname)
            ? profile.Nickname!
            : profile.Username;
        NicknameLabel.Text = displayName;
        UsernameLabel.Text = "@" + profile.Username;
        InitialLabel.Text = displayName[..1].ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(profile.StatusMessage))
        {
            StatusLabel.Text = profile.StatusMessage!;
            StatusDot.IsVisible = true;
            StatusDot.BackgroundColor = ParseColor(profile.OnlineStatusColor, "#3BA55D");
            if (profile.LastSeenUnixMs > 0)
            {
                StatusLabel.Text += $" · был в сети {FormatLastSeen(profile.LastSeenUnixMs)}";
            }
        }
        else
        {
            StatusLabel.Text = "Онлайн";
            StatusDot.IsVisible = true;
            StatusDot.BackgroundColor = ParseColor(profile.OnlineStatusColor, "#3BA55D");
        }

        if (!string.IsNullOrWhiteSpace(profile.Bio))
        {
            BioSection.IsVisible = true;
            BioLabel.Text = profile.Bio!;
        }

        if (!string.IsNullOrWhiteSpace(profile.Fingerprint))
        {
            FingerprintCard.IsVisible = true;
            FingerprintLabel.Text = FormatFingerprint(profile.Fingerprint!);
        }

        if (!string.IsNullOrWhiteSpace(profile.AvatarBase64))
        {
            try
            {
                var bytes = Convert.FromBase64String(profile.AvatarBase64!);
                AvatarImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            }
            catch (FormatException)
            {
                // Битый аватар — остаётся заглушка с буквой.
            }
        }
    }

    /// <summary>
    /// Начать чат: профиль подтвердили — открываем личный чат.
    /// </summary>
    private async void OnStartChatClicked(object sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new ChatPage(_username));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private static Color ParseColor(string? hex, string fallback)
    {
        try
        {
            return Color.FromArgb(string.IsNullOrWhiteSpace(hex) ? fallback : hex!);
        }
        catch
        {
            return Color.FromArgb(fallback);
        }
    }

    /// <summary>Отпечаток в группы по 8 символов для удобной сверки.</summary>
    private static string FormatFingerprint(string fingerprint)
    {
        var clean = fingerprint.Replace(":", string.Empty).Replace(" ", string.Empty);
        return string.Join(" ",
            Enumerable.Range(0, (int)Math.Ceiling(clean.Length / 8.0))
                .Select(i => string.Concat(clean.Skip(i * 8).Take(8))));
    }

    private static string FormatLastSeen(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            .ToLocalTime()
            .ToString("dd.MM HH:mm");
}
