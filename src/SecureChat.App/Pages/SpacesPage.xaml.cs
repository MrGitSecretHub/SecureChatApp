using SecureChat.Core.Net;
using System.Collections.ObjectModel;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>Список групп, каналов, сообществ и форумов.</summary>
public partial class SpacesPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly ObservableCollection<SpaceVm> _items = new();
    private List<SpaceDto> _all = new();
    private string _filter = "all";

    public SpacesPage()
    {
        InitializeComponent();
        SpacesView.ItemsSource = _items;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _spaces.Subscribe();
        _spaces.JoinRequestResolved += OnJoinRequestResolved;
        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _spaces.JoinRequestResolved -= OnJoinRequestResolved;
    }

    /// <summary>Заявку приняли — обновляем список пространств и показываем результат.</summary>
    private async void OnJoinRequestResolved(string spaceId, bool approved, string spaceName)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await LoadAsync();

            if (approved)
            {
                await DisplayAlert(Loc.T("dg_029", "Заявка принята"),
                    string.Format(Loc.T("dg_016"), spaceName),
                    "Ок");
            }
        });
    }

    private async Task LoadAsync()
    {
        if (_chatManager.IsOffline)
        {
            OfflineLabel.IsVisible = true;
            return;
        }

        try
        {
            _all = await _spaces.ListAsync();
            OfflineLabel.IsVisible = false;
            JoinRequestsButton.IsVisible = _all.Any(space =>
                space.MyRole is SpaceRoles.Owner or SpaceRoles.Admin);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            OfflineLabel.Text = string.Format(Loc.T("uc_027"), ex.Message);
            OfflineLabel.IsVisible = true;
        }
    }

    /// <summary>Тап по строке ошибки — повторная попытка (подняла WebSocket после обрыва).</summary>
    private void OnRetryTapped(object? sender, EventArgs e) => _ = LoadAsync();

    private void ApplyFilter()
    {
        _items.Clear();
        var query = SearchEntry.Text?.Trim();
        foreach (var space in _all)
        {
            if (_filter != "all" && space.Type != _filter)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(query) &&
                !space.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !space.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _items.Add(SpaceVm.From(space));
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFilterClicked(object sender, EventArgs e)
    {
        _filter = sender switch
        {
            Button { Text: "Группы" } => SpaceTypes.Group,
            Button { Text: "Каналы" } => SpaceTypes.Channel,
            Button { Text: "Форумы" } => SpaceTypes.Forum,
            Button { Text: "Сообщества" } => SpaceTypes.Community,
            _ => "all"
        };

        ApplyFilter();
    }

    private async void OnSpaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not SpaceVm item)
        {
            return;
        }

        SpacesView.SelectedItem = null;

        // Публичное пространство: вступить может каждый, без приглашения.
        if (item.IsJoinable)
        {
            var join = await DisplayAlert(
                item.Name,
                Loc.T("dg_056", "Публичное пространство. Историей владеют только участники: общий E2E-ключ передаст вам любой участник, как только будет в сети — обычно это секунды."),
                Loc.T("dg_014", "Вступить"), Loc.T("common_cancel", "Отмена"));
            if (!join)
            {
                return;
            }

            try
            {
                if (await _spaces.JoinAsync(item.Id))
                {
                    await DisplayAlert(item.Name,
                        Loc.T("dg_015", "Вы в пространстве. Если ключ ещё не дошёл — переоткройте чат чуть позже."),
                        Loc.T("dg_084", "Хорошо"));
                }
                else
                {
                    await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_024", "Не удалось вступить."), "OK");
                }

                await LoadAsync();
            }
            catch (Exception ex)
            {
                // Ошибку связи не показываем окном — есть постоянная подсказка
                // про офлайн-режим.
                if (ConnectionErrors.IsConnectionProblem(ex.Message))
                {
                    OfflineLabel.Text = string.Format(Loc.T("uc_073"), ex.Message);
                    OfflineLabel.IsVisible = true;
                    return;
                }

                await this.ShowErrorAsync(ex);
            }

            return;
        }

        // Приватное пространство, в котором мы не участник, — подача заявки.
        if (string.IsNullOrEmpty(item.MyRole) && !item.IsPublic)
        {
            var ask = await DisplayAlert(
                item.Name,
                Loc.T("dg_055", "Приватное пространство. Отправить заявку на вступление? Её рассмотрит владелец или администратор."),
                Loc.T("dg_048", "Подать заявку"), Loc.T("common_cancel", "Отмена"));
            if (!ask)
            {
                return;
            }

            var message = await DisplayPromptAsync(
                Loc.T("rs_048", "Заявка на вступление"),
                Loc.T("rs_070", "Коротко о себе (необязательно):"),
                Loc.T("rs_114", "Отправить"),
                Loc.T("common_cancel", "Отмена"),
                maxLength: 200,
                keyboard: Keyboard.Text);
            if (message is null)
            {
                return;
            }

            try
            {
                var sent = await _spaces.RequestToJoinAsync(item.Id, message ?? string.Empty);
                await DisplayAlert(
                    sent ? Loc.T("dg_027", "Заявка отправлена") : Loc.T("uc_022", "Не удалось"),
                    sent
                        ? Loc.T("dg_028", "Заявка отправлена. Когда её примут, пространство появится в вашем списке.")
                        : Loc.T("dg_032", "Не удалось отправить заявку."),
                    "Ок");
            }
            catch (Exception ex)
            {
                await this.ShowErrorAsync(ex);
            }

            return;
        }

        await Navigation.PushAsync(new SpaceChatPage(item.Id, item.Name, item.Type, item.CanPost));
    }

    /// <summary>Показать ожидающие заявки на вступление в наши пространства.</summary>
    private async void OnJoinRequestsClicked(object? sender, EventArgs e)
    {
        var adminSpaces = _all.Where(space =>
            space.MyRole is SpaceRoles.Owner or SpaceRoles.Admin).ToList();
        if (adminSpaces.Count == 0)
        {
            return;
        }

        var requests = new List<SpaceJoinRequestDto>();
        foreach (var space in adminSpaces)
        {
            try
            {
                requests.AddRange(await _spaces.GetJoinRequestsAsync(space.Id));
            }
            catch
            {
                // пространство может быть недоступно — пропускаем
            }
        }

        if (requests.Count == 0)
        {
            await DisplayAlert("Заявки на вступление", Loc.T("uc_055", "Новых заявок нет."), "Ок");
            return;
        }

        while (true)
        {
            var labels = requests.Select(item =>
                $"{item.Username} → {item.SpaceName}" +
                (string.IsNullOrWhiteSpace(item.Message) ? string.Empty : $" («{item.Message}»)")).ToArray();

            var choice = await DisplayActionSheet(
                "Заявки на вступление",
                Loc.T("common_close", "Закрыть"),
                null,
                labels);

            if (string.IsNullOrWhiteSpace(choice) || choice == Loc.T("common_close", "Закрыть"))
            {
                return;
            }

            var index = Array.IndexOf(labels, choice);
            if (index < 0 || index >= requests.Count)
            {
                return;
            }

            var target = requests[index];
            var decision = await DisplayActionSheet(
                string.Format(Loc.T("dg_026"), target.Username),
                Loc.T("common_cancel", "Отмена"),
                null,
                Loc.T("dg_091", "✅ Принять"),
                Loc.T("dg_092", "❌ Отклонить"));

            if (decision is not ("✅ Принять" or "❌ Отклонить"))
            {
                continue;
            }

            try
            {
                var ok = await _spaces.ResolveJoinRequestAsync(target.Id, decision == "✅ Принять");
                if (!ok)
                {
                    await DisplayAlert(Loc.T("uc_022", "Не удалось"), Loc.T("uc_012", "Заявка уже обработана."), "Ок");
                    requests.RemoveAt(index);
                    continue;
                }
            }
            catch (Exception ex)
            {
                await this.ShowErrorAsync(ex);
                return;
            }

            requests.RemoveAt(index);
            if (requests.Count == 0)
            {
                await DisplayAlert(Loc.T("set_036", "Готово"), Loc.T("uc_010", "Все заявки рассмотрены."), "Ок");
                return;
            }
        }
    }

    private async void OnCreateClicked(object sender, EventArgs e)
    {
        var name = await DisplayPromptAsync(
            Loc.T("rs_109", "Новое пространство"),
            Loc.T("rs_019", "Введите название:"),
            Loc.T("rs_149", "Создать"),
            Loc.T("common_cancel", "Отмена"),
            maxLength: 64,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var typeChoice = await DisplayActionSheet(
            Loc.T("dg_072", "Тип пространства:"),
            Loc.T("common_cancel", "Отмена"),
            null,
            Loc.T("dg_094", "👥 Группа — общий чат, пишут все участники"),
            Loc.T("dg_095", "📢 Канал — объявления, пишут только админы"),
            Loc.T("dg_096", "🧵 Форум — темы с обсуждениями"),
            Loc.T("dg_093", "🌐 Сообщество — публичная площадка с каналами"));
        if (string.IsNullOrWhiteSpace(typeChoice) || typeChoice == Loc.T("common_cancel", "Отмена"))
        {
            return;
        }

        var type = typeChoice switch
        {
            var value when value.StartsWith("📢") => SpaceTypes.Channel,
            var value when value.StartsWith("🧵") => SpaceTypes.Forum,
            var value when value.StartsWith("🌐") => SpaceTypes.Community,
            _ => SpaceTypes.Group
        };

        var description = await DisplayPromptAsync(
            "Описание",
            Loc.T("rs_069", "Коротко о пространстве (необязательно):"),
            "Далее",
            Loc.T("rs_139", "Пропустить"),
            maxLength: 200,
            keyboard: Keyboard.Text);

        try
        {
            var created = await _spaces.CreateAsync(
                name,
                type,
                description ?? string.Empty,
                isPublic: type is SpaceTypes.Channel or SpaceTypes.Community or SpaceTypes.Forum,
                members: Array.Empty<string>());

            if (created is not null)
            {
                await LoadAsync();
                await DisplayAlert(Loc.T("set_036", "Готово"), string.Format(Loc.T("uc_061"), created.Name), "OK");
            }
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }
}
