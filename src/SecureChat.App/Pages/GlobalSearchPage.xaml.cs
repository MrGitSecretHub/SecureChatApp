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

    public GlobalSearchPage()
    {
        InitializeComponent();
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
            ResultsView.ItemsSource = Array.Empty<SearchResultVm>();
            HintLabel.Text = "Введите минимум 2 символа.";
            return;
        }

        var results = await _chatManager.SearchMessagesAsync(query);
        if (token.IsCancellationRequested)
        {
            return;
        }

        var items = results.Select(SearchResultVm.From).ToList();
        ResultsView.ItemsSource = items;
        HintLabel.Text = items.Count == 0
            ? Loc.T("rs_148", "Совпадений нет.")
            : string.Format(Loc.T("rs_078"), items.Count);
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
