using System.Collections.ObjectModel;
using Microsoft.Maui.ApplicationModel;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>
/// Комментарии к посту канала. Комментарий — обычное зашифрованное сообщение
/// пространства с CommentToMessageId: на сервере нет отдельной сущности,
/// а лента такие сообщения не показывает.
/// </summary>
public partial class PostCommentsPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ObservableCollection<SpaceMessageVm> _comments = new();
    private readonly string _spaceId;
    private readonly string? _topicId;
    private readonly string _postId;
    private string _myUserId = string.Empty;
    private bool _canModerate;
    private List<SpaceMemberDto> _members = new();

    public PostCommentsPage(
        string spaceId,
        string? topicId,
        string postId,
        string postAuthor,
        string postPreview)
    {
        InitializeComponent();
        _spaceId = spaceId;
        _topicId = topicId;
        _postId = postId;

        CommentsView.ItemsSource = _comments;
        PostPreviewLabel.Text =
            $"К посту от {postAuthor}: {Shorten(postPreview, 160)}";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _spaces.Subscribe();
        _spaces.SpaceMessageReceived += OnSpaceMessageReceived;
        _spaces.SpaceMessageDeleted += OnSpaceMessageDeleted;
        _myUserId = AppServices.Get<AppSession>().User?.Id ?? string.Empty;
        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _spaces.SpaceMessageReceived -= OnSpaceMessageReceived;
        _spaces.SpaceMessageDeleted -= OnSpaceMessageDeleted;
    }

    private async Task LoadAsync()
    {
        try
        {
            var members = await _spaces.GetMembersAsync(_spaceId);
            _members = members;
            CommentEntry.SetMembers(members.Select(member => member.Username));

            _canModerate = members.Any(member =>
                string.Equals(member.UserId, _myUserId, StringComparison.Ordinal) &&
                (member.Role == SpaceRoles.Owner || member.Role == SpaceRoles.Admin));

            var messages = await _spaces.GetMessagesAsync(_spaceId, _topicId);
            var found = new List<SpaceMessageVm>();

            foreach (var message in messages)
            {
                var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
                if (!string.Equals(payload?.CommentToMessageId, _postId, StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add(CreateVm(message, payload));
            }

            _comments.Clear();
            foreach (var comment in found)
            {
                _comments.Add(comment);
            }

            EmptyLabel.IsVisible = _comments.Count == 0;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    private SpaceMessageVm CreateVm(SpaceMessageDto message, SpaceMessagePayload payload)
    {
        var vm = SpaceMessageVm.From(message, payload, _myUserId, _canModerate);
        vm.MentionTapped += OnMentionTapped;
        return vm;
    }

    private async void OnSpaceMessageReceived(SpaceMessageDto message)
    {
        if (!string.Equals(message.SpaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        if (_topicId is not null &&
            !string.Equals(message.TopicId, _topicId, StringComparison.Ordinal))
        {
            return;
        }

        var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
        if (!string.Equals(payload?.CommentToMessageId, _postId, StringComparison.Ordinal))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_comments.Any(item => item.Id == message.Id))
            {
                return;
            }

            _comments.Add(CreateVm(message, payload!));
            EmptyLabel.IsVisible = false;
        });
    }

    private void OnSpaceMessageDeleted(string spaceId, string messageId)
    {
        if (!string.Equals(spaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = _comments.FirstOrDefault(item => item.Id == messageId);
            if (existing is not null)
            {
                _comments.Remove(existing);
            }

            EmptyLabel.IsVisible = _comments.Count == 0;
        });
    }

    /// <summary>Отправка комментария: обычное сообщение пространства с CommentToMessageId.</summary>
    private async void OnSendClicked(object sender, EventArgs e)
    {
        var text = CommentEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CommentEntry.Text = string.Empty;

        try
        {
            var payload = new SpaceMessagePayload
            {
                Kind = "text",
                Text = text,
                CommentToMessageId = _postId,
                Mentions = SpaceMentions.Parse(
                    text,
                    _members.Select(member => member.Username))
            };

            var sent = await _spaces.SendPayloadAsync(_spaceId, _topicId, payload);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (sent is not null && !_comments.Any(item => item.Id == sent.Id))
                {
                    _comments.Add(CreateVm(sent, payload));
                }

                EmptyLabel.IsVisible = false;
            });
        }
        catch (Exception ex)
        {
            CommentEntry.Text = text;
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    /// <summary>Удаление комментария: свой или при правах модератора.</summary>
    private async void OnCommentDeleteClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm comment })
        {
            return;
        }

        var choice = await DisplayActionSheet(
            "Удалить комментарий?",
            "Отмена",
            null,
            "Удалить");
        if (choice != "Удалить")
        {
            return;
        }

        try
        {
            var deleted = await _spaces.DeleteMessageAsync(_spaceId, comment.Id);
            if (!deleted)
            {
                await DisplayAlert("Ошибка", "Не удалось удалить комментарий.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    /// <summary>Нажали @упоминание в комментарии: открываем личный чат.</summary>
    private async void OnMentionTapped(SpaceMessageVm comment, string username)
    {
        if (string.Equals(username, AppServices.Get<AppSession>().User?.Username,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await Navigation.PushAsync(new ChatPage(username));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    private static string Shorten(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "без текста";
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
