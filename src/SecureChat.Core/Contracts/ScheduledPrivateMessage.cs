namespace SecureChat.Core.Contracts;

/// <summary>Отложенное личное сообщение: хранится lokalno и отправляется в нужное время.</summary>
public sealed class ScheduledPrivateMessage
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public long SendAtUnixMs { get; set; }
}
