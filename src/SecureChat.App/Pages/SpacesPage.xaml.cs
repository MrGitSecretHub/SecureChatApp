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
        await LoadAsync();
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

        await Navigation.PushAsync(new SpaceChatPage(item.Id, item.Name, item.Type, item.CanPost));
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
