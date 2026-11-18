using SecureChat.Core.Net;
using SecureChat.App.Services;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>
/// Карточка пространства для любого участника: описание, владелец, статистика,
/// локальное отключение звука, выход. Настройки (имя, участники, обои) — по кнопке
/// для владельцев и администраторов.
/// </summary>
public partial class SpaceProfilePage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ChatManager _chat = AppServices.Get<ChatManager>();
    private readonly string _spaceId;
    private string _spaceType = SpaceTypes.Group;
    private bool _loadingSwitch;

    public SpaceProfilePage(string spaceId)
    {
        InitializeComponent();
        _spaceId = spaceId;
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
            var dto = spaces.FirstOrDefault(item => item.Id == _spaceId);
            if (dto is null)
            {
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_062", "Пространство не найдено."), "OK");
                await Navigation.PopAsync();
                return;
            }

            _spaceType = dto.Type;
            Title = dto.Name;
            SpaceNameLabel.Text = dto.Name;

            (SpaceIconLabel.Text, SpaceTypeLabel.Text) = dto.Type switch
            {
                SpaceTypes.Channel => ("📢", Loc.T("rs_065", "Канал")),
                SpaceTypes.Forum => ("🧵", Loc.T("rs_165", "Форум")),
                SpaceTypes.Community => ("🌐", Loc.T("rs_150", "Сообщество")),
                _ => ("👥", Loc.T("rs_040", "Группа")),
            };

            DescriptionLabel.Text = string.IsNullOrWhiteSpace(dto.Description)
                ? Loc.T("rs_014", "Без описания.")
                : dto.Description;

            var members = await _spaces.GetMembersAsync(_spaceId);
            var stats = string.Format(Loc.T("uc_000"), members.Count);
            if (dto.TopicCount > 0)
            {
                stats += $" · {dto.TopicCount} тем";
            }

            if (dto.IsPublic)
            {
                stats += " · публичное";
            }

            StatsLabel.Text = stats;

            var owner = members.FirstOrDefault(item =>
                string.Equals(item.Role, SpaceRoles.Owner, StringComparison.OrdinalIgnoreCase));
            OwnerLabel.Text = Loc.T("uc_007", "Владелец: ") + (owner?.Username ?? dto.OwnerUsername);

            var isModerator = dto.MyRole is SpaceRoles.Owner or SpaceRoles.Admin;
            SettingsButton.IsVisible = isModerator;
            // Владелец не выходит — ему пространство удалять.
            LeaveButton.IsVisible = dto.MyRole != SpaceRoles.Owner;

            _loadingSwitch = true;
            MuteSwitch.IsToggled = await _chat.IsSpaceMutedAsync(_spaceId);
            _loadingSwitch = false;
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
    }

    private async void OnMuteToggled(object sender, ToggledEventArgs e)
    {
        if (_loadingSwitch)
        {
            return;
        }

        try
        {
            await _chat.SetSpaceMutedAsync(_spaceId, e.Value);
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnSettingsClicked(object sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new SpaceSettingsPage(_spaceId, SpaceNameLabel.Text, _spaceType));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnLeaveClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("dg_021", "Выйти из пространства?"),
            Loc.T("dg_017", "Вы перестанете получать его сообщения. Вернуться сможет только администратор по приглашению."),
            Loc.T("dg_019", "Выйти"), Loc.T("common_cancel", "Отмена"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            var left = await _spaces.LeaveAsync(_spaceId);
            if (!left)
            {
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_025", "Не удалось выйти из пространства."), "OK");
                return;
            }

            // Закрываем и карточку, и переписку — пространство больше недоступно.
            await Navigation.PopAsync();
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }
}
