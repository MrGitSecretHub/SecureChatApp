using Microsoft.Extensions.Logging;
using SecureChat.App.Pages;
using SecureChat.App.Scheduling;
using SecureChat.App.Services;

namespace SecureChat.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<SecretStore>();
        builder.Services.AddSingleton<QuickRepliesService>();
        builder.Services.AddSingleton<StoryService>();
        builder.Services.AddSingleton<LocalizationService>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<KeyBackupService>();
        builder.Services.AddSingleton<NotificationService>();
        builder.Services.AddSingleton<NtfyPushService>();
        builder.Services.AddSingleton<SoundService>();
        builder.Services.AddSingleton<AppSession>();
        builder.Services.AddSingleton<WallpaperService>();
        builder.Services.AddSingleton<MediaSaveService>();
        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddSingleton<LocalStore>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<ChatManager>();
        builder.Services.AddSingleton<LinkPreviewService>();
        builder.Services.AddSingleton<SpaceService>();
        builder.Services.AddSingleton<CustomPackService>();
        builder.Services.AddSingleton<AccountService>();
        builder.Services.AddSingleton<SpaceMentionNotifier>();
        builder.Services.AddSingleton<ScheduledPostService>();

#if ANDROID
        builder.Services.AddSingleton<IAudioRecorder, AudioRecorderAndroid>();
        builder.Services.AddSingleton<IVideoRecorder, VideoRecorderAndroid>();
#elif WINDOWS
        builder.Services.AddSingleton<IAudioRecorder, AudioRecorderWindows>();
        builder.Services.AddSingleton<IVideoRecorder, VideoRecorderWindows>();
#endif

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<ChatsPage>();
        builder.Services.AddTransient<ContactsPage>();
        builder.Services.AddTransient<SearchUsersPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<SpacesPage>();
        builder.Services.AddTransient<BrowserPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        AppServices.Provider = app.Services;
        return app;
    }
}
