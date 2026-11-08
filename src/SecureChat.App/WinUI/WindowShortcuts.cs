#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;

namespace SecureChat.App.WinUI;

/// <summary>
/// Глобальные горячие клавиши для Windows-окна MAUI-приложения.
/// Поддерживает: Ctrl+N (новый чат), Ctrl+F (поиск), Escape (снять выделение/закрыть чат).
/// </summary>
public sealed class WindowShortcuts : IDisposable
{
    private readonly Window _window;
    private readonly Action _onNewChat;
    private readonly Action _onCloseChat;
    private readonly Action _onFocusSearch;
    private global::Microsoft.UI.Xaml.Window? _nativeWindow;
    private global::Microsoft.UI.Xaml.UIElement? _hookedContent;
    private bool _disposed;

    private WindowShortcuts(
        Window window,
        Action onNewChat,
        Action onCloseChat,
        Action onFocusSearch)
    {
        _window = window;
        _onNewChat = onNewChat;
        _onCloseChat = onCloseChat;
        _onFocusSearch = onFocusSearch;

        _window.HandlerChanged += OnHandlerChanged;
    }

    public static IDisposable Attach(
        Window window,
        Action onNewChat,
        Action onCloseChat,
        Action? onFocusSearch = null)
    {
        return new WindowShortcuts(window, onNewChat, onCloseChat, onFocusSearch ?? (() => { }));
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
            _onFocusSearch();
        }
        else if (args.Key == global::Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            _onCloseChat();
        }
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
