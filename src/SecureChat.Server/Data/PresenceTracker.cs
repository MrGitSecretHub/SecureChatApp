using System.Collections.Concurrent;

namespace SecureChat.Server.Data;

/// <summary>
/// Отслеживает подключённых пользователей, чтобы профиль показывал реальный
/// статус «в сети», а не только сохранённый в базе.
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _online = new();

    public void Connect(string userId) =>
        _online[userId] = DateTimeOffset.UtcNow;

    public void Disconnect(string userId) =>
        _online.TryRemove(userId, out _);

    private readonly ConcurrentDictionary<string, byte> _hidden = new();

    /// <summary>Скрывает присутствие: остальные видят пользователя вне сети, сообщения доходят как обычно.</summary>
    public void SetHidden(string userId, bool hidden)
    {
        if (hidden)
        {
            _hidden[userId] = 0;
        }
        else
        {
            _hidden.TryRemove(userId, out _);
        }
    }

    public bool IsOnline(string userId) =>
        _online.ContainsKey(userId) && !_hidden.ContainsKey(userId);

    /// <summary>Человекочитаемый статус: учитывает личный статус и присутствие.</summary>
    public (string Status, string Color) GetStatus(UserRecord user)
    {
        var manual = string.IsNullOrWhiteSpace(user.StatusMessage) ? "Онлайн" : user.StatusMessage;

        if (string.Equals(manual, "Неактивен", StringComparison.OrdinalIgnoreCase))
        {
            return ("Неактивен", "#80848F");
        }

        if (!IsOnline(user.Id))
        {
            var last = DateTimeOffset.FromUnixTimeMilliseconds(user.LastSeenUnixMs);
            if (last > DateTimeOffset.UtcNow.AddDays(-7))
            {
                return ($"Неактивен · был {last.ToLocalTime():dd.MM HH:mm}", "#80848F");
            }

            return ("Неактивен", "#80848F");
        }

        if (string.Equals(manual, "Занят", StringComparison.OrdinalIgnoreCase))
        {
            return ("Занят", "#FAA81A");
        }

        return ("Онлайн", "#3BA55D");
    }
}
