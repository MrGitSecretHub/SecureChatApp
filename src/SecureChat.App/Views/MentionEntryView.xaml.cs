using Microsoft.Maui.Controls.Shapes;

namespace SecureChat.App.Views;

/// <summary>
/// Поле ввода сообщения с меню @упоминаний. Пока пользователь печатает
/// @логин участника, над полем появляется список совпадающих участников;
/// выбор вставляет «@username » в текст. Используется в чатах пространств
/// и в окне комментариев к постам каналов.
/// </summary>
public partial class MentionEntryView : ContentView
{
    /// <summary>Элемент меню: логин участника + первая буква на аватарке.</summary>
    public sealed class MentionItem
    {
        public required string Username { get; init; }
        public string Initial => Username.Length == 0 ? "?" : Username[..1].ToUpperInvariant();
    }

    private readonly List<string> _usernames = new();
    private bool _suppressMenuUpdate;
    private int _tokenStart;
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
        HideMenu();
    }

    /// <summary>Скрывает меню упоминаний.</summary>
    public void HideMenu() => MentionMenu.IsVisible = false;

    /// <summary>Убирает фокус и передаёт его полю ввода.</summary>
    public void FocusInput()
    {
        InnerEntry.Focus();
    }

    #region Фасад над Entry: совместим с прежним кодом страниц

    public string Text
    {
        get => InnerEntry.Text;
        set
        {
            _suppressMenuUpdate = true;
            InnerEntry.Text = value ?? string.Empty;
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

    /// <summary>Ближайший токен @логин перед каретой (или null): позиция @ и напечатанный кусок.</summary>
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

        if (start < 0 || caret - start < 2)
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
        if (_suppressMenuUpdate)
        {
            _suppressMenuUpdate = false;
            return;
        }

        UpdateMenu();
    }

    private void UpdateMenu()
    {
        var text = InnerEntry.Text ?? string.Empty;
        var caret = Math.Clamp(InnerEntry.CursorPosition, 0, text.Length);
        var token = FindToken(text, caret);

        if (token is null)
        {
            _tokenStart = -1;
            _token = string.Empty;
            HideMenu();
            return;
        }

        var (start, typed) = token.Value;
        var matches = _usernames
            .Where(name => name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(name => new MentionItem { Username = name })
            .ToList();

        _tokenStart = start;
        _token = typed;

        if (matches.Count == 0 || _usernames.Count == 0)
        {
            MentionMenu.IsVisible = false;
            return;
        }

        MentionList.Children.Clear();
        foreach (var item in matches)
        {
            MentionList.Children.Add(CreateItem(item));
        }

        MentionMenu.HeightRequest = Math.Min(240, matches.Count * 42 + 12);
        MentionMenu.IsVisible = true;
    }

    private View CreateItem(MentionItem item)
    {
        var tap = new TapGestureRecognizer { CommandParameter = item.Username };
        tap.Tapped += OnMentionChosen;

        var border = new Border
        {
            Padding = new Thickness(8, 8),
            BackgroundColor = Colors.Transparent,
            GestureRecognizers = { tap }
        };

        border.Content = new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Border
                {
                    WidthRequest = 26,
                    HeightRequest = 26,
                    BackgroundColor = ResolveResource<Color>("Accent", Color.FromArgb("#0088CC")),
                    StrokeShape = new Ellipse(),
                    StrokeThickness = 0,
                    Content = new Label
                    {
                        Text = item.Initial,
                        FontSize = 11,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = ResolveResource<Color>("AccentText", Colors.White),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    }
                },
                new Label
                {
                    Text = item.Username,
                    FontSize = 13,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = ResolveResource<Color>("TextPrimary", Colors.Black),
                    VerticalOptions = LayoutOptions.Center
                }
            }
        };

        return border;
    }

    private void OnMentionChosen(object? sender, EventArgs e)
    {
        var username = (sender as TapGestureRecognizer)?.CommandParameter as string;
        if (string.IsNullOrEmpty(username) || _tokenStart < 0)
        {
            return;
        }

        var text = InnerEntry.Text ?? string.Empty;
        var caret = Math.Clamp(InnerEntry.CursorPosition, 0, text.Length);

        // Токен мог уже не совпадать с сохранённым — не ломаем чужой текст.
        if (_tokenStart >= text.Length)
        {
            HideMenu();
            return;
        }

        var insertion = "@" + username + " ";
        var rebuilt = text[.._tokenStart] + insertion + text[Math.Min(caret, text.Length)..];

        _suppressMenuUpdate = true;
        InnerEntry.Text = rebuilt;
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

    private void OnInnerCompleted(object? sender, EventArgs e)
    {
        HideMenu();
        Completed?.Invoke(this, e);
    }

    private static T ResolveResource<T>(string key, T fallback) where T : class
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is T typed)
        {
            return typed;
        }

        return fallback;
    }
}
