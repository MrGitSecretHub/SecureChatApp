using SecureChat.App.Services;

namespace SecureChat.App.Pages;

public partial class ChatPage : ContentPage
{
    private readonly string _username;
    private readonly string? _scrollToMessageId;

    public ChatPage(string username, string? scrollToMessageId = null)
    {
        _username = username;
        _scrollToMessageId = scrollToMessageId;
        InitializeComponent();
        Title = username;
        Chat.CloseRequested += OnCloseRequested;
        Chat.SettingsRequested += OnSettingsRequested;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            await Chat.OpenAsync(_username, _scrollToMessageId);
            Title = Chat.Username ?? _username;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Чат", ex.Message, "OK");
            if (Navigation.NavigationStack.Count > 1)
            {
                await Navigation.PopAsync();
            }
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Chat.Close();
    }

    private async void OnCloseRequested()
    {
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
    }

    private async void OnSettingsRequested() =>
        await Navigation.PushAsync(AppServices.Get<SettingsPage>());
}
