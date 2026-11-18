using System.Collections.ObjectModel;
using System.Globalization;
using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

/// <summary>Результат поиска одного пользователя на сервере.</summary>
public sealed class SearchUserVm
{
    public required string Username { get; init; }
    public required string Fingerprint { get; init; }
    public required string? AvatarBase64 { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public string Initial =>
        string.IsNullOrWhiteSpace(Username)
            ? "?"
            : Username[..1].ToUpperInvariant();

    public ImageSource? AvatarSource =>
        string.IsNullOrWhiteSpace(AvatarBase64)
            ? null
            : ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(AvatarBase64)));

    public bool HasAvatar => AvatarSource is not null;

    public string FingerprintText =>
        string.IsNullOrWhiteSpace(Fingerprint)
            ? "—"
            : FingerprintFormatted;

    public string FingerprintFormatted =>
        string.IsNullOrWhiteSpace(Fingerprint)
            ? "—"
            : string.Join(" ",
                Enumerable.Range(0, (Fingerprint.Length + 3) / 4)
                    .Select(i => Fingerprint.Substring(i * 4, Math.Min(4, Fingerprint.Length - i * 4))));

    public bool HasFingerprint => !string.IsNullOrWhiteSpace(Fingerprint);

    public string CreatedAtText =>
        CreatedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public bool HasCreatedAt => CreatedAt != default;
}

public partial class SearchUsersPage : ContentPage
{
    private readonly ApiClient _api = AppServices.Get<ApiClient>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly ObservableCollection<SearchUserVm> _results = new();
    private bool _searching;

    public SearchUsersPage()
    {
        InitializeComponent();
        ResultsView.ItemsSource = _results;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UsernameEntry.Focus();
    }

    private async void OnSearchClicked(object sender, EventArgs e)
    {
        if (_searching)
        {
            return;
        }

        var username = (UsernameEntry.Text ?? string.Empty).Trim();
        if (username.Length < 1)
        {
            await DisplayAlert("Поиск", Loc.T("uc_005", "Введите имя пользователя."), "OK");
            return;
        }

        // Убираем ведущий @, если пользователь ввёл его.
        if (username.StartsWith("@", StringComparison.Ordinal))
        {
            username = username[1..];
        }

        _searching = true;
        SearchButton.IsEnabled = false;
        try
        {
            _results.Clear();

            var profile = await _api.GetUserAsync(
                _session.ServerUrl,
                _session.Token!,
                username,
                CancellationToken.None);

            if (profile is not null)
            {
                _results.Add(new SearchUserVm
                {
                    Username = profile.Username,
                    Fingerprint = profile.Fingerprint,
                    AvatarBase64 = profile.AvatarBase64,
                    CreatedAt = profile.CreatedAt
                });
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Поиск", ex.Message, "OK");
        }
        finally
        {
            _searching = false;
            SearchButton.IsEnabled = true;
        }
    }

    private async void OnAddClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not SearchUserVm user)
        {
            return;
        }

        try
        {
            var conversation = await _chatManager.AddContactAsync(user.Username);
            await Navigation.PushAsync(new ChatPage(conversation.Username));
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("uc_026", "Не удалось добавить контакт"), ex.Message, "OK");
        }
    }
}
