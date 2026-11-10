#if WINDOWS
using Microsoft.UI.Xaml.Input;

namespace SecureChat.App.Views;

/// <summary>Windows: ↑/↓ двигают выделение в меню @упоминаний, Enter выбирает,
/// Esc закрывает — как в Telegram. Хуки вешаются на нативный TextBox.</summary>
public partial class MentionEntryView
{
    private Microsoft.UI.Xaml.Controls.TextBox? _nativeEntry;
    private bool _hooksAttached;

    partial void AttachPlatformHooks()
    {
        if (_hooksAttached)
        {
            return;
        }

        if (InnerEntry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox native)
        {
            _nativeEntry = native;
            _nativeEntry.KeyDown += OnNativeKeyDown;
            _hooksAttached = true;
        }
    }

    partial void DetachPlatformHooks()
    {
        if (_nativeEntry is not null && _hooksAttached)
        {
            _nativeEntry.KeyDown -= OnNativeKeyDown;
        }

        _hooksAttached = false;
        _nativeEntry = null;
    }

    private void OnNativeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Меню закрыто — стандартное поведение поля ввода.
        if (!MentionMenu.IsVisible)
        {
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Up:
                OnMenuNavigationCommand(-1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                OnMenuNavigationCommand(1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                if (_rowUsernames.Count > 0)
                {
                    OnMenuAcceptCommand();
                    e.Handled = true;
                }

                break;

            case Windows.System.VirtualKey.Escape:
                OnMenuDismissCommand();
                e.Handled = true;
                break;
        }
    }
}
#else
namespace SecureChat.App.Views;

/// <summary>Android и прочие платформы: выбор упоминания касанием,
/// клавиатурная навигация не требуется.</summary>
public partial class MentionEntryView
{
    partial void AttachPlatformHooks()
    {
        // Клавиатурные хуки нужны только на Windows.
    }

    partial void DetachPlatformHooks()
    {
        // Клавиатурные хуки нужны только на Windows.
    }
}
#endif
