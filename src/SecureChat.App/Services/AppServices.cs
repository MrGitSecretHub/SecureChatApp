using Microsoft.Extensions.DependencyInjection;

namespace SecureChat.App.Services;

/// <summary>Небольшой сервис-локатор для страниц MAUI.</summary>
public static class AppServices
{
    public static IServiceProvider Provider { get; set; } = null!;

    public static T Get<T>() where T : notnull =>
        Provider.GetRequiredService<T>();
}
