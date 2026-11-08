using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SecureChat.App.Views;

/// <summary>
/// Отдельный чип реакции: один эмодзи, счётчик и признак того,
/// что реакция поставлена текущим пользователем.
/// </summary>
public sealed class ReactionChipVm : INotifyPropertyChanged
{
    private int _count;
    private bool _isMine;

    public required string Emoji { get; init; }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CountText));
        }
    }

    public bool IsMine
    {
        get => _isMine;
        set
        {
            if (_isMine == value)
            {
                return;
            }

            _isMine = value;
            OnPropertyChanged();
        }
    }

    public string CountText => Count > 1 ? Count.ToString() : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
