using SecureChat.App.Services;
using SecureChat.App.Views;

namespace SecureChat.App.Pages;

/// <summary>Поиск по тексту во всех чатах и архиве на этом устройстве.</summary>
public partial class GlobalSearchPage : ContentPage
{
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private CancellationTokenSource? _searchCts;
    private string _initialQuery = string.Empty;
    private bool _navigating;
    private List<SearchResultVm> _allResults = new();

    public GlobalSearchPage()
    {
        InitializeComponent();
        SenderPicker.ItemsSource = new[]
        {
            Loc.T("search_all", "All"), Loc.T("search_mine", "Mine"), Loc.T("search_others", "Others")
        };
        TypePicker.ItemsSource = new[]
        {
            Loc.T("search_all_types", "All types"),
            Loc.T("search_with_files", "With files"),
            Loc.T("search_text_only", "Text only")
        };
        DatePicker.ItemsSource = new[]
        {
            Loc.T("search_any_time", "Any time"), Loc.T("search_today", "Today"),
            Loc.T("search_7days", "Last 7 days"), Loc.T("search_30days", "Last 30 days")
        };
        SenderPicker.SelectedIndex = TypePicker.SelectedIndex = DatePicker.SelectedIndex = 0;
        SenderPicker.SelectedIndexChanged += OnFilterChanged;
        TypePicker.SelectedIndexChanged += OnFilterChanged;
        DatePicker.SelectedIndexChanged += OnFilterChanged;
    }

    private void OnFilterChanged(object? sender, EventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var items = _allResults.Where(item =>
        {
            if (SenderPicker.SelectedIndex == 1 && !item.IsMine) return false;
            if (SenderPicker.SelectedIndex == 2 && item.IsMine) return false;
            if (TypePicker.SelectedIndex == 1 && !item.HasMedia) return false;
            if (TypePicker.SelectedIndex == 2 && item.HasMedia) return false;
            return DatePicker.SelectedIndex switch
            {
                1 => item.SentAtUnixMs >= new DateTimeOffset(DateTime.Today, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                2 => now - item.SentAtUnixMs <= TimeSpan.FromDays(7).TotalMilliseconds,
                3 => now - item.SentAtUnixMs <= TimeSpan.FromDays(30).TotalMilliseconds,
                _ => true
            };
        }).ToList();
        ResultsView.ItemsSource = items;
        HintLabel.Text = items.Count == 0
            ? Loc.T("rs_148", "Совпадений нет.")
            : string.Format(Loc.T("rs_078"), items.Count);
    }

    public GlobalSearchPage(string initialQuery) : this()
    {
        _initialQuery = initialQuery?.Trim() ?? string.Empty;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (!string.IsNullOrWhiteSpace(_initialQuery)
            && !string.Equals(SearchEntry.Text, _initialQuery, StringComparison.Ordinal))
        {
            SearchEntry.Text = _initialQuery;
            _initialQuery = string.Empty;
        }

        SearchEntry.Focus();
    }

    private async void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            // Небольшая задержка, чтобы не сканировать историю на каждый символ.
            await Task.Delay(250, token);
            await RunSearchAsync(e.NewTextValue, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnSearchPressed(object sender, EventArgs e)
    {
        _searchCts?.Cancel();
        await RunSearchAsync(SearchEntry.Text);
    }

    private async Task RunSearchAsync(string? text, CancellationToken token = default)
    {
        var query = text?.Trim() ?? string.Empty;
        if (query.Length < 2)
        {
            _allResults.Clear();
            ResultsView.ItemsSource = Array.Empty<SearchResultVm>();
            HintLabel.Text = "Введите минимум 2 символа.";
            return;
        }

        var results = await _chatManager.SearchMessagesAsync(query);
        if (token.IsCancellationRequested)
        {
            return;
        }

        _allResults = results.Select(SearchResultVm.From).ToList();
        ApplyFilters();
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating || e.CurrentSelection.FirstOrDefault() is not SearchResultVm item)
        {
            return;
        }

        _navigating = true;
        ResultsView.SelectedItem = null;

        try
        {
            var desktop = Navigation.NavigationStack
                .OfType<DesktopMainPage>()
                .LastOrDefault();

            if (desktop is not null)
            {
                await Navigation.PopAsync();
                await desktop.OpenChatAsync(item.Username);
            }
            else
            {
                await Navigation.PushAsync(new ChatPage(item.Username));
            }
        }
        finally
        {
            _navigating = false;
        }
    }
}
