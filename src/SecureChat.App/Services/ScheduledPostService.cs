using SecureChat.App.Services;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Scheduling;

/// <summary>
/// Фоновая отправка отложенных постов пространств. Посты хранятся уже
/// зашифрованными на устройстве — сервер не знает о существовании очереди.
/// Таймер проверяет очередь раз в 20 секунд и при возврате приложения
/// на передний план.
/// </summary>
public sealed class ScheduledPostService
{
    private readonly LocalStore _localStore = AppServices.Get<LocalStore>();
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ChatManager _chat = AppServices.Get<ChatManager>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private System.Timers.Timer? _timer;
    private bool _running;

    /// <summary>Вызвано отправленное сообщение (spaceId, dto).</summary>
    public event Action<string, SpaceMessageDto>? PostSent;

    /// <summary>Запускает периодическую проверку очереди.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new System.Timers.Timer(TimeSpan.FromSeconds(20))
        {
            AutoReset = true
        };
        _timer.Elapsed += (_, _) => _ = FlushDueAsync();
        _timer.Start();
        AppLifecycle.EnteredForeground += OnEnteredForeground;
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        AppLifecycle.EnteredForeground -= OnEnteredForeground;
    }

    private void OnEnteredForeground()
    {
        if (_session.IsAuthenticated)
        {
            _ = FlushDueAsync();
        }
    }

    /// <summary>Отправляет все посты, чьё время пришло.</summary>
    public async Task<int> FlushDueAsync()
    {
        if (_running || !_session.IsAuthenticated)
        {
            return 0;
        }

        await _flushLock.WaitAsync();
        _running = true;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var posts = await _localStore.GetScheduledSpacePostsAsync();
            var due = posts.Where(post => post.SendAtUnixMs <= now).ToList();
            var duePrivate = (await _localStore.GetScheduledPrivateMessagesAsync())
                .Where(post => post.SendAtUnixMs <= now).ToList();
            var sent = 0;

            foreach (var msg in duePrivate)
            {
                try
                {
                    await _chat.SendMessageAsync(msg.Username, msg.Text);
                    await _localStore.RemoveScheduledPrivateMessageAsync(msg.Id);
                    sent++;
                }
                catch { }
            }

            foreach (var post in due)
            {
                try
                {
                    // Отправляем уже зашифрованный шифротекст как есть.
                    var result = await _spaces.SendCipherAsync(
                        post.SpaceId,
                        post.TopicId,
                        post.Cipher);

                    // При ошибке тоже удаляем: просроченный пост не должен
                    // уйти через час, когда связь внезапно вернётся.
                    await _localStore.RemoveScheduledSpacePostAsync(post.Id);

                    if (result is not null)
                    {
                        PostSent?.Invoke(post.SpaceId, result);
                        sent++;
                    }
                }
                catch
                {
                    // Нет связи — оставляем пост в очереди, попробуем позже.
                }
            }

            return sent;
        }
        finally
        {
            _running = false;
            _flushLock.Release();
        }
    }
}
