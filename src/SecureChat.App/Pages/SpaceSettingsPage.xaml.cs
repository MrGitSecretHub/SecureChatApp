using SecureChat.Core.Net;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>Настройки пространства: название, описание, участники, роли, удаление.</summary>
public partial class SpaceSettingsPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ChatService _chatService = AppServices.Get<ChatService>();
    private readonly ObservableCollection<SpaceMemberVm> _members = new();
    private readonly string _spaceId;
    private readonly string _spaceType;
    private SpaceDto? _space;
    private bool _isAdmin;
    private string? _currentWallpaperPath;
    private bool _currentWallpaperDim;

    public SpaceSettingsPage(string spaceId, string spaceName, string spaceType)
    {
        InitializeComponent();
        _spaceId = spaceId;
        _spaceType = spaceType;

        Title = string.Format(Loc.T("rs_079"), spaceName);
        SpaceNameLabel.Text = spaceName;
        SpaceIconLabel.Text = spaceType switch
        {
            SpaceTypes.Channel => "📢",
            SpaceTypes.Community => "🌐",
            SpaceTypes.Forum => "🧵",
            _ => "👥"
        };

        MembersView.ItemsSource = _memberRows;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var spaces = await _spaces.ListAsync();
            _space = spaces.FirstOrDefault(item => item.Id == _spaceId);

            if (_space is not null)
            {
                SpaceNameLabel.Text = _space.Name;
                SpaceTypeLabel.Text = _space.Type switch
                {
                    SpaceTypes.Channel => "Канал — объявления",
                    SpaceTypes.Community => "Сообщество",
                    SpaceTypes.Forum => "Форум — обсуждения по темам",
                    _ => "Группа"
                };

                NameEntry.Text = _space.Name;
                DescriptionEditor.Text = _space.Description;

                var isAdmin = _space.MyRole is SpaceRoles.Owner or SpaceRoles.Admin;
                _isAdmin = isAdmin;
                DistributeKeyButton.IsVisible = isAdmin;
                NameEntry.IsEnabled = isAdmin;
                DescriptionEditor.IsEnabled = isAdmin;

                PublicRow.IsVisible = isAdmin;
                PublicSwitch.IsToggled = _space.IsPublic;
                HiddenRow.IsVisible = isAdmin;
                RotateKeyButton.IsVisible = isAdmin;
                HiddenSwitch.IsToggled = _space.IsHidden;
                SignRow.IsVisible = isAdmin && _spaceType == SpaceTypes.Channel;
                SignSwitch.IsToggled = _space.SignMessages;
                DeleteButton.IsVisible = _space.MyRole == SpaceRoles.Owner;
            }

            var members = await _spaces.GetMembersAsync(_spaceId);
            _members.Clear();
            foreach (var member in members)
            {
                _members.Add(SpaceMemberVm.From(member));
            }

            MembersHintLabel.Text = _members.Count > 0
                ? string.Format(Loc.T("rs_001"), _members.Count)
                : Loc.T("rs_130", "Пока нет участников.");

            // Секции по ролям, как в Telegram: владелец, администраторы, участники, гости.
            RebuildMemberRows();
            ErrorLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            // Ошибку связи показываем тихо, без модального окна.
            if (ConnectionErrors.IsConnectionProblem(ex.Message))
            {
                ErrorLabel.Text = $"⚡ {ex.Message}";
                ErrorLabel.IsVisible = true;
                return;
            }

            await this.ShowErrorAsync(ex);
        }

        // Загрузка текущих обоев пространства
        var session = AppServices.Get<AppSession>();
        _currentWallpaperPath = await session.GetSpaceWallpaperPathAsync(_spaceId);
        _currentWallpaperDim = await session.GetSpaceWallpaperDimAsync(_spaceId);
        UpdateWallpaperPreview();
        SpaceWallpaperDimSwitch.IsToggled = _currentWallpaperDim;
    }

    private async void OnRotateKeyClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("s_key_rotate", "Сменить ключ пространства"),
            Loc.T("s_key_rotate_warn", "Новый ключ получат все участники. Старые сообщения останутся доступными только локально. Продолжить?"),
            Loc.T("common_yes", "Да"), Loc.T("common_no", "Нет"));
        if (!confirmed) return;
        var ok = await AppServices.Get<SpaceService>().RotateKeyAsync(_spaceId);
        await DisplayAlert(
            Loc.T("s_key_rotate", "Сменить ключ пространства"),
            ok ? Loc.T("s_key_rotated", "Ключ сменён.") : Loc.T("s_key_rotate_fail", "Не удалось сменить ключ."),
            "OK");
    }

    private async void OnDistributeKeyClicked(object sender, EventArgs e)
    {
        if (!_isAdmin) return;
        var count = await _spaces.EnsureKeyDistributedAsync(_spaceId);
        await DisplayAlert(
            "Раздача ключа",
            count > 0
                ? $"Ключ выдан {count} участникам. Теперь они смогут прочитать историю."
                : "Всем участникам ключ уже выдан.",
            "OK");
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_018", "Название не может быть пустым."), "OK");
            return;
        }

        try
        {
            var canEdit = _space?.MyRole is SpaceRoles.Owner or SpaceRoles.Admin;
            var updated = await _spaces.UpdateAsync(
                _spaceId,
                name,
                DescriptionEditor.Text ?? string.Empty,
                isPublic: canEdit ? PublicSwitch.IsToggled : null,
                isHidden: canEdit ? HiddenSwitch.IsToggled : null,
                signMessages: canEdit && _spaceType == SpaceTypes.Channel
                    ? SignSwitch.IsToggled
                    : null);
            if (updated is not null)
            {
                await DisplayAlert(Loc.T("set_036", "Готово"), Loc.T("uc_019", "Настройки пространства сохранены."), "OK");
                await LoadAsync();
            }
            else
            {
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_048", "Недостаточно прав для изменения."), "OK");
            }
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnAddMemberClicked(object sender, EventArgs e)
    {
        var username = await DisplayPromptAsync(
            Loc.T("rs_041", "Добавить участника"),
            Loc.T("rs_016", "Введите логин пользователя:"),
            Loc.T("rs_135", "Пригласить"),
            Loc.T("common_cancel", "Отмена"),
            maxLength: 32,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        try
        {
            var result = await _spaces.InviteMemberAsync(_spaceId, username);

            if (result.Success)
            {
                await DisplayAlert(
                    Loc.T("set_036", "Готово"),
                    string.Format(Loc.T("dg_004"), result.Username ?? username.Trim()) +
                    Loc.T("dg_087", "Чтобы увидеть его, участнику нужно обновить список пространств."),
                    "OK");
                await LoadAsync();
                return;
            }

            var reason = result.Error switch
            {
                "user_not_found" => string.Format(Loc.T("rs_131"), username.Trim()),
                "already_member" => "Этот пользователь уже состоит в пространстве.",
                "no_rights" => "Приглашать участников может только владелец или администратор.",
                "space_not_found" => "Пространство не найдено или у вас нет к нему доступа.",
                "key_unavailable" => "Не удалось получить ключ пространства для приглашения.",
                "key_save_failed" => "Участник добавлен, но ключ ему не доставлен. Попробуйте ещё раз.",
                "not_authorized" => "Сессия не активна. Войдите в аккаунт заново.",
                _ => "Не удалось добавить участника."
            };

            await DisplayAlert(Loc.T("uc_020", "Не получилось"), reason, "OK");
        }
        catch (ApiException ex)
        {
            await DisplayAlert(Loc.T("uc_020", "Не получилось"), ex.Message, "OK");
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (message.Contains("Failed to invoke", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Method does not exist", StringComparison.OrdinalIgnoreCase))
            {
                message = Loc.T("rs_143", "Сервер запущен в старой версии. Перезапустите SecureChat.Server.exe и повторите приглашение.");
            }

            await DisplayAlert(Loc.T("common_error", "Ошибка"), message, "OK");
        }
    }

    private readonly ObservableCollection<MemberRow> _memberRows = new();

    /// <summary>
    /// Плоский список с заголовками секций: группировка CollectionView на Windows
    /// отрабатывает нестабильно, поэтому секции — обычные строки.
    /// </summary>
    private void RebuildMemberRows()
    {
        _memberRows.Clear();

        void AddSection(string name, params string[] roles)
        {
            var items = _members
                .Where(m => roles.Any(role => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (items.Count == 0)
            {
                return;
            }

            _memberRows.Add(MemberRow.HeaderRow(name));
            foreach (var item in items)
            {
                _memberRows.Add(MemberRow.MemberRowOf(item));
            }
        }

        AddSection(Loc.T("rs_021", "Владелец"), SpaceRoles.Owner);
        AddSection(Loc.T("rs_012", "Администраторы"), SpaceRoles.Admin);
        AddSection("Участники", SpaceRoles.Member);
        AddSection(Loc.T("rs_038", "Гости (только чтение)"), SpaceRoles.Guest);

        // Неизвестные серверные роли — в «Участники», чтобы люди не терялись.
        var rest = _members
            .Where(m => m.Role is not (SpaceRoles.Owner or SpaceRoles.Admin or SpaceRoles.Member or SpaceRoles.Guest))
            .OrderBy(m => m.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rest.Count > 0)
        {
            _memberRows.Add(MemberRow.HeaderRow("Участники"));
            foreach (var item in rest)
            {
                _memberRows.Add(MemberRow.MemberRowOf(item));
            }
        }
    }

    private async void OnMemberTapped(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not MemberRow row)
        {
            return;
        }

        MembersView.SelectedItem = null;

        // Заголовки секций не выбираются.
        if (row.Member is not { } member)
        {
            return;
        }

            var isAdmin = _space?.MyRole is SpaceRoles.Owner or SpaceRoles.Admin;

        var actions = new List<string> { Loc.T("rs_198", "👤 Открыть профиль") };
            if (isAdmin)
            {
                actions.Add(Loc.T("rs_054", "Изменить роль"));
                actions.Add(member.CanPost ? Loc.T("rs_213", "🔇 Запретить писать") : Loc.T("rs_191", "✏️ Разрешить писать"));
                actions.Add(member.IsMuted ? Loc.T("rs_214", "🔊 Снять запрет записи") : Loc.T("rs_186", "⏱ Запрет записи на время…"));
                actions.Add(Loc.T("rs_162", "Удалить из пространства"));
            }

            var choice = await DisplayActionSheet(
                member.Username, Loc.T("common_cancel", "Отмена"), null, actions.ToArray());

            if (string.IsNullOrWhiteSpace(choice) || choice == Loc.T("common_cancel", "Отмена"))
            {
                return;
            }

            if (choice == Loc.T("rs_198", "👤 Открыть профиль"))
            {
                try
                {
                    await Navigation.PushAsync(new UserProfilePage(member.Username));
                }
                catch (Exception ex)
                {
                    await this.ShowErrorAsync(ex);
                }

                return;
            }

            if (member.Role == SpaceRoles.Owner)
            {
                await DisplayAlert(member.Username, Loc.T("uc_068", "Это владелец пространства — его роль не меняют."), "OK");
                return;
            }

            if (!isAdmin)
            {
                await DisplayAlert(
                    member.Username,
                    $"{member.RoleText} · {member.CanPostText}\nИзменять состав могут владелец и администраторы.",
                    "OK");
                return;
            }

        try
        {
            if (choice == Loc.T("rs_162", "Удалить из пространства"))
            {
                await _spaces.RemoveMemberAsync(_spaceId, member.UserId);
            }
            else if (choice == Loc.T("rs_054", "Изменить роль"))
            {
                var role = await DisplayActionSheet(
                    string.Format(Loc.T("dg_057"), member.Username),
                    Loc.T("common_cancel", "Отмена"),
                    null,
                    Loc.T("dg_082", "Участник"),
                    Loc.T("dg_023", "Гость (только чтение)"),
                    Loc.T("dg_007", "Администратор"));

                if (string.IsNullOrWhiteSpace(role) || role == Loc.T("common_cancel", "Отмена"))
                {
                    return;
                }

                var newRole = role switch
                {
                    "Администратор" => SpaceRoles.Admin,
                    "Гость (только чтение)" => SpaceRoles.Guest,
                    _ => SpaceRoles.Member
                };

                // Смена роли, как и раньше, задаёт право по умолчанию;
                // тонкая настройка — отдельным пунктом «Разрешить/Запретить писать».
                await _spaces.UpdateMemberAsync(
                    _spaceId, member.UserId, newRole, newRole != SpaceRoles.Guest);
            }
            else if (choice.StartsWith(Loc.T("rs_212", "🔇 Запретить")) || choice.StartsWith(Loc.T("rs_190", "✏️ Разрешить")))
            {
                // Право писать отдельно от роли: роль не трогаем.
                await _spaces.UpdateMemberAsync(
                    _spaceId, member.UserId, canPost: !member.CanPost);
            }
            else if (choice == Loc.T("rs_214", "🔊 Снять запрет записи"))
            {
                await _spaces.UnmuteMemberAsync(_spaceId, member.UserId);
            }
            else if (choice == Loc.T("rs_186", "⏱ Запрет записи на время…"))
            {
                var duration = await DisplayActionSheet(
                    string.Format(Loc.T("dg_025"), member.Username),
                    Loc.T("common_cancel", "Отмена"),
                    null,
                    Loc.T("dg_002", "1 час"),
                    Loc.T("dg_000", "1 день"),
                    Loc.T("dg_001", "1 неделя"));

                var hours = duration switch
                {
                    "1 час" => 1,
                    "1 день" => 24,
                    "1 неделя" => 24 * 7,
                    _ => 0,
                };
                if (hours > 0)
                {
                    await _spaces.MuteMemberAsync(
                        _spaceId, member.UserId, DateTimeOffset.UtcNow.AddHours(hours));
                }
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnLeaveClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("dg_051", "Покинуть пространство?"),
            Loc.T("dg_018", "Вы перестанете получать сообщения этого пространства."),
            Loc.T("dg_050", "Покинуть"),
            Loc.T("common_cancel", "Отмена"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _spaces.LeaveAsync(_spaceId);
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnDeleteClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("dg_077", "Удалить пространство?"),
            Loc.T("dg_008", "Будут удалены все сообщения, темы и список участников."),
            Loc.T("common_delete", "Удалить"),
            Loc.T("common_cancel", "Отмена"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            if (await _spaces.DeleteSpaceAsync(_spaceId))
            {
                await Navigation.PopAsync();
            }
            else
            {
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_049", "Недостаточно прав для удаления."), "OK");
            }
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    // ============================== ОБОИ ПРОСТРАНСТВА ==============================

    private async void UpdateWallpaperPreview()
    {
        var session = AppServices.Get<AppSession>();
        var path = await session.GetSpaceWallpaperPathAsync(_spaceId);
        SpaceWallpaperPreviewImage.Source = string.IsNullOrEmpty(path)
            ? null
            : ImageSource.FromFile(path);
        SpaceWallpaperPreview.IsVisible = !string.IsNullOrEmpty(path);
        SpaceWallpaperEmptyLabel.IsVisible = string.IsNullOrEmpty(path);
        SpaceWallpaperRemoveButton.IsVisible = !string.IsNullOrEmpty(path);
    }

    private async void OnSpaceWallpaperGalleryClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Loc.T("rs_030", "Выберите обои"),
                FileTypes = FilePickerFileType.Images
            });
            if (result is null)
            {
                return;
            }

            await SaveWallpaperAsync(result);
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnSpaceWallpaperFileClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Loc.T("rs_033", "Выберите файл обоев")
            });
            if (result is null)
            {
                return;
            }

            await SaveWallpaperAsync(result);
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnSpaceWallpaperRemoveClicked(object sender, EventArgs e)
    {
        var session = AppServices.Get<AppSession>();
        await session.RemoveSpaceWallpaperAsync(_spaceId);
        _currentWallpaperPath = string.Empty;
        _currentWallpaperDim = true;
        SpaceWallpaperDimSwitch.IsToggled = _currentWallpaperDim;
        UpdateWallpaperPreview();
    }

    private async void OnSpaceWallpaperDimToggled(object sender, ToggledEventArgs e)
    {
        var session = AppServices.Get<AppSession>();
        var currentPath = await session.GetSpaceWallpaperPathAsync(_spaceId);
        if (string.IsNullOrEmpty(currentPath))
        {
            return;
        }

        await session.SetSpaceWallpaperAsync(_spaceId, currentPath, e.Value);
        _currentWallpaperDim = e.Value;
        UpdateWallpaperPreview();
    }

    private async Task SaveWallpaperAsync(FileResult result)
    {
        try
        {
            var ext = Path.GetExtension(result.FullPath);
            var fileName = $"wallpaper.{_spaceId}{ext}";
            var targetPath = Path.Combine(FileSystem.AppDataDirectory, fileName);

            using var source = await result.OpenReadAsync();
            using var target = File.Create(targetPath);
            await source.CopyToAsync(target);

            var session = AppServices.Get<AppSession>();
            await session.SetSpaceWallpaperAsync(_spaceId, targetPath, await session.GetSpaceWallpaperDimAsync(_spaceId));

            _currentWallpaperPath = targetPath;
            _currentWallpaperDim = await session.GetSpaceWallpaperDimAsync(_spaceId);
            SpaceWallpaperDimSwitch.IsToggled = _currentWallpaperDim;
            UpdateWallpaperPreview();
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_043"), ex.Message), "OK");
        }
    }
}
