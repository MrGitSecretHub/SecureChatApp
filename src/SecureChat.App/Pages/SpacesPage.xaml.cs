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
                await DisplayAlert("Заявка принята",
                    $"Вы вступили в «{spaceName}». Если ключ ещё не дошёл — переоткройте чат чуть позже.",
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
            OfflineLabel.Text = $"Не удалось загрузить пространства: {ex.Message}. Нажмите, чтобы повторить.";
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
                "Публичное пространство. Историей владеют только участники: общий E2E-ключ передаст вам любой участник, как только будет в сети — обычно это секунды.",
                "Вступить", "Отмена");
            if (!join)
            {
                return;
            }

            try
            {
                if (await _spaces.JoinAsync(item.Id))
                {
                    await DisplayAlert(item.Name,
                        "Вы в пространстве. Если ключ ещё не дошёл — переоткройте чат чуть позже.",
                        "Хорошо");
                }
                else
                {
                    await DisplayAlert("Ошибка", "Не удалось вступить.", "OK");
                }

                await LoadAsync();
            }
            catch (Exception ex)
            {
                // Ошибку связи не показываем окном — есть постоянная подсказка
                // про офлайн-режим.
                if (ConnectionErrors.IsConnectionProblem(ex.Message))
                {
                    OfflineLabel.Text = $"⚡ {ex.Message}. Нажмите, чтобы повторить.";
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
                "Приватное пространство. Отправить заявку на вступление? Её рассмотрит владелец или администратор.",
                "Подать заявку", "Отмена");
            if (!ask)
            {
                return;
            }

            var message = await DisplayPromptAsync(
                "Заявка на вступление",
                "Коротко о себе (необязательно):",
                "Отправить",
                "Отмена",
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
                    sent ? "Заявка отправлена" : "Не удалось",
                    sent
                        ? "Заявка отправлена. Когда её примут, пространство появится в вашем списке."
                        : "Не удалось отправить заявку.",
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
            await DisplayAlert("Заявки на вступление", "Новых заявок нет.", "Ок");
            return;
        }

        while (true)
        {
            var labels = requests.Select(item =>
                $"{item.Username} → {item.SpaceName}" +
                (string.IsNullOrWhiteSpace(item.Message) ? string.Empty : $" («{item.Message}»)")).ToArray();

            var choice = await DisplayActionSheet(
                "Заявки на вступление",
                "Закрыть",
                null,
                labels);

            if (string.IsNullOrWhiteSpace(choice) || choice == "Закрыть")
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
                $"Заявка от {target.Username}",
                "Отмена",
                null,
                "✅ Принять",
                "❌ Отклонить");

            if (decision is not ("✅ Принять" or "❌ Отклонить"))
            {
                continue;
            }

            try
            {
                var ok = await _spaces.ResolveJoinRequestAsync(target.Id, decision == "✅ Принять");
                if (!ok)
                {
                    await DisplayAlert("Не удалось", "Заявка уже обработана.", "Ок");
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
                await DisplayAlert("Готово", "Все заявки рассмотрены.", "Ок");
                return;
            }
        }
    }

    private async void OnCreateClicked(object sender, EventArgs e)
    {
        var name = await DisplayPromptAsync(
            "Новое пространство",
            "Введите название:",
            "Создать",
            "Отмена",
            maxLength: 64,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var typeChoice = await DisplayActionSheet(
            "Тип пространства:",
            "Отмена",
            null,
            "👥 Группа — общий чат, пишут все участники",
            "📢 Канал — объявления, пишут только админы",
            "🧵 Форум — темы с обсуждениями",
            "🌐 Сообщество — публичная площадка с каналами");
        if (string.IsNullOrWhiteSpace(typeChoice) || typeChoice == "Отмена")
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
            "Коротко о пространстве (необязательно):",
            "Далее",
            "Пропустить",
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
                await DisplayAlert("Готово", $"Пространство «{created.Name}» создано.", "OK");
            }
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }
}
