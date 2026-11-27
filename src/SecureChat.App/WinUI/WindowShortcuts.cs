#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;

namespace SecureChat.App.WinUI;

/// <summary>
/// Глобальные горячие клавиши для Windows-окна MAUI-приложения.
/// Поддерживает: Ctrl+N (новый чат), Ctrl+F (поиск), Ctrl+Shift+F (глобальный поиск),
/// Ctrl+S (настройки), Ctrl+P (профиль), Ctrl+Shift+A (архив), Escape (закрыть чат).
/// </summary>
public sealed class WindowShortcuts : IDisposable
{
    private readonly Window _window;
    private readonly Action _onNewChat;
    private readonly Action _onCloseChat;
    private readonly Action _onFocusSearch;
    private readonly Action _onSettings;
    private readonly Action _onProfile;
    private readonly Action _onArchive;
    private readonly Action _onGlobalSearch;
    private global::Microsoft.UI.Xaml.Window? _nativeWindow;
    private global::Microsoft.UI.Xaml.UIElement? _hookedContent;
    private bool _disposed;

    private WindowShortcuts(
        Window window,
        Action onNewChat,
        Action onCloseChat,
        Action? onFocusSearch,
        Action? onSettings,
        Action? onProfile,
        Action? onArchive,
        Action? onGlobalSearch)
    {
        _window = window;
        _onNewChat = onNewChat;
        _onCloseChat = onCloseChat;
        _onFocusSearch = onFocusSearch ?? (() => { });
        _onSettings = onSettings ?? (() => { });
        _onProfile = onProfile ?? (() => { });
        _onArchive = onArchive ?? (() => { });
        _onGlobalSearch = onGlobalSearch ?? (() => { });

        _window.HandlerChanged += OnHandlerChanged;
    }

    public static IDisposable Attach(
        Window window,
        Action onNewChat,
        Action onCloseChat,
        Action? onFocusSearch = null,
        Action? onSettings = null,
        Action? onProfile = null,
        Action? onArchive = null,
        Action? onGlobalSearch = null)
    {
        return new WindowShortcuts(window, onNewChat, onCloseChat, onFocusSearch,
            onSettings, onProfile, onArchive, onGlobalSearch);
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (_window.Handler is not null && _nativeWindow is null)
        {
            if (_window.Handler.PlatformView is global::Microsoft.UI.Xaml.Window nativeWindow)
            {
                _nativeWindow = nativeWindow;
                if (nativeWindow.Content is global::Microsoft.UI.Xaml.UIElement content)
                {
                    content.KeyDown += OnNativeKeyDown;
                    _hookedContent = content;
                }
            }
        }
    }

    private void OnNativeKeyDown(object sender, global::Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        var isCtrl = IsControlKeyDown();

        if (args.Key == global::Windows.System.VirtualKey.N && isCtrl)
        {
            args.Handled = true;
            _onNewChat();
        }
        else if (args.Key == global::Windows.System.VirtualKey.F && isCtrl)
        {
            args.Handled = true;
            if (IsShiftKeyDown()) _onGlobalSearch();
            else _onFocusSearch();
        }
        else if (args.Key == global::Windows.System.VirtualKey.S && isCtrl)
        {
            args.Handled = true;
            _onSettings();
        }
        else if (args.Key == global::Windows.System.VirtualKey.P && isCtrl)
        {
            args.Handled = true;
            _onProfile();
        }
        else if (args.Key == global::Windows.System.VirtualKey.A && isCtrl && IsShiftKeyDown())
        {
            args.Handled = true;
            _onArchive();
        }
        else if (args.Key == global::Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            _onCloseChat();
        }
    }

    private static bool IsShiftKeyDown()
    {
        var coreWindow = global::Windows.UI.Core.CoreWindow.GetForCurrentThread();
        if (coreWindow is null) return false;
        var state = coreWindow.GetAsyncKeyState(global::Windows.System.VirtualKey.Shift);
        return (state & global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private static bool IsControlKeyDown()
    {
        var coreWindow = global::Windows.UI.Core.CoreWindow.GetForCurrentThread();
        if (coreWindow is null)
        {
            return false;
        }

        var state = coreWindow.GetAsyncKeyState(global::Windows.System.VirtualKey.Control);
        return (state & global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.HandlerChanged -= OnHandlerChanged;

        // Unsubscribe from KeyDown on the UIElement (window content), not on Window itself
        if (_hookedContent is not null)
        {
            _hookedContent.KeyDown -= OnNativeKeyDown;
            _hookedContent = null;
        }

        _nativeWindow = null;
    }
}

#endif
