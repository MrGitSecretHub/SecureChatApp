using SecureChat.App.Pages;
using SecureChat.Core.Net;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using Microsoft.Maui.ApplicationModel;
using SecureChat.App.Services;
using SecureChat.App.Views;
using SecureChat.Core.Contracts;
using SecureChat.Core.Serialization;

#if ANDROID
using Android.Media;
#endif

namespace SecureChat.App.Pages;

/// <summary>Переписка в пространстве: группа, канал, сообщество или форум.</summary>
public partial class SpaceChatPage : ContentPage
{
    private readonly SpaceService _spaces = AppServices.Get<SpaceService>();
    private readonly LocalStore _localStore = AppServices.Get<LocalStore>();
    private readonly ChatService _chatService = AppServices.Get<ChatService>();
    private readonly Dictionary<string, (PollVoteData Data, string Voter)> _spaceVotes = new();
    private readonly HashSet<string> _mediaDownloadErrors = new(StringComparer.Ordinal);
    private readonly ChatManager _chat = AppServices.Get<ChatManager>();
    private readonly SecureChat.App.Scheduling.ScheduledPostService _scheduledPosts =
        AppServices.Get<SecureChat.App.Scheduling.ScheduledPostService>();
    private readonly LinkPreviewService _previewService = AppServices.Get<LinkPreviewService>();
    private readonly IAudioRecorder _audioRecorder = AppServices.Get<IAudioRecorder>();
    private readonly ObservableCollection<SpaceMessageVm> _messages = new();
    private readonly ObservableCollection<TopicVm> _topics = new();
    private readonly List<TopicVm> _allTopics = new();
    private int _topicSortMode;
    private bool _forumPaneVisible;
    private bool _suppressTopicSelection;
    private readonly string _spaceId;
    private readonly string _spaceName;
    private readonly string _spaceType;
    private SpaceDto? _community;
    private bool _signMessages;
    private bool _silentPosting;
    private string? _topicId;
    private string? _loadedTopicId;
    private string? _topicTitle;

    /// <summary>Текущая тема закрыта — в неё нельзя писать.</summary>
    private IDispatcherTimer? _keyRetryTimer;
    private IDispatcherTimer? _spaceRefreshTimer;
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

    private CancellationTokenSource? _sendLongPressCts;
    private bool _sendLongPressTriggered;

    /// <summary>Один элемент очереди отправки с прогрессом и возможностью отмены.</summary>
    public sealed class PendingMediaItem : INotifyPropertyChanged
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string? MessageKind { get; set; }
        public SpaceMessageVm? MessageVm { get; set; }
        public CancellationTokenSource Cts { get; } = new();

        private int _progress;
        public int Progress
        {
            get => _progress;
            set { if (_progress != value) { _progress = value; Notify(); } }
        }

        private string _status = Loc.T("rs_116", "Отправка…");
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
        SetupSendLongPress();
        LoadingPlaceholder.Content = new ActivityIndicator
        {
            IsRunning = true,
            Color = (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        _spaceId = spaceId;
        _spaceName = name;
        _spaceType = type;
        _canPost = canPost;

        Title = name;
        SpaceTitleLabel.Text = name;
        _myUserId = AppServices.Get<AppSession>().User?.Id;

        var isSections = type is SpaceTypes.Forum or SpaceTypes.Community;
        TopicsButton.IsVisible = isSections;
        if (type == SpaceTypes.Community)
        {
            TopicsPaneTitle.Text = Loc.T("community_channels", "Channels");
            ToolTipProperties.SetText(AddTopicButton, Loc.T("community_add_channel", "New channel"));
            TopicSearchBar.Placeholder = Loc.T("community_search_channels", "Search channels");
        }
        TopicSortPicker.ItemsSource = new[]
        {
            Loc.T("forum_sort_position", "By order"),
            Loc.T("forum_sort_activity", "By activity"),
            Loc.T("forum_sort_unread", "Unread first"),
            Loc.T("forum_sort_name", "By name")
        };
        TopicSortPicker.SelectedIndex = 0;
        TopicsView.ItemsSource = _topics;
        MessagesView.ItemsSource = _messages;
        if (isSections &&
            Microsoft.Maui.Devices.DeviceInfo.Platform == Microsoft.Maui.Devices.DevicePlatform.WinUI)
        {
            _forumPaneVisible = true;
            ForumTopicsPane.IsVisible = true;
            ApplyForumPaneLayout();
        }
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
        _spaces.SpaceActivityReceived += OnSpaceActivityReceived;
        _spaces.SpaceKeyRotated += OnSpaceKeyRotated;

        // TCP-транспорт: события пространств приходят не через SignalR.
        _chatService.Tcp.SpaceEventReceived += OnTcpSpaceEvent;

        // Отложенные посты публикуются фоновой службой — обновляем ленту сразу,
        // не дожидаясь перезахода в канал.
        _scheduledPosts.PostSent += OnScheduledPostSent;

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
        CancelSendLongPress();
        StopVoicePlayback();
        _spaces.SpaceMessageReceived -= OnSpaceMessageReceived;
        _spaces.SpaceMessageUpdated -= OnSpaceMessageUpdated;
        _spaces.SpaceMessageDeleted -= OnSpaceMessageDeleted;
        _spaces.TopicUpdated -= OnTopicUpdated;
        _spaces.SpaceActivityReceived -= OnSpaceActivityReceived;
        _spaces.SpaceKeyRotated -= OnSpaceKeyRotated;
        _scheduledPosts.PostSent -= OnScheduledPostSent;
        _chatService.Tcp.SpaceEventReceived -= OnTcpSpaceEvent;
        AppServices.Get<AppSession>().AppearanceChanged -= OnAppearanceChanged;
    }

    /// <summary>Тему переименовали, закрыли или переставили — обновляем ленту.</summary>
    /// <summary>Отложенный пост опубликован — добавляем его в открытую ленту сразу.</summary>
    private void OnScheduledPostSent(string spaceId, SpaceMessageDto message)
    {
        if (!string.Equals(spaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        if (_topicId is not null &&
            !string.Equals(message.TopicId, _topicId, StringComparison.Ordinal))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_messages.Any(item => item.Id == message.Id))
            {
                return;
            }

            var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
            if (payload is null)
            {
                await LoadAsync();
                return;
            }

            _messages.Add(CreateVm(message, payload));
            RefreshDateLabels();
            MessagesView.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: false);
            HideScrollToBottom();
        });
    }

    private void OnTopicUpdated(TopicDto topic)
    {
        if (!string.Equals(topic.SpaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_forumPaneVisible) await RefreshTopicsAsync();
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


    private void OnSpaceKeyRotated(string spaceId)
    {
        if (!string.Equals(spaceId, _spaceId, StringComparison.Ordinal)) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _spaces.InvalidateSpaceKey(_spaceId);
            await ShowHintAsync(Loc.T("key_rotated", "Ключ пространства сменён."), isError: false);
            await LoadAsync();
        });
    }

    private async Task ShowHintAsync(string text, bool isError)
    {
        ActivityLabel.Text = text;
        ActivityLabel.TextColor = isError
            ? (Color?)Application.Current?.Resources["Danger"] ?? Colors.Red
            : (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue;
        ActivityLabel.IsVisible = true;
        ActivityLabel.Opacity = 1;
        try { await ActivityLabel.FadeTo(0, 2600); } catch { }
        ActivityLabel.IsVisible = false;
        ActivityLabel.Opacity = 1;
    }

    private void OnSpaceActivityReceived(string spaceId, string username, string activity)
    {
        if (!string.Equals(spaceId, _spaceId, StringComparison.Ordinal)) return;
        var key = activity switch
        {
            "uploading_photo" => "act_upload_photo",
            "uploading_video" => "act_upload_video",
            "recording_voice" => "act_record_voice",
            "recording_video" => "act_record_video",
            "seen" => "act_seen",
            _ => "rs_006"
        };
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            ActivityLabel.Text = string.Format(Loc.T(key), username);
            ActivityLabel.IsVisible = true;
            ActivityLabel.Opacity = 1;
            try
            {
                await ActivityLabel.FadeTo(0, 400);
            }
            catch { }
            ActivityLabel.IsVisible = false;
            ActivityLabel.Opacity = 1;
        });
    }

    private void SendActivity(string activity) =>
        _ = _spaces.NotifyActivityAsync(_spaceId, activity);

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
            _community = (await _spaces.ListAsync()).FirstOrDefault(s => s.Id == _spaceId);
            _signMessages = _community?.SignMessages == true;

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
            if (await _spaces.GetSpaceKeyAsync(_spaceId) is null &&
                (await _localStore.GetCachedSpaceMessagesAsync(_spaceId, _topicId)).Count == 0)
            {
                _messages.Clear();
                Composer.IsVisible = false;
                ReadOnlyBanner.IsVisible = false;
                KeyWaitBanner.IsVisible = true;
                SpaceInfoLabel.Text = string.Format(Loc.T("uc_000"), members.Count);
                return;
            }

                        // Админ/владелец при открытии чата раздаёт ключ новичкам,
            // которые вступили, когда никто из участников не был онлайн.
            _ = _spaces.EnsureKeyDistributedAsync(_spaceId);
KeyWaitBanner.IsVisible = false;
            OfflineBanner.IsVisible = false;
            // Сначала пробуем сервер; расшифрованное сохраняем в локальный кэш.
            // Если сети нет или сервер вернул пусто — берём кэш (он уже расшифрован,
            // поэтому ключ пространства для чтения не нужен).
            var source = new List<(SpaceMessageDto Message, SpaceMessagePayload? Payload)>();
            var cacheItems = new List<(SpaceMessageDto, string)>();
            var fromCache = false;
            try
            {
                var remote = await _spaces.GetMessagesAsync(_spaceId, _topicId);
                if (remote.Count > 0)
                {
                    foreach (var message in remote)
                    {
                        SpaceMessagePayload? payload;
                        try
                        {
                            payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
                        }
                        catch
                        {
                            payload = null;
                        }

                        source.Add((message, payload));
                        if (payload is not null)
                            cacheItems.Add((message, SpacePayloadCodec.Encode(payload)));
                    }
                    if (cacheItems.Count > 0)
                        _ = _localStore.CacheSpaceMessagesAsync(_spaceId, cacheItems);
                }
                else
                {
                    fromCache = true;
                }
            }
            catch
            {
                fromCache = true;
            }

            if (fromCache)
            {
                var cached = await _localStore.GetCachedSpaceMessagesAsync(_spaceId, _topicId);
                source = cached.Select(c => (
                    c.Message,
                    (SpaceMessagePayload?)SpacePayloadCodec.Decode(c.PayloadJson))).ToList();
            }

            var commentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var feed = new List<SpaceMessageVm>();
            _spaceVotes.Clear();

            foreach (var (message, payload) in source)
            {
                if (payload?.CommentToMessageId is not null)
                {
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
                        Text = Loc.T("rs_216", "🔒 Сообщение не удалось расшифровать")
                    }));
            }

            // Сервер иногда отдаёт пустой список — не стираем показанные сообщения.
            if (source.Count == 0 && _messages.Count > 0 &&
                string.Equals(_loadedTopicId, _topicId, StringComparison.Ordinal))
            {
                HideScrollToBottom();
                return;
            }

            _loadedTopicId = _topicId;
            _messages.Clear();
            foreach (var item in feed)
            {
                _messages.Add(item);
            }

            foreach (var item in _messages.Where(x =>
                         x.IsImage && x.Media is not null && !x.MediaDownloaded))
            {
                _ = AutoDownloadImageAsync(item);
            }

            ApplySpacePollVotes();
            _ = RecordChannelViewsAsync();
            RefreshDateLabels();
            if (_spaceType == SpaceTypes.Forum && _topicId is not null)
            {
                var lastRead = _messages.Count == 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    : _messages.Max(m => m.SentAtUnixMs);
                await _localStore.MarkSpaceTopicReadAsync(_spaceId, _topicId, lastRead);
            }
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
            // Отложенные посты доступны во всех типах пространств, где можно писать:
            // группы, каналы, темы форума и сообщества.
            ScheduleButton.IsVisible = false;
// Закрытая тема форума: писать в неё нельзя.
            TopicClosedBanner.IsVisible = _currentTopicClosed && _canPost;
            TopicClosedLabel.Text = _topicTitle is null
                ? Loc.T("ui_161", "Topic closed — you cannot post here.")
                : string.Format(Loc.T("rs_156"), _topicTitle);

            var isSection = _spaceType is SpaceTypes.Forum or SpaceTypes.Community && _topicId is not null;
            var sectionIcon = _spaceType == SpaceTypes.Community ? "🌐" : "🧵";
            SpaceTitleLabel.Text = isSection ? _topicTitle ?? _spaceName : _spaceName;
            SpaceInfoLabel.Text = isSection
                ? $"{sectionIcon} {_spaceName} · {string.Format(Loc.T("uc_000"), members.Count)}"
                : string.Format(Loc.T("uc_000"), members.Count);

            TopicInfoCard.IsVisible = isSection;
            if (isSection)
            {
                TopicInfoTitle.Text = _topicTitle ?? string.Empty;
                var topic = _topics.FirstOrDefault(t => t.Id == _topicId);
                var createdBy = topic?.CreatedByUsername ?? string.Empty;
                var subtitle = string.Format(Loc.T("forum_created_by", "Started by {0}"), createdBy);
                if (topic is not null)
                    subtitle += " · " + string.Format(Loc.T("forum_messages_count", "{0} messages"), topic.MessageCount);
                TopicInfoSubtitle.Text = subtitle;
                TopicInfoLock.IsVisible = _currentTopicClosed;
            }

            var showCommunityCard = _spaceType == SpaceTypes.Community && _topicId is null;
            CommunityInfoCard.IsVisible = showCommunityCard;
            if (showCommunityCard)
            {
                CommunityInfoTitle.Text = _spaceName;
                CommunityInfoMeta.Text = string.Format(
                    Loc.T("community_members_channels", "{0} members · {1} channels"),
                    _community?.MemberCount ?? members.Count,
                    _topics.Count > 0 ? _topics.Count : _community?.TopicCount ?? 0);
                CommunityInfoDescription.Text = _community?.Description ?? string.Empty;
                CommunityLeaveButton.IsVisible =
                    _community?.MyRole is not null &&
                    _community.MyRole != SpaceRoles.Owner;
            }
            else
            {
                CommunityLeaveButton.IsVisible = false;
            }

            EmptyStatePanel.IsVisible = isSection && _messages.Count == 0;
            MessageEntry.Placeholder = isSection
                ? Loc.T("forum_message_placeholder", "Message in topic")
                : Loc.T("ui_153", "Message in space");
            if (_forumPaneVisible) await RefreshTopicsAsync();

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
        finally
        {
            ((ActivityIndicator)LoadingPlaceholder.Content).IsRunning = false;
            LoadingPlaceholder.IsVisible = false;
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
                ? string.Format(Loc.T("rs_002"), _spaceName, vm.Sender)
                : _spaceName;
        }

        vm.MentionTapped += OnMessageMentionTapped;
        vm.HashtagTapped += OnSpaceHashtagTapped;
        vm.LinkTapped += OnMessageLinkTapped;
        vm.PollVoteRequested += OnSpacePollVoteRequested;
        vm.ViewCount = message.ViewCount;
        if (payload.Kind == MessageKinds.PollVote && payload.PollVote is not null)
        {
            _spaceVotes[message.Id] = (payload.PollVote, vm.Sender);
        }
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
            PinnedAuthorLabel.Text = string.Format(Loc.T("uc_070"), _pinnedMessage.Sender);
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

    /// <summary>Событие пространства, пришедшее по TCP вместо SignalR.</summary>
    private async void OnTcpSpaceEvent(string type, System.Text.Json.JsonElement? payload)
    {
        if (!payload.HasValue || !_chatService.IsUsingTcp)
        {
            return;
        }

        switch (type)
        {
            case "spaceMessage":
            {
                var message = payload.Value.Deserialize<SpaceMessageDto>(ChatJson.Options);
                if (message is not null) OnSpaceMessageReceived(message);
                break;
            }
            case "spaceMessageUpdated":
            {
                var message = payload.Value.Deserialize<SpaceMessageDto>(ChatJson.Options);
                if (message is not null) OnSpaceMessageUpdated(message);
                break;
            }
            case "spaceMessageDeleted":
            {
                var id = payload.HasValue && payload.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? payload.Value.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(id)) OnSpaceMessageDeleted(_spaceId, id);
                break;
            }
            case "topicCreated":
            {
                var topic = payload.Value.Deserialize<TopicDto>(ChatJson.Options);
                if (topic is not null) _ = LoadAsync();
                break;
            }
        }
    }

    /// <summary>Событие space-сообщения от SignalR/I3.</summary>
    private async void OnSpaceMessageReceived(SpaceMessageDto message)
    {
        if (!string.Equals(message.SpaceId, _spaceId, StringComparison.Ordinal))
        {
            return;
        }

        if (_topicId is not null && !string.Equals(message.TopicId, _topicId, StringComparison.Ordinal))
        {
            // Сообщение в другой теме форума: кэшируем, чтобы счётчик непрочитанного
            // пережил перезапуск, и обновляем панель тем.
            var otherPayload = await _spaces.DecryptPayloadAsync(_spaceId, message);
            if (otherPayload is not null)
                await _localStore.CacheSpaceMessagesAsync(
                    _spaceId, new[] { (message, SpacePayloadCodec.Encode(otherPayload)) });
            if (_forumPaneVisible) await RefreshTopicsAsync();
            return;
        }

        var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
        if (payload is not null)
        {
            _ = _localStore.CacheSpaceMessagesAsync(_spaceId, new[] { (message, SpacePayloadCodec.Encode(payload)) });
        }

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
                    Text = Loc.T("rs_216", "🔒 Сообщение не удалось расшифровать")
                });
            _messages.Add(vm);
            if (_spaceType == SpaceTypes.Forum && _topicId is not null)
                _ = _localStore.MarkSpaceTopicReadAsync(_spaceId, _topicId, message.SentAtUnixMs);
            RefreshDateLabels();
            RefreshMentionJump();

            // Привязываем входящее сообщение к элементу очереди отправителя,
            // чтобы прогресс отправки рисовался прямо в пузыре (как в Telegram).
            var pending = _pendingMedia.FirstOrDefault(x =>
                string.Equals(x.FileName, vm.FileName, StringComparison.OrdinalIgnoreCase));
            if (pending is not null)
            {
                pending.MessageVm = vm;
                vm.TransferProgress = pending.Progress;
            }

            if (AppServices.Get<AppSession>().LinkPreviewEnabled && vm.HasUrls)
            {
                _ = EnsurePreviewAsync(vm);
            }

            // Картинки подгружаем сразу и показываем прогресс в пузыре.
            if (vm.IsImage && vm.Media is not null && !vm.MediaDownloaded)
            {
                _ = AutoDownloadImageAsync(vm);
            }
        });
    }


    private async Task AutoDownloadImageAsync(SpaceMessageVm message)
    {
        try
        {
            if (message.Media is null || message.MediaDownloaded) return;
            var path = Path.Combine(FileSystem.CacheDirectory, message.Id + Path.GetExtension(message.FileName));
            byte[] bytes;
            if (File.Exists(path))
            {
                bytes = await File.ReadAllBytesAsync(path);
            }
            else
            {
                message.TransferProgress = 0;
                var progress = new Progress<int>(p =>
                    MainThread.BeginInvokeOnMainThread(() => message.TransferProgress = p));
                bytes = await _spaces.DownloadMediaAsync(message.Media, progress: progress);
                await File.WriteAllBytesAsync(path, bytes);
            }

            if (message.IsImage)
            {
                var thumb = await Task.Run(() => MediaCompression.MakeThumbnail(
                    bytes, message.Media?.ContentType ?? string.Empty));
                message.SetImageContent(thumb);
            }
            message.TransferProgress = 100;
            message.MediaDownloaded = true;
        }
        catch (Exception ex)
        {
            // auto-download не критичен — картинку можно открыть вручную,
            // но показываем причину, чтобы было понятно, почему она не появилась.
            message.TransferProgress = 100;
            if (_mediaDownloadErrors.Add(message.Id))
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                    await DisplayAlert(
                        Loc.T("common_error", "Error"),
                        string.Format(Loc.T("uc_036"), message.FileName, ex.Message),
                        "OK"));
            }
        }
    }

    private async void OnRetryImageClicked(object sender, EventArgs e)
    {
        if (sender is not Button button ||
            button.CommandParameter is not SpaceMessageVm message ||
            message.Media is null) return;

        button.IsEnabled = false;
        try
        {
            var path = Path.Combine(
                FileSystem.CacheDirectory, message.Id + Path.GetExtension(message.FileName));
            var bytes = await _spaces.DownloadMediaAsync(message.Media);
            await File.WriteAllBytesAsync(path, bytes);
            var thumb = await Task.Run(() => MediaCompression.MakeThumbnail(
                bytes, message.Media?.ContentType ?? string.Empty));
            message.SetImageContent(thumb);
            message.MediaDownloaded = true;
            _mediaDownloadErrors.Remove(message.Id);
        }
        catch (Exception ex)
        {
            await DisplayAlert(
                Loc.T("common_error", "Error"),
                string.Format(Loc.T("uc_036"), message.FileName, ex.Message),
                "OK");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnSendClicked(object sender, EventArgs e)
    {
        if (_sendLongPressTriggered)
        {
            _sendLongPressTriggered = false;
            return;
        }

        if (!_canPost)
        {
            await DisplayAlert(Loc.T("uc_066", "Только чтение"), Loc.T("uc_004", "В этом канале сообщения могут публиковать только администраторы."), "OK");
            return;
        }

        if (_currentTopicClosed)
        {
            await DisplayAlert(Loc.T("dg_069", "Тема закрыта"),
                Loc.T("dg_011", "В этой теме нельзя писать — её закрыл администратор."), "Ок");
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
                ReplyToText = MessagePreview(replyTarget),
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
                await DisplayAlert("SecureChat", string.Format(Loc.T("uc_035"), ex.Message), "OK");
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
                await DisplayAlert("SecureChat", string.Format(Loc.T("uc_035"), ex.Message), "OK");
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
            return Loc.T("rs_142", "Сегодня");
        }

        if (date == today.AddDays(-1))
        {
            return Loc.T("rs_027", "Вчера");
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
            ? Loc.T("rs_161", "Тихий пост включён — следующий пост придёт без уведомлений")
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
            ? Loc.T("rs_050", "Звук пространства выключен — включить")
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

    private void SetupSendLongPress()
    {
        SendButton.Pressed += (_, _) => StartSendLongPress();
        SendButton.Released += (_, _) => CancelSendLongPress();
        ToolTipProperties.SetText(SendButton,
            Loc.T("send_long_press_hint", "Долгое нажатие — отложить отправку"));
    }

    private void StartSendLongPress()
    {
        _sendLongPressTriggered = false;
        _ = SendButton.ScaleTo(0.88, 100, Easing.CubicOut);
        _sendLongPressCts?.Cancel();
        _sendLongPressCts = new CancellationTokenSource();
        var token = _sendLongPressCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            if (token.IsCancellationRequested) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _sendLongPressTriggered = true;
                OnScheduleClicked(this, EventArgs.Empty);
            });
        });
    }

    private void CancelSendLongPress()
    {
        _sendLongPressCts?.Cancel();
        _sendLongPressCts = null;
        _ = SendButton.ScaleTo(1, 120, Easing.CubicOut);
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

        try
        {
            // Отдельная страница с нативными DatePicker и TimePicker —
            // надёжнее текстовых диалогов, особенно на Windows.
            await Navigation.PushAsync(new SchedulePostPage(_spaceId, _topicId, text!));
            MessageEntry.Text = string.Empty;
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
            await DisplayAlert(Loc.T("dg_038", "Отложенные посты"),
                Loc.T("dg_012", "В этом пространстве нет отложенных постов. Напишите текст и нажмите «⏰», чтобы отложить пост."),
                "Ок");
            return;
        }

        var labels = posts.Select(post =>
            $"{DateTimeOffset.FromUnixTimeMilliseconds(post.SendAtUnixMs).ToLocalTime():dd.MM HH:mm} — {post.Preview}").ToArray();

        var choice = await DisplayActionSheet(
            Loc.T("dg_038", "Отложенные посты"),
            Loc.T("common_close", "Закрыть"),
            null,
            labels);

        if (string.IsNullOrWhiteSpace(choice) || choice == Loc.T("common_close", "Закрыть"))
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
            Loc.T("dg_040", "Отменить пост?"),
            string.Format(Loc.T("dg_052"), target.Preview),
            Loc.T("dg_039", "Отменить пост"), Loc.T("dg_036", "Оставить"));

        if (!cancel)
        {
            return;
        }

        await local.RemoveScheduledSpacePostAsync(target.Id);
        await DisplayAlert(Loc.T("set_036", "Готово"), Loc.T("uc_060", "Пост удалён из очереди."), "Ок");
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

    /// <summary>Ключ доставлен — сбрасываем баннер и загружаем историю.</summary>
    private void OnSpaceKeyReady(string spaceId)
    {
        if (!string.Equals(spaceId, _spaceId, StringComparison.Ordinal)) return;
        StopKeyRetryTimer();
        MainThread.BeginInvokeOnMainThread(async () => await LoadAsync());
    }

    private void OnRequestKeyClicked(object sender, EventArgs e) => _ = RequestKeyAsync();

    private async Task RequestKeyAsync()
    {
        await _spaces.RequestSpaceKeyAsync(_spaceId);
    }

    private void StartKeyRetryTimer()
    {
        StopKeyRetryTimer();
        _keyRetryTimer = Dispatcher.CreateTimer();
        _keyRetryTimer.Interval = TimeSpan.FromSeconds(30);
        _keyRetryTimer.Tick += (_, _) => _ = RequestKeyAsync();
        _keyRetryTimer.Start();
    }

    private void StopKeyRetryTimer()
    {
        _keyRetryTimer?.Stop();
        _keyRetryTimer = null;
    }

    private void StartSpaceRefreshTimer()
    {
        StopSpaceRefreshTimer();
        _spaceRefreshTimer = Dispatcher.CreateTimer();
        _spaceRefreshTimer.Interval = TimeSpan.FromSeconds(20);
        _spaceRefreshTimer.Tick += (_, _) => _ = MergeNewMessagesAsync();
        _spaceRefreshTimer.Start();
    }

    private void StopSpaceRefreshTimer()
    {
        _spaceRefreshTimer?.Stop();
        _spaceRefreshTimer = null;
    }

    /// <summary>Подливает новые сообщения, не перезагружая и не очищая ленту.</summary>
    private async Task MergeNewMessagesAsync()
    {
        try
        {
            if (await _spaces.GetSpaceKeyAsync(_spaceId) is null) return;
            var messages = await _spaces.GetMessagesAsync(_spaceId, _topicId);
            if (messages.Count == 0) return;

            var added = false;
            foreach (var message in messages)
            {
                if (_messages.Any(item => item.Id == message.Id)) continue;
                var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
                if (payload?.CommentToMessageId is not null) continue;
                _messages.Add(CreateVm(message, payload ?? new SpaceMessagePayload
                {
                    Kind = "text",
                    Text = Loc.T("rs_216", "🔒 Сообщение не удалось расшифровать")
                }));
                added = true;
            }
            if (added)
            {
                RefreshDateLabels();
                HideScrollToBottom();
            }
        }
        catch
        {
            // фоновая подгрузка не должна ломать экран
        }
    }

    private void OnMessagesScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        var lastIndex = _messages.Count - 1;
        if (lastIndex < 3)
        {
            ScrollToBottomButton.IsVisible = false;
            return;
        }
        var remaining = lastIndex - e.LastVisibleItemIndex;
        ScrollToBottomButton.IsVisible = remaining > 2;
        ScrollToBottomButton.Text = remaining > 0 ? $"↓ {remaining}" : "↓";
    }

    private void OnScrollToBottomClicked(object sender, EventArgs e)
    {
        if (_messages.Count == 0) return;
        MessagesView.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: true);
        ScrollToBottomButton.IsVisible = false;
    }

    private void HideScrollToBottom()
    {
        ScrollToBottomButton.IsVisible = false;
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
        ReplyPreviewLabel.Text = string.Format(Loc.T("uc_071"), Shorten(MessagePreview(message), 70));
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
            Loc.T("uc_063", "Реакция:"),
            Loc.T("common_cancel", "Отмена"),
            null,
            "👍", "❤️", "😂", "😮", "😢", "🔥", "👏", "🙏");
        if (string.IsNullOrWhiteSpace(emoji) || emoji == Loc.T("common_cancel", "Отмена"))
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

        var conversations = (await _chat.GetConversationsAsync())
            .Where(c => !string.IsNullOrWhiteSpace(c.Username))
            .ToList();
        if (conversations.Count == 0)
        {
            await DisplayAlert(Loc.T("dg_047", "Переслать в:"),
                Loc.T("rs_101", "Нет других чатов для пересылки."), "OK");
            return;
        }

        var choice = await DisplayActionSheet(
            Loc.T("dg_047", "Переслать в:"),
            Loc.T("common_cancel", "Отмена"),
            null,
            conversations.Select(c => c.Username).ToArray());
        if (string.IsNullOrWhiteSpace(choice) || choice == Loc.T("common_cancel", "Отмена"))
        {
            return;
        }

        var target = conversations.FirstOrDefault(c =>
            string.Equals(c.Username, choice, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        try
        {
            var origin = !string.IsNullOrWhiteSpace(message.AuthorText)
                ? message.AuthorText
                : message.Sender;
            var text = !string.IsNullOrWhiteSpace(message.Text)
                ? message.Text
                : MessagePreview(message);

            await _chat.SendMessageToUserAsync(
                target.UserId,
                target.Username,
                text,
                target.EncryptionPublicKey,
                isForwarded: true,
                forwardedFrom: origin);

            foreach (var m in _messages) m.IsSelected = false;
            await DisplayAlert(Loc.T("dg_047", "Переслать в:"),
                string.Format(Loc.T("rs_125"), target.Username), "OK");
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    /// <summary>Короткий текст сообщения для ответа и пересылки.</summary>
    private static string MessagePreview(SpaceMessageVm? message)
    {
        if (message is null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(message.Text)) return message.Text;
        if (!string.IsNullOrWhiteSpace(message.FileName)) return message.FileName;
        if (message.IsImage) return "🖼";
        if (message.IsVoice) return "🎙";
        return string.Empty;
    }


    private async void OnActionEditClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        ActionLabel.Text = string.Format(Loc.T("uc_064"), message.Text);
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
            Loc.T("dg_078", "Удалить сообщение?"),
            Loc.T("dg_063", "Сообщение будет удалено для всех участников."),
            Loc.T("common_delete", "Удалить"),
            Loc.T("common_cancel", "Отмена"));
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
                if (message.IsImage && !message.IsImageReady)
                {
                    var thumb = await Task.Run(() => MediaCompression.MakeThumbnail(
                        bytes, message.Media?.ContentType ?? string.Empty));
                    message.SetImageContent(thumb);
                }
            }
            else
            {
                message.TransferProgress = 0;
                var dlProgress = new Progress<int>(p =>
                    MainThread.BeginInvokeOnMainThread(() => message.TransferProgress = p));
                bytes = await _spaces.DownloadMediaAsync(message.Media, progress: dlProgress);
                await File.WriteAllBytesAsync(path, bytes);

                if (message.IsImage)
                {
                    var thumb = await Task.Run(() => MediaCompression.MakeThumbnail(
                        bytes, message.Media?.ContentType ?? string.Empty));
                    message.SetImageContent(thumb);
                }
                message.TransferProgress = 100;
            }

            message.MediaDownloaded = true;

            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
            {
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_008", "Вложение недоступно на этом устройстве."), "OK");
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
                await page.Navigation.PushAsync(
                    new ImageViewerPage(bytes, message.FileName));
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
                ?? throw new InvalidOperationException(Loc.T("rs_023", "Воспроизведение не поддерживается на этой платформе."));

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
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_023"), ex.Message), "OK");
        }
#else
        await Task.CompletedTask;
        await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_009", "Воспроизведение голосовых на этой платформе недоступно."), "OK");
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
                    await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_003", "Аудиофайл голосового сообщения не найден."), "OK");
                    return;
                }

                var bytes = await _spaces.DownloadMediaAsync(message.Media);
                await File.WriteAllBytesAsync(path, bytes);
            }

            await PlayVoiceInline(message, await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_023"), ex.Message), "OK");
        }
#else
        await Task.CompletedTask;
        await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_009", "Воспроизведение голосовых на этой платформе недоступно."), "OK");
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

    /// <summary>Одна кнопка для эмодзи и своих пакетов: показывает общие закладки.</summary>
    private async void OnCustomPackButtonClicked(object sender, EventArgs e)
    {
        AttachMenuPanel.IsVisible = false;
        var show = !PickerTabs.IsVisible;
        PickerTabs.IsVisible = show;
        EmojiPicker.IsVisible = show;
        StickerPicker.IsVisible = false;
        GifPicker.IsVisible = false;
        CustomPackPicker.IsVisible = false;
        PickerTabs.IsVisible = show;
        SetPickerTab("emoji");
        if (show)
            MessageEntry.FocusInput();
    }

    /// <summary>Переключение закладок «эмодзи» / «мои пакеты».</summary>
    private async void OnPickerTabClicked(object sender, EventArgs e)
    {
        var tab = (sender as Element)?.StyleId ?? "emoji";
        SetPickerTab(tab);
        if (tab == "pack")
        {
            EmojiPicker.IsVisible = false;
            CustomPackPicker.IsVisible = true;
            PickerTabs.IsVisible = true;
            await CustomPackPicker.ReloadAsync();
        }
        else
        {
            CustomPackPicker.IsVisible = false;
        PickerTabs.IsVisible = false;
            EmojiPicker.IsVisible = true;
            PickerTabs.IsVisible = true;
        }
        MessageEntry.FocusInput();
    }

    private void SetPickerTab(string tab)
    {
        EmojiTabButton.BackgroundColor = tab == "emoji"
            ? (Color)Application.Current!.Resources["Accent"]
            : (Color)Application.Current!.Resources["SurfaceAlt"];
        EmojiTabButton.TextColor = tab == "emoji"
            ? (Color)Application.Current!.Resources["AccentText"]
            : (Color)Application.Current!.Resources["TextSecondary"];
        PackTabButton.BackgroundColor = tab == "pack"
            ? (Color)Application.Current!.Resources["Accent"]
            : (Color)Application.Current!.Resources["SurfaceAlt"];
        PackTabButton.TextColor = tab == "pack"
            ? (Color)Application.Current!.Resources["AccentText"]
            : (Color)Application.Current!.Resources["TextSecondary"];
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
                        ? Loc.T("rs_031", "Выберите свои GIF")
                        : Loc.T("rs_032", "Выберите свои стикеры (PNG, WebP, JPG)"),
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
                    await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_032"), file.FileName, ex.Message), "OK");
                }
            }

            if (imported > 0)
            {
                await CustomPackPicker.ReloadAsync();
                await DisplayAlert(Loc.T("set_036", "Готово"), string.Format(Loc.T("uc_013"), imported), "OK");
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
        PickerTabs.IsVisible = false;
        PickerTabs.IsVisible = false;

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
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_038"), ex.Message), "OK");
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
                ReplyToText = MessagePreview(_replyingTo),
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
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_037"), ex.Message), "OK");
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
            await AttachMenuPanel.ScaleTo(0.85, 100, Easing.CubicIn);
            AttachMenuPanel.IsVisible = false;
            AttachMenuPanel.Scale = 1;
            return;
        }

        if (_sendingAttachment)
        {
            return;
        }

        AttachMenuPanel.Scale = 0.85;
        AttachMenuPanel.IsVisible = true;
        await AttachMenuPanel.ScaleTo(1, 160, Easing.CubicOut);
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
                case "poll":
                    OnCreatePollClicked(this, EventArgs.Empty);
                    break;

                case "photo":
                SendActivity("uploading_photo");
                    var mediaFiles = await FilePicker.Default.PickMultipleAsync(
                        new PickOptions { PickerTitle = Loc.T("rs_036", "Выберите фото или видео") });
                    if (mediaFiles is null || !mediaFiles.Any()) return;
                    await SendAttachmentsAsync(mediaFiles, isPhoto: true);
                    break;

                case "file":
                    var files = await FilePicker.Default.PickMultipleAsync(
                        new PickOptions { PickerTitle = Loc.T("rs_035", "Выберите файлы") });
                    if (files is null || !files.Any()) return;
                    await SendAttachmentsAsync(files, isPhoto: false);
                    break;

                case "music":
                    var audioFiles = await FilePicker.Default.PickMultipleAsync(
                        new PickOptions { PickerTitle = Loc.T("rs_029", "Выберите музыку") });
                    if (audioFiles is null || !audioFiles.Any()) return;
                    await SendAttachmentsAsync(audioFiles, isPhoto: false);
                    break;

                case "voice":
                SendActivity("recording_voice");
                    var voice = await RecordVoiceMessageAsync(this);
                    if (voice is null) return;
                    EnqueueMediaItem("voice.m4a", voice.Value.ContentType, voice.Value.Data, MessageKinds.Voice);
                    break;

                case "circle":
                SendActivity("recording_video");
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
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        item.Progress = p;
                        if (item.MessageVm is not null) item.MessageVm.TransferProgress = p;
                    }));

                try
                {
                    var sent = await _spaces.SendMediaAsync(
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
                    item.Status = Loc.T("set_036", "Готово");
                    if (sent is not null)
                    {
                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            var sentVm = _messages.FirstOrDefault(m => m.Id == sent.Id);
                            if (sentVm is not null)
                            {
                                sentVm.TransferProgress = 100;
                                return;
                            }

                            // Если событие SignalR/TCP не дошло, добавляем отправленное
                            // вложение в текущую ленту сами.
                            if (string.Equals(sent.TopicId, _topicId, StringComparison.Ordinal))
                            {
                                var sentPayload = await _spaces.DecryptPayloadAsync(_spaceId, sent);
                                if (sentPayload is not null)
                                {
                                    _messages.Add(CreateVm(sent, sentPayload));
                                    RefreshDateLabels();
                                    RefreshMentionJump();
                                }
                            }
                        });
                    }
                    await Task.Delay(350);
                    _pendingMedia.Remove(item);
                }
                catch (OperationCanceledException)
                {
                    _pendingMedia.Remove(item);
                }
                catch (Exception ex)
                {
                    item.Status = Loc.T("common_error", "Ошибка");
                    MainThread.BeginInvokeOnMainThread(async () =>
                        await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_036"), item.FileName, ex.Message), "OK"));
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
                await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_001"), file.FileName), "OK");
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
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_053", "Нет права на запись аудио. Разрешите доступ к микрофону в настройках."), "OK");
                return null;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_031"), ex.Message), "OK");
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
            Text = Loc.T("rs_053", "Идёт запись"),
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
            Text = Loc.T("rs_114", "Отправить"),
            BackgroundColor = (Color)resources["Accent"],
            TextColor = Colors.White,
            CornerRadius = 18,
            HeightRequest = 38,
            FontSize = 13,
            Padding = new Thickness(16, 0)
        };

        var cancelButton = new Button
        {
            Text = Loc.T("common_cancel", "Отмена"),
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
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_057"), ex.Message), "OK");
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
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_040"), ex.Message), "OK");
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
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_054", "Нет права на камеру. Разрешите доступ к камере в настройках."), "OK");
                return null;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_030"), ex.Message), "OK");
            return null;
        }

        try
        {
            // Системная камера показывает предпросмотр и даёт остановить/подтвердить запись.
            var captured = await MediaPicker.Default.CaptureVideoAsync(new MediaPickerOptions
            {
                Title = Loc.T("rs_046", "Запись видеокружка")
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
                await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_016", "Камера вернула пустой видеофайл."), "OK");
                return null;
            }

            if (buffer.Length > MediaLimits.MaxBytes)
            {
                await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_006"), MediaLimits.MaxBytes / (1024 * 1024)), "OK");
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
            await DisplayAlert(Loc.T("common_error", "Ошибка"), Loc.T("uc_017", "На устройстве нет приложения камеры, поддерживающего запись видео."), "OK");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            await DisplayAlert(Loc.T("common_error", "Ошибка"), string.Format(Loc.T("uc_029"), ex.Message), "OK");
            return null;
        }
#else
        await Task.CompletedTask;
        await DisplayAlert("Видеокружок", Loc.T("uc_011", "Запись видеокружков на этой платформе недоступна."), "OK");
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

    private async void OnCommunityInfoTapped(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new SpaceProfilePage(_spaceId));
    }

    private async void OnCommunityLeaveClicked(object sender, EventArgs e)
    {
        var confirmed = await DisplayAlert(
            Loc.T("community_leave", "Leave community"),
            Loc.T("community_leave", "Leave community?"),
            Loc.T("common_delete", "Leave"), Loc.T("common_cancel", "Cancel"));
        if (!confirmed) return;
        if (await _spaces.LeaveAsync(_spaceId))
        {
            if (Navigation.NavigationStack.Count > 1)
                await Navigation.PopAsync(false);
        }
    }

    private async void OnSwitchSpaceClicked(object sender, EventArgs e)
    {
        var spaces = (await _spaces.ListAsync())
            .Where(s => s.Type is SpaceTypes.Forum or SpaceTypes.Community)
            .ToList();
        if (spaces.Count <= 1)
        {
            await DisplayAlert(
                Loc.T("forum_switch_title", "Switch forum"),
                Loc.T("forum_switch_empty", "No other spaces yet"),
                "OK");
            return;
        }

        static string Icon(string type) => type switch
        {
            SpaceTypes.Forum => "🧵",
            SpaceTypes.Channel => "📢",
            SpaceTypes.Community => "🌐",
            _ => "👥"
        };

        var labels = spaces.Select(s => $"{Icon(s.Type)} {s.Name}").ToArray();
        var selected = await DisplayActionSheet(
            Loc.T("forum_switch_title", "Switch forum"),
            Loc.T("common_cancel", "Cancel"), null, labels);
        if (string.IsNullOrWhiteSpace(selected)) return;
        var index = Array.IndexOf(labels, selected);
        if (index < 0) return;
        var target = spaces[index];
        if (string.Equals(target.Id, _spaceId, StringComparison.Ordinal)) return;

        if (Navigation.NavigationStack.Count > 1)
            await Navigation.PopAsync(false);
        await Navigation.PushAsync(new SpaceChatPage(
            target.Id, target.Name, target.Type, target.CanPost));
    }

    private async void OnTopicsClicked(object sender, EventArgs e)
    {
        _forumPaneVisible = !_forumPaneVisible;
        ForumTopicsPane.IsVisible = _forumPaneVisible;
        ApplyForumPaneLayout();
        if (_forumPaneVisible) await RefreshTopicsAsync();
    }

    private void ApplyForumPaneLayout()
    {
        var isDesktop = Microsoft.Maui.Devices.DeviceInfo.Platform ==
                        Microsoft.Maui.Devices.DevicePlatform.WinUI;
        if (isDesktop)
        {
            ForumTopicsPane.WidthRequest = 288;
            ForumTopicsPane.HorizontalOptions = LayoutOptions.Start;
        }
        else
        {
            ForumTopicsPane.WidthRequest = -1;
            ForumTopicsPane.HorizontalOptions = LayoutOptions.Fill;
        }

        var left = _forumPaneVisible && isDesktop ? 296d : 0d;
        MessagesView.Margin = new Thickness(left, 4, 0, 0);
        EmptyStatePanel.Margin = new Thickness(left, 0, 0, 0);
        TopicInfoCard.Margin = new Thickness(12 + left, 6, 12, 0);
        CommunityInfoCard.Margin = new Thickness(12 + left, 6, 12, 0);
        LoadingPlaceholder.Margin = new Thickness(left, 0, 0, 0);
    }

    private async Task RefreshTopicsAsync()
    {
        var topics = await _spaces.GetTopicsAsync(_spaceId);
        var previews = await _localStore.GetSpaceTopicLastMessagesAsync(_spaceId);
        _allTopics.Clear();
        foreach (var topic in topics)
        {
            var unread = await _localStore.GetSpaceTopicUnreadCountAsync(
                _spaceId, topic.Id, _myUserId ?? string.Empty);
            previews.TryGetValue(topic.Id, out var last);
            var preview = string.IsNullOrWhiteSpace(last.Text)
                ? string.Empty
                : last.Text.Replace('\n', ' ').Trim();
            var subtitle = $"💬 {topic.MessageCount}";
            if (topic.IsClosed) subtitle += " · 🔒";
            if (preview.Length > 0) subtitle += " · " + preview;
            _allTopics.Add(new TopicVm(
                topic.Id, topic.Title, topic.MessageCount, topic.IsClosed,
                topic.CreatedByUsername, subtitle, last.SentAtUnixMs)
            {
                IsActive = string.Equals(topic.Id, _topicId, StringComparison.Ordinal),
                Unread = unread
            });
        }
        FilterTopics();
    }

    private void FilterTopics()
    {
        var query = TopicSearchBar.Text?.Trim() ?? string.Empty;
        IEnumerable<TopicVm> items = _allTopics;
        if (query.Length > 0)
            items = items.Where(t =>
                t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                t.SubtitleText.Contains(query, StringComparison.OrdinalIgnoreCase));

        items = _topicSortMode switch
        {
            1 => items.OrderByDescending(t => t.LastMessageAtUnixMs),
            2 => items.OrderByDescending(t => t.Unread).ThenByDescending(t => t.LastMessageAtUnixMs),
            3 => items.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => items
        };

        _topics.Clear();
        foreach (var item in items.ToList()) _topics.Add(item);
        _suppressTopicSelection = true;
        try
        {
            TopicsView.SelectedItem = _topics.FirstOrDefault(t => t.Id == _topicId);
        }
        finally
        {
            _suppressTopicSelection = false;
        }
    }

    private void OnTopicSearchChanged(object sender, TextChangedEventArgs e) => FilterTopics();

    private void OnTopicSortChanged(object sender, EventArgs e)
    {
        _topicSortMode = TopicSortPicker.SelectedIndex;
        FilterTopics();
    }

    private async void OnTopicSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTopicSelection) return;
        if (e.CurrentSelection.FirstOrDefault() is not TopicVm topic) return;
        _topicId = topic.Id;
        _topicTitle = topic.Title;
        _currentTopicClosed = topic.IsClosed;
        if (Microsoft.Maui.Devices.DeviceInfo.Platform !=
            Microsoft.Maui.Devices.DevicePlatform.WinUI)
        {
            _forumPaneVisible = false;
            ForumTopicsPane.IsVisible = false;
            ApplyForumPaneLayout();
        }
        await LoadAsync();
    }

    private async void OnAddTopicClicked(object sender, EventArgs e)
    {
        var title = await DisplayPromptAsync(
            Loc.T("forum_new_topic", "New topic"),
            Loc.T("rs_018", "Enter the topic name:"),
            Loc.T("rs_149", "Create"),
            Loc.T("common_cancel", "Cancel"),
            maxLength: 120,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(title)) return;
        var created = await _spaces.CreateTopicAsync(_spaceId, title, null);
        if (created is null) return;
        _topicId = created.Id;
        _topicTitle = created.Title;
        _currentTopicClosed = false;
        if (_forumPaneVisible) await RefreshTopicsAsync();
        await LoadAsync();
    }

    private async void OnManageTopicsClicked(object sender, EventArgs e)
    {
        if (!_canModerate) return;
        var topics = (await _spaces.GetTopicsAsync(_spaceId)).ToList();
        var rename = Loc.T("rs_189", "✏️ Rename topic…");
        var toggle = Loc.T("rs_215", "🔒 Close or reopen topic…");
        var up = Loc.T("rs_193", "⬆️ Move topic up");
        var down = Loc.T("rs_194", "⬇️ Move topic down");
        var delete = Loc.T("rs_220", "🗑 Delete topic…");
        var selected = await DisplayActionSheet(
            Loc.T("forum_topics", "Topics"),
            Loc.T("common_cancel", "Cancel"), null,
            rename, toggle, up, down, delete);
        if (string.IsNullOrWhiteSpace(selected)) return;

        if (selected == rename) { await RenameTopicAsync(topics); return; }
        if (selected == toggle) { await ToggleTopicClosedAsync(topics); return; }
        if (selected == up || selected == down)
        {
            await MoveTopicAsync(topics, selected == up ? -1 : 1);
            return;
        }
        if (selected == delete)
        {
            var target = await PickTopicAsync(topics, Loc.T("rs_064", "Which topic to delete? All its messages will be erased."));
            if (target is null) return;
            var confirmed = await DisplayAlert(
                Loc.T("dg_079", "Delete topic"),
                string.Format(Loc.T("dg_066"), target.Title),
                Loc.T("common_delete", "Delete"), Loc.T("common_cancel", "Cancel"));
            if (!confirmed) return;
            if (await _spaces.DeleteTopicAsync(_spaceId, target.Id) &&
                string.Equals(_topicId, target.Id, StringComparison.Ordinal))
            {
                _topicId = null;
                _topicTitle = null;
            }
            if (_forumPaneVisible) await RefreshTopicsAsync();
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
        var selected = await DisplayActionSheet(question, Loc.T("common_cancel", "Отмена"), null, labels);
        if (string.IsNullOrWhiteSpace(selected) || selected == Loc.T("common_cancel", "Отмена"))
        {
            return null;
        }

        var index = Array.IndexOf(labels, selected);
        return index >= 0 && index < topics.Count ? topics[index] : null;
    }

    /// <summary>Переименование темы форума.</summary>
    private async Task RenameTopicAsync(IReadOnlyList<TopicDto> topics)
    {
        var target = await PickTopicAsync(topics, Loc.T("rs_062", "Какую тему переименовать?"));
        if (target is null)
        {
            return;
        }

        var title = await DisplayPromptAsync(
            Loc.T("rs_123", "Переименовать тему"),
            Loc.T("rs_108", "Новое название темы:"),
            Loc.T("common_save", "Сохранить"),
            Loc.T("common_cancel", "Отмена"),
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
            await DisplayAlert(Loc.T("uc_022", "Не удалось"), Loc.T("uc_047", "Не хватает прав или тема не найдена."), "Ок");
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
        var target = await PickTopicAsync(topics, Loc.T("rs_061", "Какую тему закрыть или открыть?"));
        if (target is null)
        {
            return;
        }

        var updated = await _spaces.SetTopicClosedAsync(_spaceId, target.Id, !target.IsClosed);
        if (updated is null)
        {
            await DisplayAlert(Loc.T("uc_022", "Не удалось"), Loc.T("uc_047", "Не хватает прав или тема не найдена."), "Ок");
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
        var target = await PickTopicAsync(topics, Loc.T("rs_063", "Какую тему переместить?"));
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
            await DisplayAlert(Loc.T("uc_022", "Не удалось"), Loc.T("uc_047", "Не хватает прав или тема не найдена."), "Ок");
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

    private void OnSpaceHashtagTapped(object sender, string tag)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            _ = Navigation.PushAsync(new GlobalSearchPage("#" + tag));
        }
    }

    private async void OnCreatePollClicked(object? sender, EventArgs e)
    {
        if (!_canPost) return;
        var createPage = new PollCreatePage();
        createPage.PollCreated += async (_, poll) =>
        {
            var payload = new SpaceMessagePayload
            {
                Kind = MessageKinds.Poll,
                Text = string.Empty,
                Poll = poll
            };
            var sent = await _spaces.SendPayloadAsync(_spaceId, _topicId, payload);
            if (sent is not null && !_messages.Any(m => m.Id == sent.Id))
            {
                _messages.Add(CreateVm(sent, payload));
            }
        };
        await Navigation.PushAsync(createPage);
    }

    private void ApplySpacePollVotes()
    {
        var polls = _messages.Where(m => m.Poll is not null)
            .ToDictionary(m => m.Poll!.Id, m => m.Poll!);
        foreach (var (vote, voter) in _spaceVotes.Values)
        {
            if (polls.TryGetValue(vote.PollId, out var poll))
            {
                poll.ApplyVote(vote.OptionIndex, voter, mine: false);
            }
        }
    }

    private async void OnSpacePollVoteRequested(SpaceMessageVm message, int optionIndex)
    {
        var poll = message.Poll;
        if (poll is null) return;
        var voteData = new PollVoteData { PollId = poll.Id, OptionIndex = optionIndex };
        var ownName = AppServices.Get<AppSession>().User?.Username ?? "me";
        poll.ApplyVote(optionIndex, ownName, mine: true);
        var payload = new SpaceMessagePayload
        {
            Kind = MessageKinds.PollVote,
            Text = string.Empty,
            PollVote = voteData
        };
        try
        {
            var sent = await _spaces.SendPayloadAsync(_spaceId, _topicId, payload);
            if (sent is not null)
            {
                _spaceVotes[sent.Id] = (voteData, ownName);
            }
        }
        catch (Exception ex)
        {
            await this.ShowErrorAsync(ex);
        }
    }

    private async Task RecordChannelViewsAsync()
    {
        if (_spaceType != SpaceTypes.Channel) return;
        var ids = _messages.Where(m => m.ViewCount >= 0).Select(m => m.Id).ToList();
        await _spaces.RecordViewsAsync(_spaceId, ids);
    }

    private async void OnStatsTapped(object sender, EventArgs e)
    {
        if (sender is not Element { BindingContext: SpaceMessageVm post }) return;
        var page = this as Page;
        if (page is null) return;
        var viewers = await _spaces.GetViewersAsync(_spaceId, post.Id);
        var title = viewers.Count == 0
            ? "Просмотров пока нет"
            : $"Просмотрели ({viewers.Count})";
        await page.DisplayActionSheet(title, "Закрыть", null,
            viewers.Count == 0 ? new[] { "Пока никто" } : viewers.ToArray());
    }




}

