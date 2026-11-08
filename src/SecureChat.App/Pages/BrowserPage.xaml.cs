namespace SecureChat.App.Pages;

/// <summary>Встроенный браузер на WebView: открытие ссылок из чатов и сообществ.</summary>
public partial class BrowserPage : ContentPage
{
    private const string HomeUrl = "https://example.com";
    private string? _currentUrl;

    public BrowserPage()
    {
        InitializeComponent();
    }

    public BrowserPage(string url) : this()
    {
        AddressEntry.Text = url;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (string.IsNullOrWhiteSpace(AddressEntry.Text))
        {
            AddressEntry.Text = HomeUrl;
        }

        Navigate(AddressEntry.Text);
    }

    private async void OnNavigate(object? sender, EventArgs e)
    {
        var text = AddressEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains('.', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal))
            {
                text = "https://" + text;
            }
            else
            {
                text = "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
            }

            AddressEntry.Text = text;
        }

        try
        {
            Navigate(text);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    private void Navigate(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            BrowserWebView.Source = new UrlWebViewSource { Url = uri.ToString() };
        }
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        if (BrowserWebView.CanGoBack)
        {
            BrowserWebView.GoBack();
        }
    }

    private async void OnForwardClicked(object sender, EventArgs e)
    {
        if (BrowserWebView.CanGoForward)
        {
            BrowserWebView.GoForward();
        }
    }

    private async void OnReloadClicked(object sender, EventArgs e)
    {
        BrowserWebView.Reload();
    }

    private async void OnHomeClicked(object sender, EventArgs e)
    {
        AddressEntry.Text = HomeUrl;
        Navigate(HomeUrl);
    }

    private async void OnOpenExternalClicked(object sender, EventArgs e)
    {
        var url = _currentUrl ?? AddressEntry.Text;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Launcher.Default.OpenAsync(uri);
        }
    }

    private async void OnNavigating(object sender, WebNavigatingEventArgs e)
    {
        if (e.Cancel)
        {
            return;
        }

        // Внешние ссылки (например, Telegram) открываем системным браузером,
        // а встроенный WebView оставляем только для обычных страниц.
        if (!e.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !e.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
            {
                await Launcher.Default.OpenAsync(uri);
            }
        }
    }

    private void OnNavigated(object sender, WebNavigationEventArgs e)
    {
        _currentUrl = e.Url;
        if (e.Url is not null && AddressEntry.Text != e.Url)
        {
            AddressEntry.Text = e.Url;
        }
    }
}
