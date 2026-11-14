using SecureChat.App.Services;

namespace SecureChat.App.Pages;

/// <summary>
/// Конструктор оформления: подбор цветов поверх выбранной темы с живым
/// предпросмотром и сохранением собственных тем (в зашифрованном хранилище
/// устройства). Открывается из настроек → «Конструктор темы».
/// </summary>
public partial class ThemeEditorPage : ContentPage
{
    private readonly AppSession _session = AppServices.Get<AppSession>();

    /// <summary>Черновик: переопределения цветов, которые пользователь подбирает.</summary>
    private readonly Dictionary<string, string> _draft = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Тема, которую редактируем (пусто — создаём новую).</summary>
    private string _editingThemeId = string.Empty;

    /// <summary>Развёрнутая строка роли (с палитрой и вводом HEX).</summary>
    private string? _expandedRole;

    /// <summary>Поля ввода HEX по ролям — чтобы «Применить» знал, что введено.</summary>
    private readonly Dictionary<string, Entry> _hexEntries = new(StringComparer.OrdinalIgnoreCase);

    public ThemeEditorPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Пока открыт конструктор, предпросмотр показывает «чистую» тему
        // (встроенную палитру + акцент), а не сохранённую свою тему.
        App.SuppressCustomTheme = true;

        var active = _session.ActiveCustomTheme;
        _draft.Clear();
        if (active is not null)
        {
            _editingThemeId = active.Id;
            foreach (var pair in active.Colors)
            {
                if (ThemeColorRoles.NormalizeKey(pair.Key) is { } key)
                {
                    _draft[key] = pair.Value;
                }
            }
        }
        else
        {
            _editingThemeId = string.Empty;
        }

        _expandedRole = null;
        ApplyPreview();
        BuildRoles();
        BuildMyThemes();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        App.SuppressCustomTheme = false;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
    }

    // ============================== РОЛИ ЦВЕТА ==============================

    /// <summary>Строит строки редактируемых цветов: название, образец и палитра.</summary>
    private void BuildRoles()
    {
        RolesLayout.Children.Clear();

        foreach (var role in ThemeColorRoles.All)
        {
            RolesLayout.Children.Add(BuildRoleRow(role));
        }
    }

    /// <summary>Равные колонки для сетки образцов цвета.</summary>
    private static ColumnDefinitionCollection BuildColumns(int count)
    {
        var columns = new ColumnDefinitionCollection();
        for (var i = 0; i < count; i++)
        {
            columns.Add(new ColumnDefinition(GridLength.Auto));
        }

        return columns;
    }

    private View BuildRoleRow(ThemeColorRole role)
    {
        var title = new Label
        {
            Text = role.Title,
            Style = (Style)Application.Current!.Resources["SectionTitle"]
        };

        var hint = new Label
        {
            Text = role.Description,
            Style = (Style)Application.Current.Resources["Caption"]
        };

        var swatch = new Button
        {
            BackgroundColor = ResolveRoleColor(role.Key),
            WidthRequest = 46,
            HeightRequest = 46,
            Padding = 0,
            BorderWidth = 1,
            BorderColor = (Color)Application.Current.Resources["BorderColor"],
            CornerRadius = 8,
            CommandParameter = role.Key
        };
        swatch.Clicked += OnRoleClicked;

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10
        };
        header.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children = { title, hint }
        }, 0);
        header.Add(swatch, 1);

        // Раскрываемая часть: палитра, поле HEX и сброс роли.
        var presets = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6,
            ColumnDefinitions = BuildColumns(9)
        };

        var presetIndex = 0;
        foreach (var hex in ThemeColorRoles.Presets)
        {
            var color = Color.FromArgb(hex);
            var preset = new Button
            {
                BackgroundColor = color,
                WidthRequest = 30,
                HeightRequest = 30,
                Padding = 0,
                BorderWidth = 0,
                CornerRadius = 15,
                CommandParameter = $"{role.Key}|{hex}"
            };
            preset.Clicked += OnPresetClicked;
            Grid.SetColumn(preset, presetIndex % 9);
            Grid.SetRow(preset, presetIndex / 9);
            presets.Children.Add(preset);
            presetIndex++;
        }

        var hexEntry = new Entry
        {
            Placeholder = "#RRGGBB",
            MaxLength = 7,
            Text = _draft.TryGetValue(role.Key, out var current) ? current : string.Empty,
            WidthRequest = 110,
            TextColor = (Color)Application.Current.Resources["TextPrimary"]
        };
        _hexEntries[role.Key] = hexEntry;

        var applyButton = new Button
        {
            Text = "Применить",
            Style = (Style)Application.Current.Resources["SecondaryButton"],
            CommandParameter = role.Key
        };
        applyButton.Clicked += OnHexApplyClicked;

        var resetButton = new Button
        {
            Text = "Сбросить",
            Style = (Style)Application.Current.Resources["GhostButton"],
            CommandParameter = role.Key
        };
        resetButton.Clicked += OnRoleResetClicked;

        var panel = new VerticalStackLayout
        {
            Spacing = 8,
            IsVisible = _expandedRole == role.Key,
            Children =
            {
                presets,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { hexEntry, applyButton, resetButton }
                }
            }
        };

        var row = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { header, panel }
        };

        return row;
    }

    private void OnRoleClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string key })
        {
            return;
        }

        _expandedRole = _expandedRole == key ? null : key;
        BuildRoles();
    }

    private void OnPresetClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string parameter })
        {
            return;
        }

        var parts = parameter.Split('|', 2);
        if (parts.Length != 2 || ThemeColorRoles.NormalizeKey(parts[0]) is not { } key)
        {
            return;
        }

        _draft[key] = parts[1];
        ApplyPreview();
        BuildRoles();
    }

    private void OnHexApplyClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string key })
        {
            return;
        }

        if (ThemeColorRoles.NormalizeKey(key) is not { } normalized ||
            !_hexEntries.TryGetValue(normalized, out var entry))
        {
            return;
        }

        var text = entry.Text?.Trim() ?? string.Empty;
        if (!ThemeColor.TryParse(text, out var color))
        {
            _ = DisplayAlert("Некорректный цвет", "Укажите цвет в формате #RRGGBB, например #5865F2.", "Ок");
            return;
        }

        _draft[normalized] = ThemeColor.ToHex(color);
        ApplyPreview();
        BuildRoles();
    }

    private void OnRoleResetClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string key })
        {
            return;
        }

        if (ThemeColorRoles.NormalizeKey(key) is not { } normalized)
        {
            return;
        }

        _draft.Remove(normalized);
        ApplyPreview();
        BuildRoles();
    }

    /// <summary>Применяет черновик поверх темы — интерфейс перекрашивается сразу.</summary>
    private void ApplyPreview()
    {
        // Предпросмотр идёт на основе той встроенной темы, на которой сделана
        // редактируемая тема, — так результат совпадает с её применением.
        var baseTheme = _editingThemeId.Length > 0
            ? _session.CustomThemes.FirstOrDefault(item => item.Id == _editingThemeId)?.BaseTheme
            : _session.Theme;

        App.ApplyAppearance(_draft, baseTheme);
        _session.NotifyAppearanceChanged();
    }

    /// <summary>Текущий цвет роли с учётом палитры, акцента и черновика.</summary>
    private static Color ResolveRoleColor(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true &&
            value is Color color)
        {
            return color;
        }

        return Colors.Gray;
    }

    // ============================== СОХРАНЕНИЕ ==============================

    private async void OnSaveThemeClicked(object? sender, EventArgs e)
    {
        if (_draft.Count == 0)
        {
            await DisplayAlert("Нет изменений",
                "Сначала поменяйте хотя бы один цвет в разделе «Цвета».", "Ок");
            return;
        }

        var defaultName = string.IsNullOrEmpty(_editingThemeId)
            ? $"Моя тема {DateTime.Now:dd.MM HH:mm}"
            : _session.CustomThemes.FirstOrDefault(item => item.Id == _editingThemeId)?.Name ?? "Моя тема";

        var name = await DisplayPromptAsync(
            "Название темы",
            "Как назвать вашу тему?",
            initialValue: defaultName,
            maxLength: 32);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();

        var existing = _session.CustomThemes.FirstOrDefault(item => item.Id == _editingThemeId);
        if (existing is null)
        {
            existing = new CustomTheme { Name = name };
            _session.CustomThemes.Add(existing);
        }

        existing.Name = name;
        existing.BaseTheme = _session.Theme;
        existing.Colors = new Dictionary<string, string>(_draft, StringComparer.OrdinalIgnoreCase);
        _editingThemeId = existing.Id;
        _session.ActiveCustomThemeId = existing.Id;

        // Своя тема опирается на конкретную встроенную тему — возвращаем её основу.
        if (ThemeCatalog.Resolve(existing.BaseTheme).Name == existing.BaseTheme)
        {
            _session.Theme = existing.BaseTheme;
        }

        // Черновик уже применён — сохраняем его как постоянную тему.
        App.SuppressCustomTheme = false;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();

        BuildMyThemes();
        await DisplayAlert("Тема сохранена", $"«{name}» применена и сохранена на этом устройстве.", "Ок");
    }

    private async void OnResetColorsClicked(object? sender, EventArgs e)
    {
        if (_draft.Count == 0)
        {
            return;
        }

        var confirmed = await DisplayAlert(
            "Сбросить цвета",
            "Все подобранные цвета будут сброшены к выбранной теме. Сама сохранённая тема не изменится.",
            "Сбросить", "Отмена");

        if (!confirmed)
        {
            return;
        }

        _draft.Clear();
        ApplyPreview();
        BuildRoles();
    }

    // ============================== МОИ ТЕМЫ ==============================

    private void BuildMyThemes()
    {
        MyThemesLayout.Children.Clear();

        var themes = _session.CustomThemes
            .OrderByDescending(item => item.Name.StartsWith("Моя тема", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        EmptyThemesLabel.IsVisible = themes.Count == 0;

        foreach (var theme in themes)
        {
            MyThemesLayout.Children.Add(BuildThemeRow(theme));
        }
    }

    private View BuildThemeRow(CustomTheme theme)
    {
        var isActive = string.Equals(theme.Id, _session.ActiveCustomThemeId, StringComparison.OrdinalIgnoreCase);

        var name = new Label
        {
            Text = isActive ? $"{theme.Name}  (применена)" : theme.Name,
            Style = (Style)Application.Current!.Resources["SectionTitle"]
        };

        var dots = new HorizontalStackLayout { Spacing = 4 };

        foreach (var key in new[] { "PageBackground", "Surface", "Accent", "MessageMe", "MessageThem" })
        {
            if (!theme.Colors.TryGetValue(key, out var hex) || !ThemeColor.TryParse(hex, out var color))
            {
                continue;
            }

            dots.Children.Add(new Border
            {
                BackgroundColor = color,
                WidthRequest = 18,
                HeightRequest = 18,
                StrokeThickness = 1,
                Stroke = (Color)Application.Current.Resources["BorderColor"],
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 9 }
            });
        }

        var applyButton = new Button
        {
            Text = "Применить",
            Style = (Style)Application.Current.Resources["SecondaryButton"],
            IsEnabled = !isActive,
            CommandParameter = theme.Id
        };
        applyButton.Clicked += OnApplyThemeClicked;

        var deleteButton = new Button
        {
            Text = "Удалить",
            Style = (Style)Application.Current.Resources["GhostButton"],
            TextColor = (Color)Application.Current.Resources["Danger"],
            CommandParameter = theme.Id
        };
        deleteButton.Clicked += OnDeleteThemeClicked;

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10
        };

        row.Add(new VerticalStackLayout
        {
            Spacing = 4,
            Children = { name, dots }
        }, 0);

        row.Add(new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { applyButton, deleteButton }
        }, 1);

        return row;
    }

    private async void OnApplyThemeClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string id })
        {
            return;
        }

        var theme = _session.CustomThemes.FirstOrDefault(item => item.Id == id);
        if (theme is null)
        {
            return;
        }

        _session.ActiveCustomThemeId = theme.Id;
        _editingThemeId = theme.Id;

        // Восстанавливаем встроенную тему, на которой сделана своя тема.
        if (ThemeCatalog.Resolve(theme.BaseTheme).Name == theme.BaseTheme)
        {
            _session.Theme = theme.BaseTheme;
        }

        _draft.Clear();
        foreach (var pair in theme.Colors)
        {
            if (ThemeColorRoles.NormalizeKey(pair.Key) is { } key)
            {
                _draft[key] = pair.Value;
            }
        }

        App.SuppressCustomTheme = false;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();

        BuildMyThemes();
        BuildRoles();
    }

    private async void OnDeleteThemeClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string id })
        {
            return;
        }

        var theme = _session.CustomThemes.FirstOrDefault(item => item.Id == id);
        if (theme is null)
        {
            return;
        }

        var confirmed = await DisplayAlert(
            "Удалить тему",
            $"Тема «{theme.Name}» будет удалена с этого устройства.",
            "Удалить", "Отмена");

        if (!confirmed)
        {
            return;
        }

        _session.CustomThemes.Remove(theme);
        if (string.Equals(_session.ActiveCustomThemeId, theme.Id, StringComparison.OrdinalIgnoreCase))
        {
            _session.ActiveCustomThemeId = string.Empty;
            if (string.Equals(_editingThemeId, theme.Id, StringComparison.OrdinalIgnoreCase))
            {
                _editingThemeId = string.Empty;
                _draft.Clear();
            }
        }

        App.SuppressCustomTheme = false;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
        await App.SaveAppearanceAsync();

        BuildMyThemes();
        BuildRoles();
    }
}
