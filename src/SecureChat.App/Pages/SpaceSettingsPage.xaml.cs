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

        MembersView.ItemsSource = _members;
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

            MembersView.ItemsSource = _members;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }

        // Загрузка текущих обоев пространства
        var session = AppServices.Get<AppSession>();
        _currentWallpaperPath = session.GetSpaceWallpaperPath(_spaceId);
        _currentWallpaperDim = session.GetSpaceWallpaperDim(_spaceId);
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
            var updated = await _spaces.UpdateAsync(_spaceId, name, DescriptionEditor.Text ?? string.Empty);
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
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

    private async void OnMemberTapped(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not SpaceMemberVm member)
        {
            return;
        }

        MembersView.SelectedItem = null;

        var choice = await DisplayActionSheet(
            member.Username,
            "Отмена",
            null,
            "Изменить роль",
            "Удалить из пространства");

        if (string.IsNullOrWhiteSpace(choice) || choice == "Отмена")
        {
            return;
        }

        try
        {
            if (choice == "Удалить из пространства")
            {
                await _spaces.RemoveMemberAsync(_spaceId, member.UserId);
            }
            else
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

                await _spaces.UpdateMemberAsync(
                    _spaceId,
                    member.UserId,
                    newRole,
                    newRole != SpaceRoles.Guest);
            }

            await LoadAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    // ============================== ОБОИ ПРОСТРАНСТВА ==============================

    private void UpdateWallpaperPreview()
    {
        var session = AppServices.Get<AppSession>();
        var path = session.GetSpaceWallpaperPath(_spaceId);
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    private async void OnSpaceWallpaperRemoveClicked(object sender, EventArgs e)
    {
        var session = AppServices.Get<AppSession>();
        session.RemoveSpaceWallpaper(_spaceId);
        _currentWallpaperPath = string.Empty;
        _currentWallpaperDim = true;
        SpaceWallpaperDimSwitch.IsToggled = _currentWallpaperDim;
        UpdateWallpaperPreview();
    }

    private async void OnSpaceWallpaperDimToggled(object sender, ToggledEventArgs e)
    {
        var session = AppServices.Get<AppSession>();
        var currentPath = session.GetSpaceWallpaperPath(_spaceId);
        if (string.IsNullOrEmpty(currentPath))
        {
            return;
        }

        session.SetSpaceWallpaper(_spaceId, currentPath, e.Value);
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
            session.SetSpaceWallpaper(_spaceId, targetPath, session.GetSpaceWallpaperDim(_spaceId));

            _currentWallpaperPath = targetPath;
            _currentWallpaperDim = session.GetSpaceWallpaperDim(_spaceId);
            SpaceWallpaperDimSwitch.IsToggled = _currentWallpaperDim;
            UpdateWallpaperPreview();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось сохранить обои: {ex.Message}", "OK");
        }
    }
}
