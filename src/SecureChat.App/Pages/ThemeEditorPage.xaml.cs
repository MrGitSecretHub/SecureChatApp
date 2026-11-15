using SecureChat.App.Services;
using SecureChat.Core.Text;

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

    /// <summary>Снимок черновика на момент последнего сохранения — для проверки
    /// несохранённых изменений.</summary>
    private string _savedFingerprint = string.Empty;

    public ThemeEditorPage()
    {
        InitializeComponent();
    }

    /// <summary>Отпечаток черновика: отсортированный список «роль=цвет».</summary>
    private string Fingerprint() =>
        string.Join(";", _draft.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));

    /// <summary>Есть ли несохранённые изменения.</summary>
    private bool HasUnsavedChanges =>
        !string.Equals(Fingerprint(), _savedFingerprint, StringComparison.Ordinal);

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
        BuildPalettePresets();
        BuildMyThemes();
        UpdateHeader();
        _savedFingerprint = Fingerprint();
        UpdateUnsavedHint();
    }

    /// <summary>Обновляет шапку: как называется редактируемая тема и на чём она
    /// основана.</summary>
    private void UpdateHeader()
    {
        var theme = _session.CustomThemes.FirstOrDefault(item => item.Id == _editingThemeId);
        EditorTitleLabel.Text = theme is null ? "Новая тема" : theme.Name;
        EditorBaseLabel.Text = theme is null
            ? "Подберите цвета — тема сохранится на этом устройстве"
            : $"основа: {theme.BaseTheme} · правка сохранённой темы";
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        App.SuppressCustomTheme = false;
        App.ApplyAppearance();
        _session.NotifyAppearanceChanged();
    }

    /// <summary>Предупреждает о несохранённых цветах при выходе из конструктора.</summary>
    protected override bool OnBackButtonPressed()
    {
        if (!HasUnsavedChanges)
        {
            return base.OnBackButtonPressed();
        }

        _ = ConfirmLeaveAsync();
        return true;
    }

    /// <summary>Кнопка «✕» в шапке — выход с проверкой несохранённых изменений.</summary>
    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        if (HasUnsavedChanges)
        {
            await ConfirmLeaveAsync();
            return;
        }

        await Navigation.PopAsync();
    }

    /// <summary>Спрашивает подтверждение выхода и уходит со страницы, если выход
    /// подтверждён.</summary>
    private async Task ConfirmLeaveAsync()
    {
        var confirmed = await DisplayAlert(
            "Выйти без сохранения?",
            "Подобранные цвета не сохранены — они пропадут. Сохранённая тема останется как была.",
            "Выйти", "Остаться");

        if (!confirmed)
        {
            return;
        }

        _savedFingerprint = Fingerprint();
        await Navigation.PopAsync();
    }

    /// <summary>Показывает подсказку о несохранённых изменениях.</summary>
    private void UpdateUnsavedHint()
    {
        UnsavedHint.Text = HasUnsavedChanges
            ? "Есть несохранённые изменения — нажмите «Сохранить как свою тему»."
            : string.Empty;

        UnsavedHint.IsVisible = HasUnsavedChanges;
    }

    // ============================== РОЛИ ЦВЕТА ==============================

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

    /// <summary>Строит строки редактируемых цветов: по группам, с названием,
    /// HEX-значением, образцом и палитрой.</summary>
    private void BuildRoles()
    {
        RolesLayout.Children.Clear();

        foreach (var group in ThemeColorRoles.Groups)
        {
            var roles = ThemeColorRoles.All
                .Where(role => group.Roles.Contains(role.Key, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (roles.Count == 0)
            {
                continue;
            }

            RolesLayout.Children.Add(new Label
            {
                Text = group.Title,
                Style = (Style)Application.Current!.Resources["SectionTitle"],
                Margin = new Thickness(0, 4, 0, 0)
            });

            foreach (var role in roles)
            {
                RolesLayout.Children.Add(BuildRoleRow(role));
            }
        }

        ChangedRolesLabel.Text = _draft.Count == 0
            ? string.Empty
            : $"изменено ролей: {_draft.Count}";
    }

    /// <summary>Строит плитки готовых палитр: образцы цветов и название.</summary>
    private void BuildPalettePresets()
    {
        PalettePresetsLayout.Children.Clear();

        foreach (var palette in ThemePalettePresets.All)
        {
            var dots = new HorizontalStackLayout { Spacing = 3 };

            foreach (var key in new[]
                     {
                         ThemeColorKeys.PageBackground, ThemeColorKeys.Surface,
                         ThemeColorKeys.Accent, ThemeColorKeys.MessageMe,
                         ThemeColorKeys.MessageThem, ThemeColorKeys.TextPrimary
                     })
            {
                if (!palette.Colors.TryGetValue(key, out var hex) ||
                    !ThemeColor.TryParse(hex, out var color))
                {
                    continue;
                }

                dots.Children.Add(new Border
                {
                    BackgroundColor = color,
                    WidthRequest = 20,
                    HeightRequest = 20,
                    StrokeThickness = 1,
                    Stroke = (Color)Application.Current!.Resources["BorderColor"],
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 }
                });
            }

            var title = new Label
            {
                Text = palette.Name,
                Style = (Style)Application.Current.Resources["SectionTitle"]
            };

            var hint = new Label
            {
                Text = palette.Description,
                Style = (Style)Application.Current.Resources["Caption"],
                TextColor = (Color)Application.Current.Resources["TextMuted"]
            };

            var paletteName = palette.Name;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ApplyPalettePreset(paletteName);

            var card = new Border
            {
                BackgroundColor = (Color)Application.Current!.Resources["SurfaceAlt"],
                Stroke = (Color)Application.Current.Resources["BorderColor"],
                StrokeThickness = 1,
                Padding = new Thickness(10, 8),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 }
            };
            card.GestureRecognizers.Add(tap);
            card.Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children = { dots, title }
                    },
                    hint
                }
            };

            PalettePresetsLayout.Children.Add(card);
        }
    }

    /// <summary>Открывает полный HSV-пикер для роли и применяет выбранный цвет.</summary>
    private async void OnOpenPickerClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string key })
        {
            return;
        }

        if (ThemeColorRoles.NormalizeKey(key) is not { } normalized)
        {
            return;
        }

        var role = ThemeColorRoles.All.First(item =>
            string.Equals(item.Key, normalized, StringComparison.OrdinalIgnoreCase));

        var current = ResolveRoleColor(normalized);
        var picker = new ColorPickerPage(current, role.Title);
        await Navigation.PushModalAsync(picker);

        if (picker.SelectedColor is not { } chosen)
        {
            return;
        }

        _draft[normalized] = ThemeColor.ToHex(chosen);
        ApplyPreview();
        BuildRoles();
    }

    /// <summary>Применяет готовую палитру: все роли получают её цвета.</summary>
    private async void ApplyPalettePreset(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        var palette = ThemePalettePresets.All
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (palette is null)
        {
            return;
        }

        palette.ApplyTo(_draft);
        ApplyPreview();
        BuildRoles();

        await DisplayAlert("Палитра применена",
            $"«{palette.Name}» — цвета применены ко всем ролям. Их можно править по отдельности ниже.",
            "Ок");
    }

    private View BuildRoleRow(ThemeColorRole role)
    {
        var isChanged = _draft.ContainsKey(role.Key);
        var roleColor = ResolveRoleColor(role.Key);

        var title = new Label
        {
            Text = role.Title,
            Style = (Style)Application.Current!.Resources["SectionTitle"]
        };

        var hint = new Label
        {
            Text = isChanged ? $"{role.Description} · изменено" : role.Description,
            Style = (Style)Application.Current.Resources["Caption"],
            TextColor = isChanged
                ? (Color)Application.Current.Resources["Accent"]
                : (Color)Application.Current.Resources["TextMuted"]
        };

        // HEX текущего цвета — видно, какой именно оттенок стоит.
        var hexLabel = new Label
        {
            Text = ThemeColor.ToHex(roleColor),
            Style = (Style)Application.Current.Resources["Caption"],
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.End
        };

        var swatch = new Button
        {
            BackgroundColor = roleColor,
            WidthRequest = 46,
            HeightRequest = 46,
            Padding = 0,
            BorderWidth = isChanged ? 2 : 1,
            BorderColor = isChanged
                ? (Color)Application.Current.Resources["Accent"]
                : (Color)Application.Current.Resources["BorderColor"],
            CornerRadius = 8,
            CommandParameter = role.Key
        };
        swatch.Clicked += OnRoleClicked;

        // Кнопка полного пикера (HSV) — для точного подбора оттенка.
        var pickerButton = new Button
        {
            Text = "🎯",
            Style = (Style)Application.Current.Resources["GhostButton"],
            Padding = 0,
            WidthRequest = 36,
            HeightRequest = 36,
            FontSize = 16,
            VerticalOptions = LayoutOptions.Center,
            CommandParameter = role.Key
        };
        pickerButton.Clicked += OnOpenPickerClicked;

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10
        };
        header.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children = { title, hint }
        }, 0);
        header.Add(hexLabel, 1);
        header.Add(swatch, 2);
        header.Add(pickerButton, 3);

        // Раскрываемая часть: палитра по семействам, поле HEX и сброс роли.
        var swatchPanel = new VerticalStackLayout { Spacing = 8 };

        foreach (var family in ThemeColorRoles.SwatchFamilies)
        {
            swatchPanel.Children.Add(new Label
            {
                Text = family.Title,
                Style = (Style)Application.Current!.Resources["Caption"],
                Margin = new Thickness(0, 2, 0, 0)
            });

            var swatches = new Grid
            {
                ColumnSpacing = 6,
                RowSpacing = 6,
                ColumnDefinitions = BuildColumns(10)
            };

            var index = 0;
            foreach (var hex in family.Colors)
            {
                var color = Color.FromArgb(hex);
                var preset = new Button
                {
                    BackgroundColor = color,
                    WidthRequest = 28,
                    HeightRequest = 28,
                    Padding = 0,
                    BorderWidth = 0,
                    CornerRadius = 14,
                    CommandParameter = $"{role.Key}|{hex}"
                };
                preset.Clicked += OnPresetClicked;
                Grid.SetColumn(preset, index % 10);
                Grid.SetRow(preset, index / 10);
                swatches.Children.Add(preset);
                index++;
            }

            swatchPanel.Children.Add(swatches);
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
                swatchPanel,
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

        PreviewBaseLabel.Text = $"основа: {baseTheme}";

        App.ApplyAppearance(_draft, baseTheme);
        _session.NotifyAppearanceChanged();
        UpdateUnsavedHint();
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
            "Сохранить",
            "Отмена",
            maxLength: 32,
            initialValue: defaultName);

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
        _savedFingerprint = Fingerprint();
        UpdateUnsavedHint();
        await DisplayAlert("Тема сохранена", $"«{name}» применена и сохранена на этом устройстве.", "Ок");
    }

    /// <summary>Копирует текущую палитру в буфер обмена — можно передать её
    /// сообщением в чате или вставить на другом устройстве.</summary>
    private async void OnExportThemeClicked(object? sender, EventArgs e)
    {
        if (_draft.Count == 0)
        {
            await DisplayAlert("Нечего копировать",
                "Сначала поменяйте хотя бы один цвет в разделе «Цвета».", "Ок");
            return;
        }

        var name = _session.CustomThemes.FirstOrDefault(item => item.Id == _editingThemeId)?.Name
            ?? $"Моя тема {DateTime.Now:dd.MM HH:mm}";

        var transfer = new ThemeTransfer
        {
            Name = name,
            BaseTheme = _session.Theme,
            Colors = new Dictionary<string, string>(_draft, StringComparer.OrdinalIgnoreCase)
        };

        try
        {
            await Clipboard.Default.SetTextAsync(transfer.ToJson());
            await DisplayAlert("Тема скопирована",
                "Текст темы в буфере обмена — вставьте его в чат, чтобы передать другим, или в поле «Вставить тему» на другом устройстве.",
                "Ок");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Не удалось скопировать", ex.Message, "Ок");
        }
    }

    /// <summary>Вставляет тему из буфера обмена.</summary>
    private async void OnImportThemeClicked(object? sender, EventArgs e)
    {
        string text;
        try
        {
            text = await Clipboard.Default.GetTextAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Не удалось прочитать буфер", ex.Message, "Ок");
            return;
        }

        var transfer = ThemeTransfer.Parse(text);
        if (transfer is null)
        {
            await DisplayAlert("Это не тема",
                "В буфере обмена нет текста темы. Скопируйте тему на другом устройстве и попробуйте снова.",
                "Ок");
            return;
        }

        _draft.Clear();
        foreach (var pair in transfer.Colors)
        {
            _draft[pair.Key] = pair.Value;
        }

        // Основа импортированной темы — та же, на которой её делали.
        if (ThemeCatalog.Resolve(transfer.BaseTheme).Name == transfer.BaseTheme)
        {
            _session.Theme = transfer.BaseTheme;
        }

        _editingThemeId = string.Empty;
        ApplyPreview();
        BuildRoles();
        BuildMyThemes();

        await DisplayAlert("Тема загружена",
            $"«{transfer.Name}» загружена в редактор. Нажмите «Сохранить как свою тему», чтобы оставить её на этом устройстве.",
            "Ок");
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

        var editButton = new Button
        {
            Text = "Править",
            Style = (Style)Application.Current.Resources["SecondaryButton"],
            CommandParameter = theme.Id
        };
        editButton.Clicked += OnEditThemeClicked;

        var renameButton = new Button
        {
            Text = "✎",
            Style = (Style)Application.Current.Resources["GhostButton"],
            Padding = new Thickness(8, 0),
            WidthRequest = 34,
            CommandParameter = theme.Id
        };
        renameButton.Clicked += OnRenameThemeClicked;

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
            Children = { applyButton, editButton, renameButton, deleteButton }
        }, 1);

        return row;
    }

    /// <summary>Загружает сохранённую тему в редактор для правки.</summary>
    private void OnEditThemeClicked(object? sender, EventArgs e)
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

        _editingThemeId = theme.Id;
        _draft.Clear();
        foreach (var pair in theme.Colors)
        {
            if (ThemeColorRoles.NormalizeKey(pair.Key) is { } key)
            {
                _draft[key] = pair.Value;
            }
        }

        ApplyPreview();
        BuildRoles();
        UpdateHeader();
    }

    /// <summary>Переименовывает сохранённую тему.</summary>
    private async void OnRenameThemeClicked(object? sender, EventArgs e)
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

        var name = await DisplayPromptAsync(
            "Переименовать тему",
            "Как назвать тему?",
            "Сохранить",
            "Отмена",
            maxLength: 32,
            initialValue: theme.Name);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        theme.Name = name.Trim();
        await App.SaveAppearanceAsync();
        BuildMyThemes();
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
