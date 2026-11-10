using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace SecureChat.App.Views;

/// <summary>
/// Поле ввода сообщения с меню @упоминаний в стиле Telegram:
/// меню появляется сразу на «@» (полный список участников), фильтруется по
/// префиксу логина при печати, первая строка выделена, стрелки/Enter/Esc
/// работают (Windows), выбор вставляет «@username » и оставляет каретку.
/// Используется в чатах пространств и в окне комментариев к постам каналов.
/// </summary>
public partial class MentionEntryView : ContentView
{
    private const int RowHeight = 46;

    /// <summary>Элемент меню: логин участника + первая буква на аватарке.</summary>
    public sealed class MentionItem
    {
        public required string Username { get; init; }
        public string Initial => Username.Length == 0 ? "?" : Username[..1].ToUpperInvariant();
    }

    private readonly List<string> _usernames = new();
    private readonly List<string> _rowUsernames = new();
    private string? _programmaticText;
    private int _tokenStart;
    private int _highlightIndex;
    private string _token = string.Empty;

    public MentionEntryView()
    {
        InitializeComponent();
    }

    /// <summary>Отправка по Enter / кнопке Send.</summary>
    public event EventHandler? Completed;

    /// <summary>Логины участников пространства для меню упоминаний.</summary>
    public void SetMembers(IEnumerable<string> usernames)
    {
        _usernames.Clear();
        _usernames.AddRange(usernames ?? []);
        _usernames.Sort(StringComparer.OrdinalIgnoreCase);
        HideMenu();
    }

    /// <summary>Скрывает меню упоминаний.</summary>
    public void HideMenu()
    {
        MentionMenu.IsVisible = false;
        _rowUsernames.Clear();
        _highlightIndex = 0;
        _tokenStart = -1;
        _token = string.Empty;
    }

    /// <summary>Передаёт фокус полю ввода.</summary>
    public void FocusInput()
    {
        InnerEntry.Focus();
    }

    #region Фасад над Entry

    public string Text
    {
        get => InnerEntry.Text;
        set
        {
            // Запоминаем программное значение: событие TextChanged может прийти,
            // а может и не прийти — в обоих случаях меню не должно сбоить.
            _programmaticText = value ?? string.Empty;
            InnerEntry.Text = _programmaticText;
            HideMenu();
        }
    }

    public int CursorPosition
    {
        get => InnerEntry.CursorPosition;
        set => InnerEntry.CursorPosition = value;
    }

    public string Placeholder
    {
        get => InnerEntry.Placeholder;
        set => InnerEntry.Placeholder = value;
    }

    public bool IsReadOnly
    {
        get => InnerEntry.IsReadOnly;
        set => InnerEntry.IsReadOnly = value;
    }

    #endregion

    #region Меню, телеграм-стиль

    /// <summary>Ближайший токен @логин перед каретой (или null): позиция @ и напечатанный кусок.
    /// Пустой кусок после «@» — тоже валидный токен: меню показывает всех участников.</summary>
    private (int Start, string Token)? FindToken(string text, int caret)
    {
        var start = -1;
        for (var i = caret - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == '@')
            {
                start = i;
                break;
            }

            if (char.IsWhiteSpace(c) || !(char.IsLetterOrDigit(c) || c == '_'))
            {
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        // @ должен стоять в начале текста или после разделителя.
        if (start > 0)
        {
            var before = text[start - 1];
            if (char.IsLetterOrDigit(before) || before == '_' || before == '@')
            {
                return null;
            }
        }

        return (start, text[(start + 1)..caret]);
    }

    private void OnInnerTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_programmaticText is not null)
        {
            // Все ещё программное значение (или его эхо) — меню не трогаем.
            if (string.Equals(e.NewTextValue, _programmaticText, StringComparison.Ordinal))
            {
                return;
            }

            // Дальше текст уже менялся пользователем — меняем меню.
            _programmaticText = null;
        }

        UpdateMenu();
    }

    private void UpdateMenu()
    {
        var text = InnerEntry.Text ?? string.Empty;
        var caret = Math.Clamp(InnerEntry.CursorPosition, 0, text.Length);
        var token = FindToken(text, caret);

        if (token is null || _usernames.Count == 0)
        {
            HideMenu();
            return;
        }

        var (start, typed) = token.Value;
        var exact = typed.Length > 0 ? typed : null;
        var matches = _usernames
            .Where(name => typed.Length == 0 || name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name =>
            {
                if (exact is not null && string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
                {
                    return -1; // точное совпадение первым
                }

                return 0;
            })
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _tokenStart = start;
        _token = typed;

        if (matches.Count == 0)
        {
            HideMenu();
            return;
        }

        _rowUsernames.Clear();
        _rowUsernames.AddRange(matches);
        _highlightIndex = 0;
        RebuildRows();

        MentionMenu.HeightRequest = Math.Min(286, _rowUsernames.Count * RowHeight + 10);
        MentionMenu.IsVisible = true;
    }

    private void HighlightRelative(int delta)
    {
        if (_rowUsernames.Count == 0)
        {
            return;
        }

        var next = Math.Clamp(_highlightIndex + delta, 0, _rowUsernames.Count - 1);
        if (next != _highlightIndex)
        {
            _highlightIndex = next;
            RebuildRows();
        }
    }

    private void PickHighlighted()
    {
        if (_highlightIndex >= 0 && _highlightIndex < _rowUsernames.Count)
        {
            Pick(_rowUsernames[_highlightIndex]);
        }
    }

    private void RebuildRows()
    {
        MentionList.Children.Clear();
        for (var index = 0; index < _rowUsernames.Count; index++)
        {
            var item = new MentionItem { Username = _rowUsernames[index] };
            MentionList.Children.Add(CreateItem(item, index == _highlightIndex));
        }
    }

    private View CreateItem(MentionItem item, bool isHighlighted)
    {
        var tap = new TapGestureRecognizer { CommandParameter = item.Username };
        tap.Tapped += OnMentionChosen;

        var row = new Border
        {
            Padding = new Thickness(8, 6),
            BackgroundColor = isHighlighted
                ? ResolveResource<Color>("AccentSoft", Color.FromArgb("#1A0088CC"))
                : Colors.Transparent,
            StrokeThickness = 0,
            GestureRecognizers = { tap },
            MinimumHeightRequest = RowHeight
        };

        row.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) };

        row.Content = new HorizontalStackLayout
        {
            Spacing = 10,
            Children =
            {
                new Border
                {
                    WidthRequest = 34,
                    HeightRequest = 34,
                    BackgroundColor = ResolveResource<Color>("Accent", Color.FromArgb("#0088CC")),
                    StrokeShape = new Ellipse(),
                    StrokeThickness = 0,
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = item.Initial,
                        FontSize = 14,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = ResolveResource<Color>("AccentText", Colors.White),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    }
                },
                new VerticalStackLayout
                {
                    Spacing = 1,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new Label
                        {
                            Text = item.Username,
                            FontSize = 15,
                            FontAttributes = FontAttributes.Bold,
                            TextColor = ResolveResource<Color>("TextPrimary", Colors.Black),
                            VerticalOptions = LayoutOptions.Center
                        },
                        new Label
                        {
                            Text = "@" + item.Username,
                            FontSize = 12,
                            TextColor = ResolveResource<Color>("TextMuted", Colors.Gray),
                            VerticalOptions = LayoutOptions.Center
                        }
                    }
                }
            }
        };

        return row;
    }

    /// <summary>Вставляет «@username » вместо напечатанного токена, каретка после пробела.</summary>
    private void Pick(string username)
    {
        var text = InnerEntry.Text ?? string.Empty;
        var caret = Math.Clamp(InnerEntry.CursorPosition, 0, text.Length);

        // Токен мог уже не совпадать с сохранённым — не ломаем чужой текст.
        if (_tokenStart < 0 || _tokenStart >= text.Length)
        {
            HideMenu();
            return;
        }

        var insertion = "@" + username + " ";
        var rebuilt = text[.._tokenStart] + insertion + text[Math.Min(caret, text.Length)..];

        Text = rebuilt;

        try
        {
            InnerEntry.CursorPosition = Math.Min(_tokenStart + insertion.Length, rebuilt.Length);
        }
        catch
        {
            // Каретка устанавливается не везде — не мешаем.
        }

        HideMenu();
    }

    private void OnMentionChosen(object? sender, EventArgs e)
    {
        var username = (sender as TapGestureRecognizer)?.CommandParameter as string;
        if (string.IsNullOrEmpty(username))
        {
            return;
        }

        Pick(username);
    }

    private void OnInnerCompleted(object? sender, EventArgs e)
    {
        HideMenu();
        Completed?.Invoke(this, e);
    }

    #endregion

    #region Клавиатура (Windows: ↑ / ↓ / Enter / Esc)

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (Handler?.PlatformView is not null)
        {
            AttachPlatformHooks();
        }
        else
        {
            DetachPlatformHooks();
        }
    }

    partial void AttachPlatformHooks();

    partial void DetachPlatformHooks();

    /// <summary>Внутренняя: пересоздаёт строку меню (для наглядного выделения стрелками).
    /// Вызывается с платформенных хуков.</summary>
    private void OnMenuNavigationCommand(int direction) => HighlightRelative(direction);

    /// <summary>Внутренняя: принимает выделенную строку (Enter). Вызывается с хуков.</summary>
    private void OnMenuAcceptCommand() => PickHighlighted();

    /// <summary>Внутренняя: закрыть меню без выбора (Esc). Вызывается с хуков.</summary>
    private void OnMenuDismissCommand() => HideMenu();

    #endregion

    private static T ResolveResource<T>(string key, T fallback) where T : class
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is T typed)
        {
            return typed;
        }

        return fallback;
    }
}
