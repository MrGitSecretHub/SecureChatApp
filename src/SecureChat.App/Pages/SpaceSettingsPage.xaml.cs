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
    private string? _currentWallpaperPath;
    private bool _currentWallpaperDim;

    public SpaceSettingsPage(string spaceId, string spaceName, string spaceType)
    {
        InitializeComponent();
        _spaceId = spaceId;
        _spaceType = spaceType;

        Title = $"Настройки: {spaceName}";
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
                NameEntry.IsEnabled = isAdmin;
                DescriptionEditor.IsEnabled = isAdmin;

                PublicRow.IsVisible = isAdmin;
                PublicSwitch.IsToggled = _space.IsPublic;
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
                ? $"{_members.Count} участников. Нажмите на участника, чтобы изменить роль."
                : "Пока нет участников.";

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

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await DisplayAlert("Ошибка", "Название не может быть пустым.", "OK");
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
                signMessages: canEdit && _spaceType == SpaceTypes.Channel
                    ? SignSwitch.IsToggled
                    : null);
            if (updated is not null)
            {
                await DisplayAlert("Готово", "Настройки пространства сохранены.", "OK");
                await LoadAsync();
            }
            else
            {
                await DisplayAlert("Ошибка", "Недостаточно прав для изменения.", "OK");
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
            "Добавить участника",
            "Введите логин пользователя:",
            "Пригласить",
            "Отмена",
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
                    "Готово",
                    $"{result.Username ?? username.Trim()} добавлен в пространство. " +
                    "Чтобы увидеть его, участнику нужно обновить список пространств.",
                    "OK");
                await LoadAsync();
                return;
            }

            var reason = result.Error switch
            {
                "user_not_found" => $"Пользователь «{username.Trim()}» не найден на сервере. Проверьте логин.",
                "already_member" => "Этот пользователь уже состоит в пространстве.",
                "no_rights" => "Приглашать участников может только владелец или администратор.",
                "space_not_found" => "Пространство не найдено или у вас нет к нему доступа.",
                "key_unavailable" => "Не удалось получить ключ пространства для приглашения.",
                "key_save_failed" => "Участник добавлен, но ключ ему не доставлен. Попробуйте ещё раз.",
                "not_authorized" => "Сессия не активна. Войдите в аккаунт заново.",
                _ => "Не удалось добавить участника."
            };

            await DisplayAlert("Не получилось", reason, "OK");
        }
        catch (ApiException ex)
        {
            await DisplayAlert("Не получилось", ex.Message, "OK");
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (message.Contains("Failed to invoke", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Method does not exist", StringComparison.OrdinalIgnoreCase))
            {
                message = "Сервер запущен в старой версии. Перезапустите SecureChat.Server.exe и повторите приглашение.";
            }

            await DisplayAlert("Ошибка", message, "OK");
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

        AddSection("Владелец", SpaceRoles.Owner);
        AddSection("Администраторы", SpaceRoles.Admin);
        AddSection("Участники", SpaceRoles.Member);
        AddSection("Гости (только чтение)", SpaceRoles.Guest);

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

        var actions = new List<string> { "👤 Открыть профиль" };
            if (isAdmin)
            {
                actions.Add("Изменить роль");
                actions.Add(member.CanPost ? "🔇 Запретить писать" : "✏️ Разрешить писать");
                actions.Add(member.IsMuted ? "🔊 Снять запрет записи" : "⏱ Запрет записи на время…");
                actions.Add("Удалить из пространства");
            }

            var choice = await DisplayActionSheet(
                member.Username, "Отмена", null, actions.ToArray());

            if (string.IsNullOrWhiteSpace(choice) || choice == "Отмена")
            {
                return;
            }

            if (choice == "👤 Открыть профиль")
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
                await DisplayAlert(member.Username, "Это владелец пространства — его роль не меняют.", "OK");
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
            if (choice == "Удалить из пространства")
            {
                await _spaces.RemoveMemberAsync(_spaceId, member.UserId);
            }
            else if (choice == "Изменить роль")
            {
                var role = await DisplayActionSheet(
                    $"Роль: {member.Username}",
                    "Отмена",
                    null,
                    "Участник",
                    "Гость (только чтение)",
                    "Администратор");

                if (string.IsNullOrWhiteSpace(role) || role == "Отмена")
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
            else if (choice.StartsWith("🔇 Запретить") || choice.StartsWith("✏️ Разрешить"))
            {
                // Право писать отдельно от роли: роль не трогаем.
                await _spaces.UpdateMemberAsync(
                    _spaceId, member.UserId, canPost: !member.CanPost);
            }
            else if (choice == "🔊 Снять запрет записи")
            {
                await _spaces.UnmuteMemberAsync(_spaceId, member.UserId);
            }
            else if (choice == "⏱ Запрет записи на время…")
            {
                var duration = await DisplayActionSheet(
                    $"Запрет писать для {member.Username}:",
                    "Отмена",
                    null,
                    "1 час",
                    "1 день",
                    "1 неделя");

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
            "Покинуть пространство?",
            "Вы перестанете получать сообщения этого пространства.",
            "Покинуть",
            "Отмена");
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
            "Удалить пространство?",
            "Будут удалены все сообщения, темы и список участников.",
            "Удалить",
            "Отмена");
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
                await DisplayAlert("Ошибка", "Недостаточно прав для удаления.", "OK");
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
                PickerTitle = "Выберите обои",
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
                PickerTitle = "Выберите файл обоев"
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
            await DisplayAlert("Ошибка", $"Не удалось сохранить обои: {ex.Message}", "OK");
        }
    }
}
