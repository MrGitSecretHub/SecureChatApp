namespace SecureChat.App.Pages;

/// <summary>
/// Модальный запрос пароля с скрытым вводом. Возвращает null при отмене
/// и пустую строку, если пользователь подтвердил пустое поле.
/// </summary>
public partial class PasswordPromptPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _completion = new();
    private bool _isClosed;

    private PasswordPromptPage(string title, string message, string acceptText)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        AcceptButton.Text = acceptText;
    }

    public static async Task<string?> ShowAsync(
        INavigation navigation,
        string title,
        string message,
        string acceptText = "Продолжить")
    {
        var page = new PasswordPromptPage(title, message, acceptText);
        await navigation.PushModalAsync(page);
        return await page._completion.Task;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        PasswordEntry.Focus();
    }

    private async void OnAcceptClicked(object? sender, EventArgs e)
    {
        await CloseAsync(PasswordEntry.Text ?? string.Empty);
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await CloseAsync(null);
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private async Task CloseAsync(string? result)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        try
        {
            await Navigation.PopModalAsync();
        }
        finally
        {
            _completion.TrySetResult(result);
        }
    }
}
