using System.ComponentModel;

namespace SecureChat.App.Pages;

/// <summary>
/// Плитка темы оформления в настройках: название темы и признак выбора
/// (обводка и кружок-радиокнопка, как в Telegram).
/// </summary>
public sealed class ThemeTileVm : INotifyPropertyChanged
{
    private bool _isSelected;

    public ThemeTileVm(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
