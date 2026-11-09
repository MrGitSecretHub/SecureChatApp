namespace SecureChat.App.Services;

/// <summary>Windows: держатели ядра/сети не требуются, пока работает процесс.</summary>
public sealed partial class BackgroundKeepAlive
{
    private static partial void AcquireCore()
    {
        // Нет необходимости на Windows.
    }

    private static partial void ReleaseCore()
    {
        // Нет необходимости на Windows.
    }
}
