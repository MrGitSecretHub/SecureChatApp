using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SecureChat.App.Views;

/// <summary>Строка списка тем форума: заголовок, счётчики и активная тема.</summary>
public sealed class TopicVm : INotifyPropertyChanged
{
    private bool _isActive;
    private int _unread;

    public TopicVm(string id, string title, int messageCount, bool isClosed, string createdByUsername)
    {
        Id = id;
        Title = title;
        MessageCount = messageCount;
        IsClosed = isClosed;
        CreatedByUsername = createdByUsername;
    }

    public string Id { get; }
    public string Title { get; }
    public int MessageCount { get; }
    public bool IsClosed { get; }
    public string CreatedByUsername { get; }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; OnPropertyChanged(); } }
    }

    public int Unread
    {
        get => _unread;
        set
        {
            if (_unread != value)
            {
                _unread = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadText));
            }
        }
    }

    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
