using SecureChat.Core.Contracts;

namespace SecureChat.App.Services;

/// <summary>
/// Уведомления о @упоминаниях в пространствах (группа, канал, сообщество, форум).
/// Работает на уровне приложения: расшифровывает входящие сообщение и, если
/// в тексте упомянут текущего пользователя, показывает системное уведомление
/// (Android в фоне) или проигрывает звук. Сервер об упоминаниях не знает —
/// всё живёт внутри E2E-шифрованного содержимого.
/// </summary>
public sealed class SpaceMentionNotifier
{
    private readonly SpaceService _spaces;
    private readonly NotificationService _notifications;
    private readonly AppSession _session;
    private readonly SoundService _sounds;
    private readonly ChatManager _chat;
    private readonly Dictionary<string, string> _spaceNames = new(StringComparer.Ordinal);

    public SpaceMentionNotifier(
        SpaceService spaces,
        NotificationService notifications,
        AppSession session,
        SoundService sounds,
        ChatManager chat)
    {
        _spaces = spaces;
        _notifications = notifications;
        _session = session;
        _sounds = sounds;
        _chat = chat;

        _spaces.SpaceMessageReceived += OnSpaceMessageReceived;
        _chat.ConnectionStateChanged += OnConnectionStateChanged;
        EnsureSubscription();
    }

    private void OnConnectionStateChanged(bool connected)
    {
        if (connected)
        {
            EnsureSubscription();
        }
    }

    /// <summary>Подписка на события хаба должна состояться хотя бы один раз,
    /// чтобы упоминания приходили даже без открытых страниц пространств.</summary>
    private void EnsureSubscription()
    {
        try
        {
            _spaces.Subscribe();
        }
        catch
        {
            // Идёт переподключение — подписка повторится после ConnectionStateChanged.
        }
    }

    private void OnSpaceMessageReceived(SpaceMessageDto message) =>
        _ = Task.Run(() => HandleAsync(message));

    private async Task HandleAsync(SpaceMessageDto message)
    {
        try
        {
            var me = _session.User;
            if (me is null || string.IsNullOrWhiteSpace(me.Username))
            {
                return;
            }

            // Свои упоминания не трекаем.
            if (string.Equals(message.SenderId, me.Id, StringComparison.Ordinal))
            {
                return;
            }

            var payload = await _spaces.DecryptPayloadAsync(message.SpaceId, message);

            // Триггеры, как в Telegram: личное @упоминание, ответ на моё сообщение, @all.
            var personal = payload is not null && payload.Mentions.Any(name =>
                string.Equals(name, me.Username, StringComparison.OrdinalIgnoreCase));
            var forAll = payload is not null && payload.Mentions.Contains(
                SpaceMentions.AllToken, StringComparer.OrdinalIgnoreCase);
            var repliedToMe = payload is not null && string.Equals(
                payload.ReplyToSender, me.Username, StringComparison.OrdinalIgnoreCase);

            if (!personal && !forAll && !repliedToMe)
            {
                return;
            }

            // Отключение звука конкретного пространства гасит и уведомления об упоминаниях.
            if (await _chat.IsSpaceMutedAsync(message.SpaceId))
            {
                return;
            }

            var spaceName = await ResolveSpaceNameAsync(message.SpaceId);
            var preview = MessagePreview.Build(payload!.Text ?? string.Empty);
            var what = personal
                ? "упомянул(а) вас"
                : repliedToMe
                    ? "ответил(а) на ваше сообщение"
                    : "упомянул(а) всех участников";
            var body = $"{message.SenderUsername} {what}: {preview}";

            var viaSystem = false;
            if (DeviceInfo.Platform == DevicePlatform.Android && !AppLifecycle.IsForeground)
            {
                // Прямо в чат автора упоминания — это ближайшая точка ответа.
                viaSystem = _notifications.ShowNotification(
                    spaceName,
                    body,
                    message.SenderUsername,
                    1,
                    silent: !_session.SoundsEnabled);
            }

            if (!viaSystem && _session.SoundsEnabled)
            {
                _sounds.PlayNotification();
            }
        }
        catch
        {
            // Сбой уведомления не имеет права ломать приём сообщений.
        }
    }

    /// <summary>Название пространства для заголовка уведомления; кэшируется.</summary>
    private async Task<string> ResolveSpaceNameAsync(string spaceId)
    {
        lock (_spaceNames)
        {
            if (_spaceNames.TryGetValue(spaceId, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var spaces = await _spaces.ListAsync();
            lock (_spaceNames)
            {
                foreach (var space in spaces)
                {
                    _spaceNames[space.Id] = space.Name;
                }
            }
        }
        catch
        {
            // Нет списка — уведомим нейтральным заголовком.
        }

        lock (_spaceNames)
        {
            return _spaceNames.TryGetValue(spaceId, out var name) ? name : "Пространство";
        }
    }
}
