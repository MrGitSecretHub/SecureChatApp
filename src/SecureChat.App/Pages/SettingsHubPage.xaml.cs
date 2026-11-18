using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>
/// Главное меню настроек в стиле Telegram: группы строк с иконкой в цветном
/// круге, названием, подписью и стрелкой. Категория открывает отдельный экран.
/// </summary>
public partial class SettingsHubPage : ContentPage
{
    private sealed record Category(
        string Icon, string Title, string Subtitle, string IconHex, string Section);

    private readonly IReadOnlyList<Category> _categories = new List<Category>
    {
        new("👤", "Профиль", "Имя, аватар, статус, био, ключ", "#4F8EF7", "profile"),
        new("🔐", "Безопасность и приватность",
            "Статус, уведомления, блокировка, код-пароль", "#5FBF6A", "privacy"),
        new("🎨", "Оформление", "Тема, язык, шрифты, размер текста", "#9B6FE8", "appearance"),
        new("🖼", "Обои чата", "Картинка под перепиской и затемнение", "#E8A33D", "wallpaper"),
        new("🌐", "Сервер и подключение", "Проверка сервера, транспорт, TCP", "#3FB8C4", "server"),
        new("💾", "Медиа и память", "Автозагрузка, кэш, история", "#E2705A", "media"),
        new("⚙", "Аккаунт", "Пароль, выход, удаление аккаунта", "#8B8F9A", "account")
    };

    public SettingsHubPage()
    {
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        Root.Children.Clear();

        // Группа 1: пользователь
        Root.Children.Add(MakeGroup("Аккаунт", _categories.Take(2)));
        // Группа 2: внешний вид
        Root.Children.Add(MakeGroup("Внешний вид", _categories.Skip(2).Take(2)));
        // Группа 3: подключение и данные
        Root.Children.Add(MakeGroup("Подключение и данные", _categories.Skip(4)));
    }

    private View MakeGroup(string header, IEnumerable<Category> items)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 0,
            Padding = new Thickness(0, 12, 0, 4)
        };

        var title = new Label
        {
            Text = header,
            Style = (Style)Application.Current.Resources["SectionTitle"],
            Margin = new Thickness(4, 0, 0, 6)
        };
        stack.Children.Add(title);

        var list = new VerticalStackLayout { Spacing = 0 };
        foreach (var item in items)
        {
            list.Children.Add(MakeRow(item));
        }
        stack.Children.Add(list);

        return new Border
        {
            BackgroundColor = (Color)Application.Current!.Resources["Surface"],
            StrokeThickness = 0,
            Padding = new Thickness(2),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Content = stack
        };
    }

    private View MakeRow(Category item)
    {
        var icon = new Border
        {
            WidthRequest = 38,
            HeightRequest = 38,
            BackgroundColor = Color.FromArgb(item.IconHex),
            StrokeThickness = 0,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 }
        };
        icon.Content = new Label
        {
            Text = item.Icon,
            FontSize = 18,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        var title = new Label
        {
            Text = item.Title,
            FontSize = 15,
            FontFamily = (string)Application.Current!.Resources["FontFamilyBold"],
            TextColor = (Color)Application.Current.Resources["TextPrimary"],
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var subtitle = new Label
        {
            Text = item.Subtitle,
            FontSize = 12,
            TextColor = (Color)Application.Current.Resources["TextMuted"],
            LineBreakMode = LineBreakMode.TailTruncation
        };

        var chevron = new Label
        {
            Text = "›",
            FontSize = 24,
            TextColor = (Color)Application.Current.Resources["TextMuted"],
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 12,
            Padding = new Thickness(14, 10),
            MinimumHeightRequest = 62
        };
        grid.Add(icon, 0);
        grid.Add(new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { title, subtitle }
        }, 1);
        grid.Add(chevron, 2);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Open(item.Section);
        grid.GestureRecognizers.Add(tap);

        // разделитель между строками
        var border = new Border
        {
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(14, 0, 0, 0),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 0 }
        };
        border.Content = grid;
        return border;
    }

    private void Open(string section) =>
        _ = Navigation.PushAsync(new SettingsPage(section));
}
