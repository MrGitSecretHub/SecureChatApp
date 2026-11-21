using System.Collections.ObjectModel;
using Microsoft.Maui.Storage;
using System.Globalization;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>Результат поиска одного пользователя на сервере.</summary>
public sealed class SearchUserVm
{
    public required string Username { get; init; }
    public required string Fingerprint { get; init; }
    public required string? AvatarBase64 { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public string OnlineDot { get; init; } = string.Empty;

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

    public string DisplayNameText =>
        string.IsNullOrWhiteSpace(DisplayName) ? "@" + Username : DisplayName;

    public string UsernameText => "@" + Username;

    public string StatusText =>
        string.IsNullOrWhiteSpace(Nickname)
            ? "• " + CreatedAtText
            : Nickname.Length > 40 ? Nickname[..40] + "…" : Nickname;

    public bool HasCreatedAt => CreatedAt != default;
}

public partial class SearchUsersPage : ContentPage
{
    private readonly ApiClient _api = AppServices.Get<ApiClient>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly ObservableCollection<SearchUserVm> _results = new();
    private List<UserProfileDto>? _allUsers;
    private CancellationTokenSource? _debounce;
    private bool _searching;
    private const string RecentKey = "search_recent_usernames";

    public SearchUsersPage()
    {
        InitializeComponent();
        ResultsView.ItemsSource = _results;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UsernameEntry.Focus();
        await EnsureUsersAsync();
        ShowRecent();
    }

    private async Task EnsureUsersAsync()
    {
        if (_allUsers is not null) return;
        try
        {
            _allUsers = (await _api.ListUsersAsync(
                _session.ServerUrl, _session.Token!, CancellationToken.None)).ToList();
        }
        catch
        {
            _allUsers = new List<UserProfileDto>();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _ = DebouncedSearchAsync();

    private async void OnSearchClicked(object sender, EventArgs e) =>
        await ApplyFilterAsync(CancellationToken.None);

    private async Task DebouncedSearchAsync()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        try
        {
            await Task.Delay(350, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested) return;
        await ApplyFilterAsync(token);
    }

    private async Task ApplyFilterAsync(CancellationToken token)
    {
        var raw = (UsernameEntry.Text ?? string.Empty).Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowRecent();
            return;
        }

        await EnsureUsersAsync();
        if (token.IsCancellationRequested) return;

        var query = raw.Trim();
        var matches = (_allUsers ?? new List<UserProfileDto>())
            .Where(u => !string.Equals(u.Id, _session.User?.Id, StringComparison.Ordinal))
            .Select(u => new
            {
                User = u,
                Score = Score(u, query)
            })
            .Where(x => x.Score < 100)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.User.Username, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .Select(x => ToVm(x.User))
            .ToList();

        _results.Clear();
        foreach (var item in matches) _results.Add(item);

        if (matches.Count == 0)
        {
            EmptyTitleLabel.Text = $"Пользователь @{query} не найден";
            EmptyCaptionLabel.Text = "Проверьте написание логина или попробуйте часть имени.";
        }
        else
        {
            EmptyTitleLabel.Text = "🔍 Найдено";
            EmptyCaptionLabel.Text = $"Результаты по запросу @{query}";
        }
    }

    private static int Score(UserProfileDto user, string query)
    {
        var name = user.Username ?? string.Empty;
        var nick = user.Nickname ?? string.Empty;
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (nick.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (nick.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 100;
    }

    private static SearchUserVm ToVm(UserProfileDto profile) => new()
    {
        Username = profile.Username,
        Fingerprint = profile.Fingerprint,
        AvatarBase64 = profile.AvatarBase64,
        CreatedAt = profile.CreatedAt,
        DisplayName = profile.Nickname ?? string.Empty,
        Nickname = profile.Nickname ?? string.Empty,
        OnlineDot = profile.OnlineStatus == "Онлайн" ? "🟢" : "⚪"
    };

    private void ShowRecent()
    {
        _results.Clear();
        var recent = Preferences.Get(RecentKey, string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in recent.Take(10))
        {
            _results.Add(new SearchUserVm
            {
                Username = name,
                Fingerprint = string.Empty,
                AvatarBase64 = null,
                CreatedAt = default,
                DisplayName = "Недавний поиск"
            });
        }
        EmptyTitleLabel.Text = "Недавние поиски";
        EmptyCaptionLabel.Text = "Введите @username или часть имени — подсказки появятся сразу.";
    }

    private static void Remember(string username)
    {
        var recent = Preferences.Get(RecentKey, string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.Equals(x, username, StringComparison.OrdinalIgnoreCase))
            .Take(9).ToList();
        recent.Insert(0, username);
        Preferences.Set(RecentKey, string.Join('|', recent));
    }

    private async void OnAddClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not SearchUserVm user)
        {
            return;
        }

        try
        {
            Remember(user.Username);
            var conversation = await _chatManager.AddContactAsync(user.Username);
            await Navigation.PushAsync(new ChatPage(conversation.Username));
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("uc_026", "Не удалось добавить контакт"), ex.Message, "OK");
        }
    }
}
