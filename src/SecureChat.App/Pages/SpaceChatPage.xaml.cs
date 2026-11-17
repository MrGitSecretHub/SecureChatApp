using SecureChat.Core.Net;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Timers;
using Microsoft.Maui.ApplicationModel;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Contracts;

#if ANDROID
using Android.Media;
#endif

namespace SecureChat.App.Pages;

/// <summary>Переписка в пространстве: группа, канал, сообщество или форум.</summary>
public partial class SpaceChatPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly ChatManager _chat = AppServices.Get<ChatManager>();
    private readonly LinkPreviewService _previewService = AppServices.Get<LinkPreviewService>();
    private readonly IAudioRecorder _audioRecorder = AppServices.Get<IAudioRecorder>();
    private readonly ObservableCollection<SpaceMessageVm> _messages = new();
    private readonly string _spaceId;
    private readonly string _spaceName;
    private readonly string _spaceType;
    private bool _signMessages;
    private bool _silentPosting;
    private string? _topicId;
    private string? _topicTitle;

    /// <summary>Текущая тема закрыта — в неё нельзя писать.</summary>
    private bool _currentTopicClosed;
    private SpaceMessageVm? _pinnedMessage;
    private string? _myUserId;
    private bool _canModerate;
    private bool _isMuted;
    private bool _canPost = true;
    private bool _sendingAttachment;
    private SpaceMessageVm? _replyingTo;
    private List<SpaceMemberDto> _members = new();
    private readonly ObservableCollection<SpaceMessageVm> _pinnedItems = new();
    private int _currentPinnedIndex = -1;

    // Переход к упоминаниям и ответам: список целевых сообщений лент + курсор.
    private readonly List<SpaceMessageVm> _mentionTargets = new();
    private int _mentionCursor = -1;

    /// <summary>Очередь отправки медиа в пространстве.</summary>
    private readonly ObservableCollection<PendingMediaItem> _pendingMedia = new();
    public IEnumerable<PendingMediaItem> PendingMedia => _pendingMedia;
    private bool _processingMediaQueue;

    /// <summary>Один элемент очереди отправки с прогрессом и возможностью отмены.</summary>
    public sealed class PendingMediaItem : INotifyPropertyChanged
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string? MessageKind { get; set; }
        public CancellationTokenSource Cts { get; } = new();

        private int _progress;
        public int Progress
        {
            get => _progress;
            set { if (_progress != value) { _progress = value; Notify(); } }
        }

        private string _status = "Отправка…";
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; Notify(); } }
        }

        public string KindEmoji => ContentType switch
        {
            var c when c.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "📷",
            var c when c.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "🎤",
            var c when c.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "📹",
            _ => "📄"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

#if ANDROID || WINDOWS
    private IVoicePlayer? _voicePlayer;
    private string? _voicePlayingMessageId;
    private string? _voiceProgressMessageId;
    private System.Timers.Timer? _voiceProgressTimer;
#endif

    public SpaceChatPage(string spaceId, string name, string type, bool canPost = true)
    {
        InitializeComponent();
        _spaceId = spaceId;
        _spaceName = name;
        _spaceType = type;
        _canPost = canPost;

        Title = name;
        SpaceTitleLabel.Text = name;
        _myUserId = AppServices.Get<AppSession>().User?.Id;

        TopicsButton.IsVisible = type == SpaceTypes.Forum;
        MessagesView.ItemsSource = _messages;
        PinnedItemsViewInline.ItemsSource = _pinnedItems;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _spaces.Subscribe();
        _spaces.SpaceMessageReceived += OnSpaceMessageReceived;
        _spaces.SpaceMessageUpdated += OnSpaceMessageUpdated;
        _spaces.SpaceMessageDeleted += OnSpaceMessageDeleted;
        _spaces.TopicUpdated += OnTopicUpdated;
        AppServices.Get<AppSession>().AppearanceChanged += OnAppearanceChanged;
        RefreshSenderNameColors();
        ApplySpaceWallpaper();
        await LoadAsync();
    }

    /// <summary>Перекрашивает имена своих сообщений после изменения
    /// настройки «Цвет вашего имени» (возврат со страницы настроек).</summary>
    private void RefreshSenderNameColors()
    {
        if (Application.Current?.Resources["SenderNameColor"] is not Color nameColor)
        {
            return;
        }

        foreach (var message in _messages)
        {
            if (message.IsMine)
            {
                message.SenderNameColor = nameColor;
            }
        }
    }

    private async void ApplySpaceWallpaper()
    {
        var session = AppServices.Get<AppSession>();
        var path = await session.GetSpaceWallpaperPathAsync(_spaceId);
        SpaceWallpaperImage.IsVisible = !string.IsNullOrEmpty(path);
        SpaceWallpaperImage.Source = string.IsNullOrEmpty(path)
            ? null
            : ImageSource.FromFile(path);
        SpaceWallpaperShade.IsVisible = !string.IsNullOrEmpty(path) && await session.GetSpaceWallpaperDimAsync(_spaceId);
    }

    private void OnAppearanceChanged()
    {
        MainThread.BeginInvokeOnMainThread(ApplySpaceWallpaper);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopVoicePlayback();
        _spaces.SpaceMessageReceived -= OnSpaceMessageReceived;
        _spaces.SpaceMessageUpdated -= OnSpaceMessageUpdated;
        _spaces.SpaceMessageDeleted -= OnSpaceMessageDeleted;
        _spaces.TopicUpdated -= OnTopicUpdated;
        AppServices.Get<AppSession>().AppearanceChanged -= OnAppearanceChanged;
    }

    /// <summary>Тему переименовали, закрыли или переставили — обновляем ленту.</summary>
    private void OnTopicUpdated(TopicDto topic)
    {
        if (!string.Equals(topic.SpaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (string.Equals(topic.Id, _topicId, StringComparison.Ordinal))
            {
                _topicTitle = topic.Title;
                _currentTopicClosed = topic.IsClosed;
                await LoadAsync();
            }
        });
    }

    private void OnSpaceMessageUpdated(SpaceMessageDto message)
    {
        if (!string.Equals(message.SpaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = _messages.FirstOrDefault(item => item.Id == message.Id);
            if (existing is not null)
            {
                existing.IsPinned = message.IsPinned;
                existing.IsEdited = message.EditedAtUnixMs > 0;
                existing.SetReactions(message.Reactions, _myUserId ?? string.Empty);
            }

            UpdatePinnedBanner();
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
            var existing = _messages.FirstOrDefault(item => item.Id == messageId);
            if (existing is not null)
            {
                _messages.Remove(existing);
            }

            UpdatePinnedBanner();
        });
    }

    private async Task LoadAsync()
    {
        try
        {
            // Участники нужны раньше сообщений: меню @упоминаний и счётчики.
            var members = await _spaces.GetMembersAsync(_spaceId);
            _members = members;
            MessageEntry.SetMembers(members.Select(member => member.Username));

            // Права модератора (закрепление, удаление чужого, удаление тем) —
            // раньше поле никогда не заполнялось и всегда было false.
            _canModerate = members.Any(member =>
                string.Equals(member.UserId, _myUserId, StringComparison.Ordinal) &&
                (member.Role == SpaceRoles.Owner || member.Role == SpaceRoles.Admin));

            // Состояние локального мута пространства (иконка в шапке).
            _isMuted = await _chat.IsSpaceMutedAsync(_spaceId);
            MuteButton.Text = _isMuted ? "🔕" : "🔔";

            // @all позволяют только владелец и администраторы.
            MessageEntry.SetShowAll(_canModerate);

            // Для подсветки @упоминаний в сообщениях, отправленных до появления
            // этой функции (payload старых версий не несёт список упомянутых).
            SpaceMessageVm.SetFallbackMembers(
                _spaceId,
                members.Select(member => member.Username));

            // Режимы канала (подписи «через @админ») берём из описания пространства.
            _signMessages = (await _spaces.ListAsync())
                .FirstOrDefault(s => s.Id == _spaceId)?.SignMessages == true;

            // Статус темы форума: закрытая тема не принимает новых сообщений.
            if (_topicId is not null)
            {
                var topic = (await _spaces.GetTopicsAsync(_spaceId))
                    .FirstOrDefault(item => item.Id == _topicId);
                _currentTopicClosed = topic?.IsClosed == true;
            }
            else
            {
                _currentTopicClosed = false;
            }

            // Публичное пространство после вступления: ключа может ещё не быть —
            // тогда лента не читается, показываем баннер ожидания.
            if (await _spaces.GetSpaceKeyAsync(_spaceId) is null)
            {
                _messages.Clear();
                Composer.IsVisible = false;
                ReadOnlyBanner.IsVisible = false;
                KeyWaitBanner.IsVisible = true;
                SpaceInfoLabel.Text = $"{members.Count} участников";
                return;
            }

            KeyWaitBanner.IsVisible = false;
            OfflineBanner.IsVisible = false;
            var messages = await _spaces.GetMessagesAsync(_spaceId, _topicId);

            var commentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var feed = new List<SpaceMessageVm>();

            foreach (var message in messages)
            {
                var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);

                if (payload?.CommentToMessageId is not null)
                {
                    // Комментарий к посту: не в ленту, только в счётчик поста.
                    commentCounts[payload.CommentToMessageId] =
                        commentCounts.TryGetValue(payload.CommentToMessageId, out var count)
                            ? count + 1
                            : 1;
                    continue;
                }

                feed.Add(CreateVm(
                    message,
                    payload ?? new SpaceMessagePayload
                    {
                        Kind = "text",
                        Text = "🔒 Сообщение не удалось расшифровать"
                    }));
            }

            _messages.Clear();
            foreach (var item in feed)
            {
                _messages.Add(item);
            }

            RefreshDateLabels();
            RefreshMentionJump();
            if (AppServices.Get<AppSession>().LinkPreviewEnabled)
            {
                _ = EnsureSpacePreviewsAsync(_messages.ToList());
            }

            if (_spaceType == SpaceTypes.Channel)
            {
                foreach (var item in _messages)
                {
                    if (commentCounts.TryGetValue(item.Id, out var count))
                    {
                        item.CommentsCount = count;
                    }
                }
            }

            // Канал только для чтения — скрываем композер и показываем баннер.
            Composer.IsVisible = _canPost;
            ReadOnlyBanner.IsVisible = !_canPost;
            SilentButton.IsVisible = _spaceType == SpaceTypes.Channel && _canPost;
            ScheduleButton.IsVisible = _spaceType == SpaceTypes.Channel && _canPost;

            // Закрытая тема форума: писать в неё нельзя.
            TopicClosedBanner.IsVisible = _currentTopicClosed && _canPost;
            TopicClosedLabel.Text = _topicTitle is null
                ? "Тема закрыта — писать в неё нельзя."
                : $"Тема «{_topicTitle}» закрыта — писать в неё нельзя.";

            SpaceInfoLabel.Text = _topicTitle is null
                ? $"{members.Count} участников"
                : $"Тема: {(_currentTopicClosed ? "🔒 " : string.Empty)}{_topicTitle} · {members.Count} участников";

            UpdatePinnedBanner();
        }
        catch (Exception ex)
        {
            // Ошибки связи не показываем окном: всплывающие окна подряд только
            // раздражают, а в интерфейсе уже есть индикатор офлайн-режима.
            if (ConnectionErrors.IsConnectionProblem(ex.Message))
            {
                ShowOfflineBanner(ex.Message);
                return;
            }

            await this.ShowErrorAsync(ex);
        }
    }

    /// <summary>Показывает баннер «нет связи» вместо модального окна.</summary>
    private void ShowOfflineBanner(string? message)
    {
        OfflineBannerLabel.Text = string.IsNullOrWhiteSpace(message)
            ? ConnectionErrors.OfflineHint
            : $"⚡ {message}";
        OfflineBanner.IsVisible = true;
    }

    /// <summary>Создаёт VM сообщения и подписывает её на нажатия по @упоминаниям.</summary>
    private SpaceMessageVm CreateVm(SpaceMessageDto message, SpaceMessagePayload payload)
    {
        var vm = SpaceMessageVm.From(
            message,
            payload,
            _myUserId ?? string.Empty,
            _canModerate,
            isChannel: _spaceType == SpaceTypes.Channel,
            myUsername: AppServices.Get<AppSession>().User?.Username);

        // Автор ленты канала — сам канал; с подписями — «канал через @админ».
        if (_spaceType == SpaceTypes.Channel)
        {
            vm.AuthorText = _signMessages
                ? $"{_spaceName} через @{vm.Sender}"
                : _spaceName;
        }

        vm.MentionTapped += OnMessageMentionTapped;
        vm.LinkTapped += OnMessageLinkTapped;
        return vm;
    }

    /// <summary>Тап по ссылке в тексте сообщения пространства.</summary>
    private void OnMessageLinkTapped(SpaceMessageVm message, string url) => _ = OpenSpaceLinkAsync(url);

    private void UpdatePinnedBanner()
    {
        _pinnedMessage = _messages.FirstOrDefault(item => item.IsPinned);
        PinnedBanner.IsVisible = _pinnedMessage is not null;

        if (_pinnedMessage is null)
        {
            PinnedLabel.Text = string.Empty;
            PinnedAuthorLabel.Text = string.Empty;
        }
        else
        {
            PinnedLabel.Text = string.IsNullOrEmpty(_pinnedMessage.ReplyToText)
                ? _pinnedMessage.Text
                : $"↪ {_pinnedMessage.ReplyToText}";
            PinnedAuthorLabel.Text = $"от {_pinnedMessage.Sender}";
        }

        RefreshPinnedItems();
    }

    private void RefreshPinnedItems()
    {
        _pinnedItems.Clear();

        foreach (var item in _messages.Reverse())
        {
            if (item.IsPinned)
            {
                _pinnedItems.Add(item);
            }
        }

        _currentPinnedIndex = _pinnedItems.Count > 0 ? 0 : -1;

        var count = _pinnedItems.Count;
        var countText = count switch
        {
            0 => string.Empty,
            1 => "1-1",
            2 => "2-2",
            3 => "3-3",
            _ => "4+"
        };
        PinnedCountLabelInline.Text = countText;
        PinnedSectionInline.IsVisible = count > 0;
    }

    private async void OnSpaceMessageReceived(SpaceMessageDto message)
    {
        if (!string.Equals(message.SpaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        if (_topicId is not null && !string.Equals(message.TopicId, _topicId, StringComparison.Ordinal))
        {
            return;
        }

        var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (payload?.CommentToMessageId is not null)
            {
                // Новый комментарий к посту: инкрементируем счётчик под постом.
                var parent = _messages.FirstOrDefault(
                    item => item.Id == payload.CommentToMessageId);
                if (parent is not null)
                {
                    parent.CommentsCount += 1;
                }

                return;
            }

            if (_messages.Any(item => item.Id == message.Id))
            {
                return;
            }

            var vm = CreateVm(
                message,
                payload ?? new SpaceMessagePayload
                {
                    Kind = "text",
                    Text = "🔒 Сообщение не удалось расшифровать"
                });
            _messages.Add(vm);
            RefreshDateLabels();
            RefreshMentionJump();
            if (AppServices.Get<AppSession>().LinkPreviewEnabled && vm.HasUrls)
            {
                _ = EnsurePreviewAsync(vm);
            }
        });
    }

    private async void OnSendClicked(object sender, EventArgs e)
    {
        if (!_canPost)
        {
            await DisplayAlert("Только чтение", "В этом канале сообщения могут публиковать только администраторы.", "OK");
            return;
        }

        if (_currentTopicClosed)
        {
            await DisplayAlert("Тема закрыта",
                "В этой теме нельзя писать — её закрыл администратор.", "Ок");
            return;
        }

        var text = MessageEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var replyTarget = _replyingTo;
        _replyingTo = null;
        ReplyBar.IsVisible = false;

        MessageEntry.Text = string.Empty;

        try
        {
            var payload = new SpaceMessagePayload
            {
                Kind = "text",
                Text = text,
                ReplyToMessageId = replyTarget?.Id,
                ReplyToText = replyTarget?.ReplyToText,
                ReplyToSender = replyTarget?.Sender,
                Silent = _silentPosting,
                Mentions = SpaceMentions.ParseForSend(
                    text,
                    _members.Select(member => member.Username),
                    allowAll: _canModerate)
            };

            _silentPosting = false;
            ResetSilentButton();

            var sent = await _spaces.SendPayloadAsync(_spaceId, _topicId, payload);

            if (sent is not null && !_messages.Any(item => item.Id == sent.Id))
            {
                _messages.Add(CreateVm(sent, payload));
            }
        }
        catch (Exception ex)
        {
            MessageEntry.Text = text;
            await this.ShowErrorAsync(ex);
        }
    }

    /// <summary>Нажали @упоминание в тексте сообщения: показываем профиль.</summary>
    private async void OnMessageMentionTapped(SpaceMessageVm message, string username)
    {
        if (string.Equals(username, AppServices.Get<AppSession>().User?.Username,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await Navigation.PushAsync(new UserProfilePage(username));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    /// <summary>Тап по ссылке в тексте: почту и телефон — в системных приложениях,
    /// веб — во встроенном браузере, при неудаче — в системном.</summary>
    private async Task OpenSpaceLinkAsync(string url)
    {
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await Launcher.Default.OpenAsync(url);
            }
            catch (Exception ex)
            {
                await DisplayAlert("SecureChat", $"Не удалось открыть ссылку: {ex.Message}", "OK");
            }

            return;
        }

        try
        {
            await Navigation.PushAsync(new BrowserPage(url));
        }
        catch
        {
            // Встроенная навигация недоступна — открываем в системном браузере.
            try
            {
                await Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);
            }
            catch (Exception ex)
            {
                await DisplayAlert("SecureChat", $"Не удалось открыть ссылку: {ex.Message}", "OK");
            }
        }
    }

    // ================================================================
    // Даты и превью ссылок в ленте пространства
    // ================================================================

    /// <summary>
    /// Пересчитывает пилюли дат: у первого сообщения ленты и при смене дня.
    /// </summary>
    private void RefreshDateLabels()
    {
        var today = DateTime.Now.Date;
        DateTime? previous = null;
        foreach (var item in _messages)
        {
            var local = item.LocalDate;
            item.HasDateLabel = previous is null || previous.Value != local;
            if (item.HasDateLabel)
            {
                item.DateLabel = FormatDateLabel(local, today);
            }

            previous = local;
        }
    }

    /// <summary>Цели перехода: загруженные сообщения с упоминанием или ответом мне.</summary>
    private void RefreshMentionJump()
    {
        _mentionTargets.Clear();
        foreach (var item in _messages)
        {
            if (!item.IsMine && (item.MentionedMe || item.RepliedToMe || item.MentionedByAll))
            {
                _mentionTargets.Add(item);
            }
        }

        if (_mentionCursor >= _mentionTargets.Count)
        {
            _mentionCursor = -1;
        }

        MentionsButton.IsVisible = _mentionTargets.Count > 0;
        MentionsButton.Text = _mentionTargets.Count > 0 ? $"🎯 {_mentionTargets.Count}" : "🎯";
    }

    /// <summary>Тап по 🎯 — прокрутка к следующему упоминанию/ответу, по кругу.</summary>
    private void OnMentionsClicked(object sender, EventArgs e)
    {
        if (_mentionTargets.Count == 0)
        {
            return;
        }

        _mentionCursor = (_mentionCursor + 1) % _mentionTargets.Count;
        var target = _mentionTargets[_mentionCursor];
        try
        {
            MessagesView.ScrollTo(target, position: ScrollToPosition.Center, animate: true);
        }
        catch
        {
            // Сообщение могло ещё не отрисоваться — следующий тап повторит попытку.
        }
    }

    private static string FormatDateLabel(DateTime date, DateTime today)
    {
        if (date == today)
        {
            return "Сегодня";
        }

        if (date == today.AddDays(-1))
        {
            return "Вчера";
        }

        return date.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"));
    }

    /// <summary>Превью последних загруженных сообщений ленты — по порядку, чтобы не плодить запросы.</summary>
    private async Task EnsureSpacePreviewsAsync(IReadOnlyList<SpaceMessageVm> messages)
    {
        foreach (var vm in messages.Where(m => m.HasUrls).Reverse().Take(30).ToList())
        {
            await EnsurePreviewAsync(vm);
        }
    }

    private async Task EnsurePreviewAsync(SpaceMessageVm vm)
    {
        foreach (var url in vm.Urls.Take(SpaceMessageVm.MaxPreviews))
        {
            if (vm.HasPreviewFor(url))
            {
                continue;
            }

            var preview = await _previewService.GetPreviewAsync(url);
            if (preview is not null)
            {
                MainThread.BeginInvokeOnMainThread(() => vm.AddPreview(preview));
            }
        }
    }

    // Чипы скрытых markdown-целей и карточки превью: три фиксированных слота.

    private void OnSpaceChip0Tapped(object? sender, EventArgs e) => OpenSlotSpaceLink(sender, 0, chip: true);

    /// <summary>Режим тихой публикации для следующего поста канала.</summary>
    private void OnSilentClicked(object? sender, EventArgs e)
    {
        _silentPosting = !_silentPosting;
        ResetSilentButton();
    }

    private void ResetSilentButton()
    {
        SilentButton.BackgroundColor = _silentPosting
            ? (Color)Application.Current!.Resources["Accent"]
            : Colors.Transparent;
        SilentButton.TextColor = _silentPosting
            ? (Color)Application.Current!.Resources["AccentText"]
            : (Color)Application.Current!.Resources["TextMuted"];
        ToolTipProperties.SetText(SilentButton, _silentPosting
            ? "Тихий пост включён — следующий пост придёт без уведомлений"
            : "Тихий пост — следующий пост придёт без уведомлений у подписчиков");
    }
    private void OnSpaceChip1Tapped(object? sender, EventArgs e) => OpenSlotSpaceLink(sender, 1, chip: true);
    private void OnSpaceChip2Tapped(object? sender, EventArgs e) => OpenSlotSpaceLink(sender, 2, chip: true);
    private void OnSpacePreview0Tapped(object? sender, EventArgs e) => OpenSlotSpaceLink(sender, 0, chip: false);
    private void OnSpacePreview1Tapped(object? sender, EventArgs e) => OpenSlotSpaceLink(sender, 1, chip: false);
    private void OnSpacePreview2Tapped(object? sender, EventArgs e) => OpenSlotSpaceLink(sender, 2, chip: false);

    private void OpenSlotSpaceLink(object? sender, int slot, bool chip)
    {
        if (sender is not BindableObject { BindingContext: SpaceMessageVm vm })
        {
            return;
        }

        var url = chip ? vm.HrefForChip(slot) : vm.HrefForPreview(slot);
        if (!string.IsNullOrEmpty(url))
        {
            _ = OpenSpaceLinkAsync(url);
        }
    }

    /// <summary>Открыть комментарии к посту канала.</summary>
    private async void OnOpenCommentsClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm post })
        {
            return;
        }

        try
        {
            await Navigation.PushAsync(new PostCommentsPage(
                _spaceId,
                post.TopicId,
                post.Id,
                post.Sender,
                post.Text));
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
            await Navigation.PushAsync(new SpaceSettingsPage(_spaceId, SpaceTitleLabel.Text, _spaceType));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    /// <summary>Локальное отключение звука пространства: звук и уведомления об упоминаниях.</summary>
    private async void OnToggleMuteClicked(object sender, EventArgs e)
    {
        _isMuted = !_isMuted;
        await _chat.SetSpaceMutedAsync(_spaceId, _isMuted);
        MuteButton.Text = _isMuted ? "🔕" : "🔔";
        ToolTipProperties.SetText(MuteButton, _isMuted
            ? "Звук пространства выключен — включить"
            : "Звук и уведомления об упоминаниях в этом пространстве");
    }

    /// <summary>Тап по заголовку — карточка пространства.</summary>
    private async void OnHeaderTapped(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new SpaceProfilePage(_spaceId));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }


    /// <summary>Открывает галерею вложений пространства.</summary>
    private async void OnMediaClicked(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new SpaceMediaPage(_spaceId, _topicId));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    /// <summary>Отложить пост канала: выбрать время публикации.</summary>
    private async void OnScheduleClicked(object? sender, EventArgs e)
    {
        var text = MessageEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            // Без текста кнопка «⏰» показывает ожидающие посты.
            await ShowScheduledPostsAsync();
            return;
        }

        var choice = await DisplayActionSheet(
            "Когда опубликовать пост?",
            "Отмена",
            null,
            "Через 15 минут",
            "Через 1 час",
            "Через 3 часа",
            "Завтра утром (09:00)",
            "Выбрать время…");

        if (string.IsNullOrWhiteSpace(choice) || choice == "Отмена")
        {
            return;
        }

        DateTime sendAt;
        if (choice == "Выбрать время…")
        {
            sendAt = await PickScheduleTimeAsync();
            if (sendAt == DateTime.MinValue)
            {
                return;
            }
        }
        else
        {
            var now = DateTime.Now;
            sendAt = choice switch
            {
                "Через 15 минут" => now.AddMinutes(15),
                "Через 1 час" => now.AddHours(1),
                "Через 3 часа" => now.AddHours(3),
                "Завтра утром (09:00)" => now.Date.AddDays(1).AddHours(9),
                _ => now.AddHours(1)
            };
        }

        try
        {
            var post = await _spaces.ScheduleMessageAsync(_spaceId, _topicId, text!, sendAt);
            if (post is not null)
            {
                MessageEntry.Text = string.Empty;
                await DisplayAlert("Пост отложен",
                    $"Пост будет опубликован {sendAt:dd.MM.yyyy HH:mm}. Он хранится только на этом устройстве.",
                    "Ок");
            }
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    /// <summary>Показывает отложенные посты этого пространства и позволяет отменить.</summary>
    private async Task ShowScheduledPostsAsync()
    {
        var local = AppServices.Get<LocalStore>();
        var posts = (await local.GetScheduledSpacePostsAsync())
            .Where(post => post.SpaceId == _spaceId)
            .ToList();

        if (posts.Count == 0)
        {
            await DisplayAlert("Отложенные посты",
                "В этом пространстве нет отложенных постов. Напишите текст и нажмите «⏰», чтобы отложить пост.",
                "Ок");
            return;
        }

        var labels = posts.Select(post =>
            $"{DateTimeOffset.FromUnixTimeMilliseconds(post.SendAtUnixMs).ToLocalTime():dd.MM HH:mm} — {post.Preview}").ToArray();

        var choice = await DisplayActionSheet(
            "Отложенные посты",
            "Закрыть",
            null,
            labels);

        if (string.IsNullOrWhiteSpace(choice) || choice == "Закрыть")
        {
            return;
        }

        var index = Array.IndexOf(labels, choice);
        if (index < 0 || index >= posts.Count)
        {
            return;
        }

        var target = posts[index];
        var cancel = await DisplayAlert(
            "Отменить пост?",
            $"Пост «{target.Preview}» будет удалён из очереди и не будет опубликован.",
            "Отменить пост", "Оставить");

        if (!cancel)
        {
            return;
        }

        await local.RemoveScheduledSpacePostAsync(target.Id);
        await DisplayAlert("Готово", "Пост удалён из очереди.", "Ок");
    }

    /// <summary>Выбор конкретных даты и времени через системные диалоги.</summary>
    private async Task<DateTime> PickScheduleTimeAsync()
    {
        var date = await DisplayActionSheet(
            "Дата публикации",
            "Отмена",
            null,
            "Сегодня",
            "Завтра",
            "Послезавтра");

        if (date is null or "Отмена")
        {
            return DateTime.MinValue;
        }

        var days = date switch
        {
            "Завтра" => 1,
            "Послезавтра" => 2,
            _ => 0
        };

        var timeChoice = await DisplayActionSheet(
            "Время публикации",
            "Отмена",
            null,
            "09:00", "12:00", "15:00", "18:00", "21:00", "Свой час…");

        if (timeChoice is null or "Отмена")
        {
            return DateTime.MinValue;
        }

        int hour;
        if (timeChoice == "Свой час…")
        {
            var answer = await DisplayPromptAsync(
                "Время публикации",
                "Введите час от 0 до 23:",
                "Ок", "Отмена",
                keyboard: Keyboard.Numeric);
            if (!int.TryParse(answer, out hour) || hour is < 0 or > 23)
            {
                return DateTime.MinValue;
            }
        }
        else
        {
            hour = timeChoice switch
            {
                "12:00" => 12,
                "15:00" => 15,
                "18:00" => 18,
                "21:00" => 21,
                _ => 9
            };
        }

        var result = DateTime.Now.Date.AddDays(days).AddHours(hour);
        return result <= DateTime.Now ? result.AddDays(1) : result;
    }

    private void OnPinnedBannerTapped(object sender, EventArgs e)
    {
        if (_pinnedMessage is null)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var target = _messages.FirstOrDefault(item => item.Id == _pinnedMessage!.Id)
                ?? _pinnedMessage;

            try
            {
                MessagesView.ScrollTo(target, position: ScrollToPosition.Center, animate: true);
                foreach (var item in _messages)
                {
                    item.IsSelected = false;
                }

                target.IsSelected = true;
            }
            catch
            {
                // Сообщение может быть ещё не отрисовано.
            }
        });
    }

    private async void OnPinnedUpClicked(object sender, EventArgs e)
    {
        if (_pinnedItems.Count == 0)
        {
            return;
        }

        _currentPinnedIndex = (_currentPinnedIndex + 1) % _pinnedItems.Count;
        await ScrollToPinnedMessageAsync(_pinnedItems[_currentPinnedIndex]);
    }

    private async void OnPinnedDownClicked(object sender, EventArgs e)
    {
        if (_pinnedItems.Count == 0)
        {
            return;
        }

        _currentPinnedIndex = (_currentPinnedIndex - 1 + _pinnedItems.Count) % _pinnedItems.Count;
        await ScrollToPinnedMessageAsync(_pinnedItems[_currentPinnedIndex]);
    }

    private async Task ScrollToPinnedMessageAsync(SpaceMessageVm item)
    {
        var target = _messages.FirstOrDefault(m => m.Id == item.Id);
        if (target is null)
        {
            return;
        }

        try
        {
            MessagesView.ScrollTo(target, position: ScrollToPosition.Center, animate: true);
            foreach (var m in _messages)
            {
                m.IsSelected = false;
            }
            target.IsSelected = true;
        }
        catch
        {
            // Сообщение может быть ещё не отрисовано.
        }
    }

    private void OnCancelActionClicked(object sender, EventArgs e)
    {
        ActionBar.IsVisible = false;
    }

    private void OnBubbleTapped(object sender, EventArgs e)
    {
        if (sender is not Border { BindingContext: SpaceMessageVm message })
        {
            return;
        }

        foreach (var m in _messages)
        {
            m.IsSelected = false;
        }

        message.IsSelected = true;
    }

    private async void OnActionCopyClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        try
        {
            await Clipboard.Default.SetTextAsync(message.Text);
        }
        catch
        {
            // Игнорируем ошибки буфера обмена
        }
    }

    private async void OnActionCopyLinkClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        try
        {
            var link = DeepLink.BuildMessageLink("space", _spaceId, message.Id, _topicId);
            await Clipboard.Default.SetTextAsync(link);
        }
        catch
        {
            // Игнорируем ошибки буфера обмена
        }
    }

    private void OnActionReplyClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        _replyingTo = message;
        ReplyPreviewLabel.Text = $"↩ Ответ на «{Shorten(message.ReplyToText, 70)}»";
        ReplyBar.IsVisible = true;
        MessageEntry.FocusInput();
    }

    private void OnCancelReplyClicked(object sender, EventArgs e)
    {
        _replyingTo = null;
        ReplyBar.IsVisible = false;
        MessageEntry.FocusInput();
    }

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private async void OnActionReactionClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        var emoji = await DisplayActionSheet(
            "Реакция:",
            "Отмена",
            null,
            "👍", "❤️", "😂", "😮", "😢", "🔥", "👏", "🙏");
        if (string.IsNullOrWhiteSpace(emoji) || emoji == "Отмена")
        {
            return;
        }

        try
        {
            await _spaces.SetReactionAsync(_spaceId, message.Id, emoji, true);
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnActionForwardClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        MessageEntry.Text = $"↩️ {message.Text}";
        MessageEntry.FocusInput();
    }

    private async void OnActionEditClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        ActionLabel.Text = $"Редактирование: {message.Text}";
        ActionBar.IsVisible = true;
        MessageEntry.Text = message.Text;
        MessageEntry.FocusInput();
    }

    private async void OnActionPinClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        try
        {
            await _spaces.SetPinnedAsync(_spaceId, message.Id, !message.IsPinned);
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnActionDeleteClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        var confirmed = await DisplayAlert(
            "Удалить сообщение?",
            "Сообщение будет удалено для всех участников.",
            "Удалить",
            "Отмена");
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _spaces.DeleteMessageAsync(_spaceId, message.Id);
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnOpenMediaClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message } || message.Media is null)
        {
            return;
        }

        try
        {
            var path = Path.Combine(FileSystem.CacheDirectory, message.Id + Path.GetExtension(message.FileName));

            byte[] bytes;
            if (File.Exists(path))
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            else
            {
                bytes = await _spaces.DownloadMediaAsync(message.Media);
                await File.WriteAllBytesAsync(path, bytes);

                if (message.IsImage)
                {
                    message.SetImageContent(bytes);
                }
            }

            message.MediaDownloaded = true;

            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
            {
                await DisplayAlert("Ошибка", "Вложение недоступно на этом устройстве.", "OK");
                return;
            }

            if (message.IsVoice)
            {
                await PlayVoiceInline(message, bytes);
                return;
            }

            if (message.IsVideoCircle)
            {
                await page.Navigation.PushModalAsync(
                    new NavigationPage(new MediaPlayerPage(path, message.FileName, message.Media?.ContentType, isVideoCircle: true)));
                return;
            }

            if (message.IsImage)
            {
                await page.Navigation.PushModalAsync(
                    new NavigationPage(new ImageViewerPage(bytes, message.FileName)));
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest(message.FileName, new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private static IVoicePlayer? CreateVoicePlayer()
    {
#if ANDROID
        return new VoicePlayerAndroid();
#elif WINDOWS
        return new VoicePlayerWindows();
#else
        return null;
#endif
    }

    private async Task PlayVoiceInline(SpaceMessageVm message, byte[] bytes)
    {
        StopVoicePlayback();

        var path = Path.Combine(FileSystem.CacheDirectory, message.Id + ".m4a");
        await File.WriteAllBytesAsync(path, bytes);

#if ANDROID || WINDOWS
        try
        {
            var player = CreateVoicePlayer()
                ?? throw new InvalidOperationException("Воспроизведение не поддерживается на этой платформе.");

            await player.OpenAsync(path);
            message.UpdatePlayback(0, player.DurationSeconds);

            player.Completed += (_, _) =>
                MainThread.BeginInvokeOnMainThread(StopVoicePlayback);

            _voicePlayer = player;
            _voicePlayingMessageId = message.Id;
            _voiceProgressMessageId = message.Id;
            message.IsPlaying = true;
            player.Play();
            StartVoiceProgressTimer(message);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось воспроизвести голосовое: {ex.Message}", "OK");
        }
#else
        await Task.CompletedTask;
        await DisplayAlert("Ошибка", "Воспроизведение голосовых на этой платформе недоступно.", "OK");
#endif
    }

#if ANDROID || WINDOWS
    private void StartVoiceProgressTimer(SpaceMessageVm message)
    {
        _voiceProgressTimer?.Stop();
        _voiceProgressTimer = new System.Timers.Timer(250);
        _voiceProgressTimer.Elapsed += (_, _) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var player = _voicePlayer;
                if (player is null || _voicePlayingMessageId != message.Id)
                {
                    return;
                }

                try
                {
                    var duration = player.DurationSeconds;
                    if (duration > 0)
                    {
                        message.UpdatePlayback(player.PositionSeconds, duration);
                    }
                }
                catch
                {
                    // Плеер мог быть освобождён между тиками таймера.
                }
            });
        };
        _voiceProgressTimer.Start();
    }
#endif

    private void OnVoiceProgressTapped(object sender, TappedEventArgs e)
    {
#if ANDROID || WINDOWS
        if (sender is not View view ||
            view.BindingContext is not SpaceMessageVm message ||
            _voicePlayer is null ||
            _voicePlayingMessageId != message.Id)
        {
            return;
        }

        var duration = _voicePlayer.DurationSeconds;
        if (duration <= 0 || view.Width <= 0)
        {
            return;
        }

        var point = e.GetPosition(view);
        if (point is null)
        {
            return;
        }

        var ratio = Math.Clamp(point.Value.X / view.Width, 0, 1);
        var position = ratio * duration;
        _voicePlayer.SeekSeconds(position);
        message.UpdatePlayback(position, duration);
#endif
    }

    private void StopVoicePlayback()
    {
#if ANDROID || WINDOWS
        if (_voicePlayer != null)
        {
            try
            {
                _voicePlayer.Release();
            }
            catch
            {
                // Игнорируем
            }
            _voicePlayer = null;
        }
        _voicePlayingMessageId = null;

        if (_voiceProgressMessageId != null)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == _voiceProgressMessageId);
            msg?.ResetPlayback();
            _voiceProgressMessageId = null;
        }

        _voiceProgressTimer?.Stop();
        _voiceProgressTimer = null;
#endif
    }

    private async void OnPlayVoiceClicked(object sender, EventArgs e)
    {
#if ANDROID || WINDOWS
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        // Нажатие на то же сообщение — пауза или продолжение воспроизведения.
        if (_voicePlayingMessageId == message.Id && _voicePlayer != null)
        {
            if (_voicePlayer.IsPlaying)
            {
                _voicePlayer.Pause();
                message.IsPlaying = false;
                _voiceProgressTimer?.Stop();
            }
            else
            {
                _voicePlayer.Play();
                message.IsPlaying = true;
                StartVoiceProgressTimer(message);
            }
            return;
        }

        StopVoicePlayback();

        var path = Path.Combine(FileSystem.CacheDirectory, message.Id + ".m4a");
        try
        {
            if (!File.Exists(path))
            {
                if (message.Media is null)
                {
                    await DisplayAlert("Ошибка", "Аудиофайл голосового сообщения не найден.", "OK");
                    return;
                }

                var bytes = await _spaces.DownloadMediaAsync(message.Media);
                await File.WriteAllBytesAsync(path, bytes);
            }

            await PlayVoiceInline(message, await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось воспроизвести голосовое: {ex.Message}", "OK");
        }
#else
        await Task.CompletedTask;
        await DisplayAlert("Ошибка", "Воспроизведение голосовых на этой платформе недоступно.", "OK");
#endif
    }

    /// <summary>Кнопка 😊 — показать или скрыть панель Эмодзи/Стикеры/GIF.</summary>
    private void OnEmojiButtonClicked(object sender, EventArgs e)
    {
        AttachMenuPanel.IsVisible = false;

        EmojiPicker.IsVisible = !EmojiPicker.IsVisible;
        StickerPicker.IsVisible = false;
        GifPicker.IsVisible = false;

        if (EmojiPicker.IsVisible)
        {
            MessageEntry.FocusInput();
        }
    }

    /// <summary>Панель своих пакетов (импорт своих GIF и стикеров).</summary>
    private async void OnCustomPackButtonClicked(object sender, EventArgs e)
    {
        AttachMenuPanel.IsVisible = false;
        EmojiPicker.IsVisible = false;
        StickerPicker.IsVisible = false;
        GifPicker.IsVisible = false;

        CustomPackPicker.IsVisible = !CustomPackPicker.IsVisible;
        if (CustomPackPicker.IsVisible)
        {
            await CustomPackPicker.ReloadAsync();
            MessageEntry.FocusInput();
        }
    }

    /// <summary>Импорт своих GIF или картинок-стикеров в локальный пак.</summary>
    private async void OnCustomPackImportRequested(string packKind)
    {
        try
        {
            var files = (await FilePicker.Default.PickMultipleAsync(
                new PickOptions
                {
                    PickerTitle = packKind == CustomPackService.GifPackKind
                        ? "Выберите свои GIF"
                        : "Выберите свои стикеры (PNG, WebP, JPG)",
                    FileTypes = FilePickerFileType.Images
                })).ToList();

            if (files.Count == 0)
            {
                return;
            }

            var service = AppServices.Get<CustomPackService>();
            var imported = 0;
            foreach (var file in files)
            {
                try
                {
                    await using var stream = await file.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    var data = buffer.ToArray();
                    if (data.Length == 0 || data.Length > CustomPackService.MaxItemBytes)
                    {
                        continue;
                    }

                    var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                        ? "application/octet-stream"
                        : file.ContentType;
                    await service.ImportAsync(
                        packKind,
                        CustomPackService.DefaultPackName(packKind),
                        [(file.FileName ?? "file", contentType, data)]);
                    imported++;
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Ошибка", $"Не удалось импортировать {file.FileName}: {ex.Message}", "OK");
                }
            }

            if (imported > 0)
            {
                await CustomPackPicker.ReloadAsync();
                await DisplayAlert("Готово", $"Импортировано файлов: {imported}", "OK");
            }
        }
        catch
        {
            // Отмена выбора — не ошибка.
        }
    }

    /// <summary>Файл из своего пака: отправляем как шифрованное вложение в пространство.</summary>
    private async void OnCustomPackItemPicked(CustomPackPickerView.PackItem item)
    {
        if (item.Bytes is not { Length: > 0 } || _sendingAttachment)
        {
            return;
        }

        _sendingAttachment = true;
        try
        {
            EmojiPicker.IsVisible = false;
            StickerPicker.IsVisible = false;
            GifPicker.IsVisible = false;
            CustomPackPicker.IsVisible = false;

            await _spaces.SendMediaAsync(
                _spaceId,
                _topicId,
                item.FileName,
                item.ContentType,
                item.Bytes,
                string.Empty);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось отправить файл из пака: {ex.Message}", "OK");
        }
        finally
        {
            _sendingAttachment = false;
        }
    }

    /// <summary>Эмодзи из панели — вставить в позицию курсора поля ввода.</summary>
    private void OnEmojiPicked(string emoji)    {
        var text = MessageEntry.Text ?? string.Empty;
        var position = Math.Clamp(MessageEntry.CursorPosition, 0, text.Length);
        MessageEntry.Text = text.Insert(position, emoji);
        MessageEntry.CursorPosition = Math.Min(position + emoji.Length, MessageEntry.Text.Length);
        MessageEntry.FocusInput();
    }

    /// <summary>Стикер из панели — отправляем как сообщение с Kind=Sticker.</summary>
    private async void OnStickerPicked(string glyph)
    {
        if (string.IsNullOrWhiteSpace(glyph)) return;
        EmojiPicker.IsVisible = false;
        StickerPicker.IsVisible = false;
        GifPicker.IsVisible = false;

        try
        {
            var payload = new SpaceMessagePayload
            {
                Kind = MessageKinds.Sticker,
                Text = glyph,
                ReplyToMessageId = _replyingTo?.Id,
                ReplyToText = _replyingTo?.ReplyToText,
                ReplyToSender = _replyingTo?.Sender,
                Silent = _silentPosting,
                Mentions = SpaceMentions.Parse(glyph, _members.Select(member => member.Username))
            };

            _silentPosting = false;
            ResetSilentButton();

            await _spaces.SendPayloadAsync(_spaceId, _topicId, payload);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось отправить стикер: {ex.Message}", "OK");
        }
    }

    /// <summary>GIF из панели — отправляем как медиа-вложение.</summary>
    private async void OnGifPicked(GifPickerView.GifItem item)
    {
        if (item.Bytes.Length == 0) return;
        EmojiPicker.IsVisible = false;
        StickerPicker.IsVisible = false;
        GifPicker.IsVisible = false;

        EnqueueMediaItem(item.FileName, item.ContentType, item.Bytes, MessageKinds.File);

        if (_pendingMedia.Count > 0)
        {
            SendQueuePanel.IsVisible = true;
            _ = ProcessMediaQueueAsync();
        }
    }

    private async void OnAttachClicked(object sender, EventArgs e)
    {
        if (AttachMenuPanel.IsVisible)
        {
            AttachMenuPanel.IsVisible = false;
            return;
        }

        if (_sendingAttachment)
        {
            return;
        }

        AttachMenuPanel.IsVisible = true;
    }

    /// <summary>Выбор пункта меню скрепки: фото/видео, файл, музыка, голосовое, кружок.</summary>
    private async void OnAttachMenuItemTapped(object sender, EventArgs e)
    {
        var action = (sender as Element)?.StyleId;
        AttachMenuPanel.IsVisible = false;
        if (_sendingAttachment)
        {
            return;
        }

        _sendingAttachment = true;
        try
        {
            switch (action)
            {
                case "photo":
                    var mediaFiles = await FilePicker.Default.PickMultipleAsync(
                        new PickOptions { PickerTitle = "Выберите фото или видео" });
                    if (mediaFiles is null || !mediaFiles.Any()) return;
                    await SendAttachmentsAsync(mediaFiles, isPhoto: true);
                    break;

                case "file":
                    var files = await FilePicker.Default.PickMultipleAsync(
                        new PickOptions { PickerTitle = "Выберите файлы" });
                    if (files is null || !files.Any()) return;
                    await SendAttachmentsAsync(files, isPhoto: false);
                    break;

                case "music":
                    var audioFiles = await FilePicker.Default.PickMultipleAsync(
                        new PickOptions { PickerTitle = "Выберите музыку" });
                    if (audioFiles is null || !audioFiles.Any()) return;
                    await SendAttachmentsAsync(audioFiles, isPhoto: false);
                    break;

                case "voice":
                    var voice = await RecordVoiceMessageAsync(this);
                    if (voice is null) return;
                    EnqueueMediaItem("voice.m4a", voice.Value.ContentType, voice.Value.Data, MessageKinds.Voice);
                    break;

                case "circle":
                    var video = await RecordVideoCircleAsync();
                    if (video is null) return;
                    EnqueueMediaItem("circle.mp4", video.Value.ContentType, video.Value.Data, MessageKinds.VideoCircle);
                    break;
            }
        }
        finally
        {
            _sendingAttachment = false;
        }

        if (_pendingMedia.Count > 0)
        {
            SendQueuePanel.IsVisible = true;
            _ = ProcessMediaQueueAsync();
        }
    }

    private void EnqueueMediaItem(string fileName, string contentType, byte[] data, string? kind)
    {
        var prepared = kind is null
            ? MediaCompression.CompressImage(data, contentType)
            : (Data: data, ContentType: contentType);

        _pendingMedia.Add(new PendingMediaItem
        {
            FileName = fileName,
            Data = prepared.Data,
            ContentType = prepared.ContentType,
            MessageKind = kind
        });
    }

    /// <summary>
    /// Последовательно загружает файлы из очереди пространства с индикатором
    /// прогресса и возможностью отмены каждого файла.
    /// </summary>
    private async Task ProcessMediaQueueAsync()
    {
        if (_processingMediaQueue)
        {
            return;
        }

        _processingMediaQueue = true;
        try
        {
            while (_pendingMedia.Count > 0)
            {
                var item = _pendingMedia[0];

                if (item.Cts.Token.IsCancellationRequested)
                {
                    _pendingMedia.Remove(item);
                    MainThread.BeginInvokeOnMainThread(() => SendQueuePanel.IsVisible = _pendingMedia.Count > 0);
                    continue;
                }

                var progress = new Progress<int>(p =>
                    MainThread.BeginInvokeOnMainThread(() => item.Progress = p));

                try
                {
                    await _spaces.SendMediaAsync(
                        _spaceId,
                        _topicId,
                        item.FileName,
                        item.ContentType,
                        item.Data,
                        string.Empty,
                        messageKind: item.MessageKind,
                        progress: progress,
                        cancellationToken: item.Cts.Token);

                    item.Progress = 100;
                    item.Status = "Готово";
                    await Task.Delay(350);
                    _pendingMedia.Remove(item);
                }
                catch (OperationCanceledException)
                {
                    _pendingMedia.Remove(item);
                }
                catch (Exception ex)
                {
                    item.Status = "Ошибка";
                    MainThread.BeginInvokeOnMainThread(async () =>
                        await DisplayAlert("Ошибка", $"Не удалось отправить «{item.FileName}»: {ex.Message}", "OK"));
                    _pendingMedia.Remove(item);
                }

                MainThread.BeginInvokeOnMainThread(() => SendQueuePanel.IsVisible = _pendingMedia.Count > 0);
            }
        }
        finally
        {
            _processingMediaQueue = false;
        }
    }

    private void OnCancelMediaClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string id })
        {
            var item = _pendingMedia.FirstOrDefault(i => i.Id == id);
            if (item is not null)
            {
                item.Cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Читает выбранные файлы и ставит их в очередь отправки: фото
    /// пережимаются, слишком большие пропускаются с сообщением.
    /// </summary>
    private async Task SendAttachmentsAsync(IEnumerable<FileResult> picked, bool isPhoto)
    {
        var items = new List<(string FileName, byte[] Data, string ContentType, string? Kind)>();
        foreach (var file in picked)
        {
            var data = await ReadAttachmentAsync(file);
            if (data is null)
            {
                await DisplayAlert("Ошибка", $"«{file.FileName}» слишком большой.", "OK");
                continue;
            }

            var contentType = ResolveContentType(file);
            string? kind;
            if (isPhoto)
            {
                kind = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : MessageKinds.File;
            }
            else
            {
                kind = MessageKinds.File;
            }

            items.Add((file.FileName, data, contentType, kind));
        }

        if (items.Count == 0)
        {
            return;
        }

        foreach (var (fileName, data, contentType, kind) in items)
        {
            EnqueueMediaItem(fileName, contentType, data, kind);
        }

        SendQueuePanel.IsVisible = true;
        _ = ProcessMediaQueueAsync();
    }

    private async Task<(byte[] Data, string ContentType)?> RecordVoiceMessageAsync(Page page)
    {
#if WINDOWS
        if (!await WindowsRecording.EnsureMicrophoneAllowedAsync(page))
        {
            return null;
        }
#else
        try
        {
            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Ошибка", "Нет права на запись аудио. Разрешите доступ к микрофону в настройках.", "OK");
                return null;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось запросить право на микрофон: {ex.Message}", "OK");
            return null;
        }
#endif

        var resources = Application.Current!.Resources;
        var cts = new CancellationTokenSource();
        var recordingTask = _audioRecorder.RecordAsync(cts.Token);
        var startedAt = DateTime.UtcNow;

        var timerLabel = new Label
        {
            Text = "0:00",
            FontSize = 34,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center
        };

        var statusLabel = new Label
        {
            Text = "Идёт запись",
            FontSize = 13,
            TextColor = Color.FromArgb("#B3FFFFFF"),
            VerticalOptions = LayoutOptions.Center
        };

        var redDot = new BoxView
        {
            WidthRequest = 9,
            HeightRequest = 9,
            CornerRadius = 5,
            Color = (Color)resources["Danger"],
            VerticalOptions = LayoutOptions.Center
        };

        var sendButton = new Button
        {
            Text = "Отправить",
            BackgroundColor = (Color)resources["Accent"],
            TextColor = Colors.White,
            CornerRadius = 18,
            HeightRequest = 38,
            FontSize = 13,
            Padding = new Thickness(16, 0)
        };

        var cancelButton = new Button
        {
            Text = "Отмена",
            BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#B3FFFFFF"),
            CornerRadius = 18,
            HeightRequest = 38,
            FontSize = 13,
            Padding = new Thickness(16, 0)
        };

        var card = new Border
        {
            BackgroundColor = (Color)resources["Surface"],
            StrokeThickness = 0,
            Padding = new Thickness(24, 20),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 },
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        HorizontalOptions = LayoutOptions.Center,
                        Children = { redDot, statusLabel }
                    },
                    timerLabel,
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        HorizontalOptions = LayoutOptions.Center,
                        Children = { cancelButton, sendButton }
                    }
                }
            }
        };

        var recordingPage = new ContentPage
        {
            BackgroundColor = Color.FromArgb("#B3000000"),
            Content = new Grid { Children = { card } }
        };

        var result = new TaskCompletionSource<bool>();
        var finished = false;
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - startedAt;
            timerLabel.Text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        };

        void Finish(bool send)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            timer.Stop();
            cts.Cancel();
            result.TrySetResult(send);
        }

        sendButton.Clicked += (_, _) => Finish(true);
        cancelButton.Clicked += (_, _) => Finish(false);
        recordingPage.Disappearing += (_, _) => Finish(false);

        timer.Start();
        await page.Navigation.PushModalAsync(recordingPage);

        var send = await result.Task;
        if (recordingPage.Navigation.ModalStack.Contains(recordingPage))
        {
            await page.Navigation.PopModalAsync();
        }
        timer.Stop();

        string? path = null;
        try
        {
            path = await recordingTask;
        }
        catch (Exception ex) when (send)
        {
            await DisplayAlert("Ошибка", $"Ошибка записи голосового: {ex.Message}", "OK");
        }
        catch
        {
            // Отмена записи — ожидаемая ситуация.
        }

        if (!send || string.IsNullOrEmpty(path))
        {
            DeleteQuietly(path);
            return null;
        }

        try
        {
            var data = await File.ReadAllBytesAsync(path);
            DeleteQuietly(path);
            return (data, "audio/mp4");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось прочитать запись: {ex.Message}", "OK");
            return null;
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Временный файл мог быть уже удалён.
        }
    }

    private async Task<(byte[] Data, string ContentType)?> RecordVideoCircleAsync()
    {
#if WINDOWS || ANDROID
#if WINDOWS
        if (!await WindowsRecording.EnsureCameraReadyAsync(this))
        {
            return null;
        }
#endif
        try
        {
            var status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlert("Ошибка", "Нет права на камеру. Разрешите доступ к камере в настройках.", "OK");
                return null;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось запросить право на камеру: {ex.Message}", "OK");
            return null;
        }

        try
        {
            // Системная камера показывает предпросмотр и даёт остановить/подтвердить запись.
            var captured = await MediaPicker.Default.CaptureVideoAsync(new MediaPickerOptions
            {
                Title = "Запись видеокружка"
            });

            if (captured is null)
            {
                return null;
            }

            await using var stream = await captured.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            if (buffer.Length == 0)
            {
                await DisplayAlert("Ошибка", "Камера вернула пустой видеофайл.", "OK");
                return null;
            }

            if (buffer.Length > MediaLimits.MaxBytes)
            {
                await DisplayAlert("Ошибка", $"Видео слишком большое. Максимум — {MediaLimits.MaxBytes / (1024 * 1024)} МБ.", "OK");
                return null;
            }

            var contentType = !string.IsNullOrWhiteSpace(captured.ContentType) &&
                              captured.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? captured.ContentType
                : "video/mp4";

            return (buffer.ToArray(), contentType);
        }
        catch (FeatureNotSupportedException)
        {
            await DisplayAlert("Ошибка", "На устройстве нет приложения камеры, поддерживающего запись видео.", "OK");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось записать видео: {ex.Message}", "OK");
            return null;
        }
#else
        await Task.CompletedTask;
        await DisplayAlert("Видеокружок", "Запись видеокружков на этой платформе недоступна.", "OK");
        return null;
#endif
    }

    private static string ResolveContentType(FileResult file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType) &&
            !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return file.ContentType;
        }
        return Path.GetExtension(file.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
    }

    private async void OnUnpinBannerClicked(object sender, EventArgs e)
    {
        if (_pinnedMessage is null)
        {
            return;
        }

        try
        {
            var ok = await _spaces.SetPinnedAsync(_spaceId, _pinnedMessage.Id, false);
            if (ok)
            {
                PinnedBanner.IsVisible = false;
                _pinnedMessage.IsPinned = false;
                _pinnedMessage = null;
            }
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async void OnTopicsClicked(object sender, EventArgs e)
    {
        var topics = await _spaces.GetTopicsAsync(_spaceId);
        if (topics.Count == 0)
        {
            var title = await DisplayPromptAsync(
                "Новая тема",
                "Введите название темы:",
                "Создать",
                "Отмена",
                maxLength: 120,
                keyboard: Keyboard.Text);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            var created = await _spaces.CreateTopicAsync(_spaceId, title, null);
            if (created is not null)
            {
                _topicId = created.Id;
                _topicTitle = created.Title;
                await LoadAsync();
            }

            return;
        }

        var options = new List<string> { "＋ Новая тема" };
        options.AddRange(topics.Select(TopicLabel));
        if (_canModerate)
        {
            options.Add("✏️ Переименовать тему…");
            options.Add("🔒 Закрыть или открыть тему…");
            options.Add("⬆️ Переместить тему выше");
            options.Add("⬇️ Переместить тему ниже");
            options.Add("🗑 Удалить тему…");
        }

        var selected = await DisplayActionSheet(
            "Темы форума",
            "Отмена",
            null,
            options.ToArray());
        if (string.IsNullOrWhiteSpace(selected) || selected == "Отмена")
        {
            return;
        }

        if (selected == "✏️ Переименовать тему…")
        {
            await RenameTopicAsync(topics);
            return;
        }

        if (selected == "🔒 Закрыть или открыть тему…")
        {
            await ToggleTopicClosedAsync(topics);
            return;
        }

        if (selected == "⬆️ Переместить тему выше" || selected == "⬇️ Переместить тему ниже")
        {
            await MoveTopicAsync(topics, selected == "⬆️ Переместить тему выше" ? -1 : 1);
            return;
        }

        if (selected == "🗑 Удалить тему…")
        {
            var target = await PickTopicAsync(topics, "Какую тему удалить? Все её сообщения будут стёрты.");
            if (target is null)
            {
                return;
            }

            var doomed = target;
            var confirmed = await DisplayAlert(
                "Удалить тему",
                $"Тема «{doomed.Title}» и все её сообщения исчезнут для всех участников.",
                "Удалить", "Отмена");
            if (!confirmed)
            {
                return;
            }

            if (await _spaces.DeleteTopicAsync(_spaceId, doomed.Id) &&
                string.Equals(_topicId, doomed.Id, StringComparison.Ordinal))
            {
                _topicId = null;
                _topicTitle = null;
            }

            await LoadAsync();
            return;
        }

        if (selected == "＋ Новая тема")
        {
            var title = await DisplayPromptAsync(
                "Новая тема",
                "Введите название темы:",
                "Создать",
                "Отмена",
                maxLength: 120,
                keyboard: Keyboard.Text);
            if (!string.IsNullOrWhiteSpace(title))
            {
                var created = await _spaces.CreateTopicAsync(_spaceId, title, null);
                if (created is not null)
                {
                    _topicId = created.Id;
                    _topicTitle = created.Title;
                    await LoadAsync();
                }
            }

            return;
        }

        // Иначе выбрана тема: ищем её по позиции в списке (у закрытых — замок).
        var index = options.IndexOf(selected) - 1;
        var topic = index >= 0 && index < topics.Count ? topics[index] : null;
        if (topic is not null)
        {
            _topicId = topic.Id;
            _topicTitle = topic.Title;
            _currentTopicClosed = topic.IsClosed;
            await LoadAsync();
        }
    }

    /// <summary>Подпись темы в списке: у закрытой — замок.</summary>
    private static string TopicLabel(TopicDto topic) =>
        topic.IsClosed ? $"{topic.Title}  🔒" : topic.Title;

    /// <summary>Выбор темы из списка (с заголовком-вопросом).</summary>
    private async Task<TopicDto?> PickTopicAsync(IReadOnlyList<TopicDto> topics, string question)
    {
        if (topics.Count == 0)
        {
            return null;
        }

        var labels = topics.Select(TopicLabel).ToArray();
        var selected = await DisplayActionSheet(question, "Отмена", null, labels);
        if (string.IsNullOrWhiteSpace(selected) || selected == "Отмена")
        {
            return null;
        }

        var index = Array.IndexOf(labels, selected);
        return index >= 0 && index < topics.Count ? topics[index] : null;
    }

    /// <summary>Переименование темы форума.</summary>
    private async Task RenameTopicAsync(IReadOnlyList<TopicDto> topics)
    {
        var target = await PickTopicAsync(topics, "Какую тему переименовать?");
        if (target is null)
        {
            return;
        }

        var title = await DisplayPromptAsync(
            "Переименовать тему",
            "Новое название темы:",
            "Сохранить",
            "Отмена",
            maxLength: 120,
            initialValue: target.Title,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var updated = await _spaces.RenameTopicAsync(_spaceId, target.Id, title);
        if (updated is null)
        {
            await DisplayAlert("Не удалось", "Не хватает прав или тема не найдена.", "Ок");
            return;
        }

        if (string.Equals(_topicId, target.Id, StringComparison.Ordinal))
        {
            _topicTitle = updated.Title;
        }

        await LoadAsync();
    }

    /// <summary>Закрытие и открытие темы форума.</summary>
    private async Task ToggleTopicClosedAsync(IReadOnlyList<TopicDto> topics)
    {
        var target = await PickTopicAsync(topics, "Какую тему закрыть или открыть?");
        if (target is null)
        {
            return;
        }

        var updated = await _spaces.SetTopicClosedAsync(_spaceId, target.Id, !target.IsClosed);
        if (updated is null)
        {
            await DisplayAlert("Не удалось", "Не хватает прав или тема не найдена.", "Ок");
            return;
        }

        if (string.Equals(_topicId, target.Id, StringComparison.Ordinal))
        {
            _currentTopicClosed = updated.IsClosed;
        }

        await LoadAsync();
    }

    /// <summary>Перестановка темы в списке форума.</summary>
    private async Task MoveTopicAsync(IReadOnlyList<TopicDto> topics, int shift)
    {
        var target = await PickTopicAsync(topics, "Какую тему переместить?");
        if (target is null)
        {
            return;
        }

        var ordered = topics.OrderBy(topic => topic.Position).ThenBy(topic => topic.Title).ToList();
        var index = ordered.FindIndex(topic => topic.Id == target.Id);
        var newIndex = index + shift;
        if (index < 0 || newIndex < 0 || newIndex >= ordered.Count)
        {
            return;
        }

        // Меняем местами с соседней темой.
        var other = ordered[newIndex];
        var updated = await _spaces.SetTopicPositionAsync(_spaceId, other.Id, target.Position);
        if (updated is null)
        {
            await DisplayAlert("Не удалось", "Не хватает прав или тема не найдена.", "Ок");
            return;
        }

        await _spaces.SetTopicPositionAsync(_spaceId, target.Id, other.Position);
        await LoadAsync();
    }

    private async Task<byte[]?> ReadAttachmentAsync(FileResult file)
    {
        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > MediaLimits.MaxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MediaLimits.MaxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

