using System.Text.Json;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using SecureChat.App.Pages;
using SecureChat.App.Services;
using SecureChat.Core.Contracts;

#if ANDROID
using Android.Media;
using Android.Content;
#endif

namespace SecureChat.App.Views;

/// <summary>
/// Переписка с одним контактом. Используется и на телефоне (внутри ChatPage),
/// и в двухпанельном Windows-клиенте.
/// </summary>
public partial class ChatView : ContentView
{
    private const string ProfileTab = "profile";
    private const string MediaTab = "media";
    private const string PinnedTab = "pinned";

    private static readonly string[] ReactionEmojis = { "👍", "❤️", "😂", "😮", "😢", "🔥", "👏", "🙏" };

    private static readonly CultureInfo RuCulture = new("ru-RU");

    private readonly ChatManager _chatManager = AppServices.Get<ChatManager>();
    private readonly AppSession _session = AppServices.Get<AppSession>();
    private readonly LinkPreviewService _previewService = AppServices.Get<LinkPreviewService>();
    private readonly IAudioRecorder _audioRecorder = AppServices.Get<IAudioRecorder>();
    private readonly ObservableCollection<MessageVm> _messages = new();
    private readonly List<MessageVm> _allPollVotes = new();
    private readonly ObservableCollection<MediaListItemVm> _mediaItems = new();
    private readonly ObservableCollection<MessageVm> _pinnedItems = new();

    private StoredConversation? _conversation;
    private string? _username;
    private bool _subscribed;
    private bool _sending;
    private long? _ttlNext;
    private bool _sendingAttachment;

    /// <summary>Следующее сообщение (или вложение) будет одноразовым.</summary>
    private bool _viewOnceNext;
    private bool _suppressDraft;
    private MessageVm? _selected;
    private int _pinnedIndex;
    private string _messageQuery = string.Empty;
    private DateTime _lastTypingSentUtc = DateTime.MinValue;
    private CancellationTokenSource? _hintCts;
    private CancellationTokenSource? _draftCts;
    private MessageVm? _replyingTo;
    private string? _editingMessageId;
    private Point _menuPosition;
    private CancellationTokenSource? _longPressCts;
    private System.Threading.Timer? _attachHideTimer;

    /// <summary>Очередь отправки медиа (фото, видео, голосовые, кружки, файлы).</summary>
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

        /// <summary>Идентификатор альбома (несколько фото одним блоком).</summary>
        public string? AlbumId { get; set; }

        /// <summary>Номер фото в альбоме (с нуля).</summary>
        public int AlbumIndex { get; set; }

        /// <summary>Всего фото в альбоме.</summary>
        public int AlbumCount { get; set; }

        /// <summary>Ответ на сообщение, с которым ушло вложение (если отвечали).</summary>
        public string? ReplyToMessageId { get; set; }
        public string? ReplyToText { get; set; }
        public string? ReplyToFileName { get; set; }
        public string? ReplyToSender { get; set; }

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

        public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

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
    private string? _scrollToMessageId;

#if ANDROID || WINDOWS
    private IVoicePlayer? _voicePlayer;
    private string? _voicePlayingMessageId;
    private string? _voiceProgressMessageId;
    private System.Timers.Timer? _voiceProgressTimer;
#endif

    public ChatView()
    {
        InitializeComponent();
        MessagesView.ItemsSource = _messages;
        MediaItemsView.ItemsSource = _mediaItems;
        PinnedItemsView.ItemsSource = _pinnedItems;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object? sender, EventArgs e)
    {
        _session.AppearanceChanged += OnAppearanceChanged;
        ApplyWallpaper();
#if WINDOWS
        EnsureWindowHooks();
#endif
    }

    private void OnViewUnloaded(object? sender, EventArgs e)
    {
        _session.AppearanceChanged -= OnAppearanceChanged;
#if WINDOWS
        // Чат закрыт — файлы, брошенные в окно, больше не должны уходить сюда.
        // Если чат ещё открыт, приём остаётся включённым.
        if (ReferenceEquals(_activeDropTarget, this) && string.IsNullOrWhiteSpace(_username))
        {
            _activeDropTarget = null;
        }
#endif
    }

    /// <summary>Обои переписки: картинка из настроек «Обои чата» + затемнение.</summary>
    private void ApplyWallpaper()
    {
        var path = _session.WallpaperPath;
        var hasWallpaper = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        WallpaperImage.IsVisible = hasWallpaper;
        WallpaperShade.IsVisible = hasWallpaper && _session.WallpaperDim;
        WallpaperImage.Source = hasWallpaper ? ImageSource.FromFile(path) : null;
    }

    private void OnAppearanceChanged() =>
        MainThread.BeginInvokeOnMainThread(ApplyWallpaper);

    /// <summary>Кнопка 😊 — показать или скрыть панель Эмодзи/Стикеры/GIF.</summary>
    private void OnEmojiButtonClicked(object sender, EventArgs e)
    {
        AttachMenuPanel.IsVisible = false;

        if (MediaPanel.IsVisible)
        {
            MediaPanel.IsVisible = false;
            return;
        }

        // По умолчанию открываем вкладку эмодзи, как в Telegram.
        MediaPanel.IsVisible = true;
        ActivatePanelTab("Emoji");
    }

    /// <summary>Переключение вкладок панели: Эмодзи | Стикеры | GIF.</summary>
    private void OnPanelTabClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string tab })
        {
            return;
        }

        ActivatePanelTab(tab);
    }

    private void ActivatePanelTab(string tab)
    {
        EmojiTabContent.IsVisible = tab == "Emoji";
        StickerTabContent.IsVisible = tab == "Stickers";
        GifTabContent.IsVisible = tab == "Gifs";
        PacksTabContent.IsVisible = tab == "Packs";

        StyleTabButton(EmojiTabButton, tab == "Emoji");
        StyleTabButton(StickerTabButton, tab == "Stickers");
        StyleTabButton(GifTabButton, tab == "Gifs");
        StyleTabButton(PacksTabButton, tab == "Packs");

        if (tab == "Gifs")
        {
            // GIF собираются из уже загруженной истории — быстрее успеть показать.
            _ = RefreshGifPanelAsync();
        }
        else if (tab == "Packs")
        {
            _ = PacksTabContent.ReloadAsync();
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
                    ShowHint(string.Format(Loc.T("uc_032"), file.FileName, ex.Message), isError: true);
                }
            }

            if (imported > 0)
            {
                await PacksTabContent.ReloadAsync();
                ShowHint(string.Format(Loc.T("uc_013"), imported), isError: false);
            }
        }
        catch
        {
            // Отмена выбора — не ошибка.
        }
    }

    /// <summary>Файл из своего пака: отправляем как шифрованное вложение.</summary>
    private async void OnCustomPackItemPicked(CustomPackPickerView.PackItem item)
    {
        var user = _username;
        if (user is null || _sendingAttachment || item.Bytes is not { Length: > 0 })
        {
            return;
        }

        _sendingAttachment = true;
        try
        {
            MediaPanel.IsVisible = false;

            var reply = _replyingTo;
            _replyingTo = null;
            ReplyBar.IsVisible = false;

            await _chatManager.SendMediaAsync(
                user,
                item.FileName,
                item.ContentType,
                item.Bytes,
                replyToMessageId: reply?.Id,
                replyToText: reply?.Text,
                replyToFileName: reply?.FileName,
                replyToSender: reply?.ReplyAuthorName,
                isOnce: _viewOnceNext);
            ResetViewOnce();
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("uc_038"), ex.Message), isError: true);
        }
        finally
        {
            _sendingAttachment = false;
        }
    }

    /// <summary>Активная вкладка панели выделяется как в Telegram: акцентный цвет и шрифт.</summary>
    private void StyleTabButton(Button button, bool active)
    {
        button.TextColor = active
            ? (Color)Application.Current!.Resources["Accent"]
            : (Color)Application.Current!.Resources["TextMuted"];
        button.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
    }

    /// <summary>Стикер — крупный глиф; отправляем Kind=Sticker, поддержка ответа как у текста.</summary>
    private async void OnStickerPicked(string glyph)
    {
        var user = _username;
        if (user is null || _sending || string.IsNullOrWhiteSpace(glyph))
        {
            return;
        }

        var replyTarget = _replyingTo;
        _sending = true;
        try
        {
            MediaPanel.IsVisible = false;
            _replyingTo = null;
            ReplyBar.IsVisible = false;

            await _chatManager.SendMessageAsync(
                user,
                glyph,
                replyToMessageId: replyTarget?.Id,
                replyToText: replyTarget?.Text,
                replyToFileName: replyTarget?.FileName,
                kind: MessageKinds.Sticker,
                replyToSender: replyTarget?.ReplyAuthorName);

            ScrollToBottom();
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("uc_037"), ex.Message), isError: true);
        }
        finally
        {
            _sending = false;
        }
    }

    /// <summary>Тап по GIF на вкладке — отправляем тот же файл в текущий чат.</summary>
    private async void OnGifPicked(GifPickerView.GifItem item)
    {
        var user = _username;
        if (user is null || _sendingAttachment || item.Bytes.Length == 0)
        {
            return;
        }

        _sendingAttachment = true;
        try
        {
            MediaPanel.IsVisible = false;

            // Ответ на сообщение уходит вместе с GIF, панель ответа закрывается.
            var reply = _replyingTo;
            _replyingTo = null;
            ReplyBar.IsVisible = false;

            await _chatManager.SendMediaAsync(
                user,
                item.FileName,
                item.ContentType,
                item.Bytes,
                replyToMessageId: reply?.Id,
                replyToText: reply?.Text,
                replyToFileName: reply?.FileName,
                replyToSender: reply?.ReplyAuthorName);
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_087"), ex.Message), isError: true);
        }
        finally
        {
            _sendingAttachment = false;
        }
    }

    /// <summary>Недавние GIF из переписки: уникальные вложения, свежими вперёд, только локально загруженные.</summary>
    private async Task RefreshGifPanelAsync()
    {
        var conversation = _conversation;
        if (conversation is null)
        {
            return;
        }

        try
        {
            var messages = conversation.Messages
                .Where(m => m.Kind == MessageKinds.Image &&
                            m.ContentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase))
                .Select(m => string.IsNullOrEmpty(m.MediaId) ? (Key: m.Id, Message: m) : (Key: m.MediaId, Message: m))
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(x => x.Message.SentAtUnixMs).First().Message)
                .OrderByDescending(m => m.SentAtUnixMs)
                .Take(24)
                .ToList();

            var items = new List<GifPickerView.GifItem>();
            foreach (var message in messages)
            {
                var bytes = await _chatManager.GetLocalMediaAsync(message.Id);
                if (bytes is { Length: > 0 })
                {
                    items.Add(new GifPickerView.GifItem
                    {
                        MessageId = message.Id,
                        FileName = message.FileName,
                        ContentType = message.ContentType,
                        Bytes = bytes
                    });
                }
            }

            MainThread.BeginInvokeOnMainThread(() => GifTabContent.SetItems(items));
        }
        catch
        {
            // Панель GIF не критична: ошибка загрузки превью не должна ронять чат.
        }
    }

    /// <summary>Эмодзи из панели — вставить в позицию курсора поля ввода.</summary>
    private void OnEmojiPicked(string emoji)
    {
        var text = MessageEntry.Text ?? string.Empty;
        var position = Math.Clamp(MessageEntry.CursorPosition, 0, text.Length);
        MessageEntry.Text = text.Insert(position, emoji);
        MessageEntry.CursorPosition = Math.Min(position + emoji.Length, MessageEntry.Text.Length);
        MessageEntry.FocusInput();
    }

    /// <summary>Нажата ⚙ — открыть профиль и настройки.</summary>
    public event Action? SettingsRequested;

    /// <summary>Нажата ✕ — закрыть чат.</summary>
    public event Action? CloseRequested;

    public string? Username => _username;

    private Page? HostPage => Application.Current?.Windows.FirstOrDefault()?.Page;

    public async Task OpenAsync(string username, string? scrollToMessageId = null)
    {
        ResetChat();

#if WINDOWS
        // Перетаскивание работает только с открытым чатом: подключаем
        // обработчики окна и помечаем этот чат активным получателем файлов.
        EnsureWindowHooks();

        // Приложение от имени администратора Windows не получает перетаскивание из Проводника.
        // Сообщаем об этом один раз за сеанс, а не молча.
        if (!_elevatedWarningShown && IsRunningElevated())
        {
            _elevatedWarningShown = true;
            ShowHint(
                Loc.T("rs_047", "Запущено от администратора: Windows не передаёт перетаскивание файлов из Проводника. Запустите приложение без прав администратора."),
                isError: true);
        }
#endif

        var stored = await _chatManager.GetConversationAsync(username)
            ?? throw new InvalidOperationException(Loc.T("rs_068", "Контакт не найден."));

        _username = stored.Username;
        Subscribe();
        await _chatManager.OpenChatAsync(_username);
        _conversation = await _chatManager.GetConversationAsync(_username) ?? stored;

        ContactLabel.Text = _conversation.Username;
        ContactAvatar.Source = AvatarSource(_conversation.AvatarBase64);
        KeyWarningLabel.IsVisible = _conversation.KeysChanged;
        ApplyConnectionState(_chatManager.IsOnline, offline: _chatManager.IsOffline);
        FillProfile();
        LoadMessages();

        _scrollToMessageId = scrollToMessageId;
        if (_scrollToMessageId is not null)
        {
            var target = _messages.FirstOrDefault(m => m.Id == _scrollToMessageId);
            if (target is not null)
            {
                ScrollToMessage(target);
                _scrollToMessageId = null;
            }
        }

        _ = FillMentionMembersAsync();

        _suppressDraft = true;
        MessageEntry.Text = await _chatManager.GetDraftAsync(_username) ?? string.Empty;
        _suppressDraft = false;
        DraftLabel.IsVisible = MessageEntry.Text.Length > 0;
        ScrollToBottom();
    }

    public void Close() => ResetChat();

    private void ResetChat()
    {
        StopVoicePlayback();

        if (_username is not null)
        {
            _draftCts?.Cancel();
            _ = _chatManager.SaveDraftAsync(_username, MessageEntry.Text ?? string.Empty);
            _chatManager.CloseChat(_username);
        }

        Unsubscribe();
        _username = null;
        _conversation = null;
        _selected = null;
        _pinnedIndex = 0;
        _messageQuery = string.Empty;
        _messages.Clear();
        _mediaItems.Clear();
        _pinnedItems.Clear();

        // Ответ и редактирование относятся к конкретному чату: при смене чата они сбрасываются,
        // иначе цитата из одного чата ушла бы в другой.
        _replyingTo = null;
        _editingMessageId = null;
        ReplyBar.IsVisible = false;
        EditBar.IsVisible = false;

        _suppressDraft = true;
        MessageEntry.Text = string.Empty;
        _suppressDraft = false;

        MessageSearchBar.Text = string.Empty;
        MessageSearchBar.IsVisible = false;
        InfoPanel.IsVisible = false;
        ContextMenuOverlay.IsVisible = false;
        OfflineBanner.IsVisible = false;
        DraftLabel.IsVisible = false;
        HideHint();
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _chatManager.MessageAdded += OnMessageAdded;
        _chatManager.MessageStatusChanged += OnMessageStatusChanged;
        _chatManager.MessageMediaChanged += OnMessageMediaChanged;
        _chatManager.MessageMediaProgress += OnMessageMediaProgress;
        _chatManager.MessageDeleted += OnMessageDeleted;
        _chatManager.ConversationDeleted += OnConversationDeleted;
        _chatManager.ConversationCleared += OnConversationCleared;
        _chatManager.TypingReceived += OnTypingReceived;
        _chatManager.Error += OnError;
        _chatManager.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _chatManager.MessageAdded -= OnMessageAdded;
        _chatManager.MessageStatusChanged -= OnMessageStatusChanged;
        _chatManager.MessageMediaChanged -= OnMessageMediaChanged;
        _chatManager.MessageMediaProgress -= OnMessageMediaProgress;
        _chatManager.MessageDeleted -= OnMessageDeleted;
        _chatManager.ConversationDeleted -= OnConversationDeleted;
        _chatManager.ConversationCleared -= OnConversationCleared;
        _chatManager.TypingReceived -= OnTypingReceived;
        _chatManager.Error -= OnError;
        _chatManager.ConnectionStateChanged -= OnConnectionStateChanged;
    }

    private bool IsCurrent(string username) =>
        _username is not null && string.Equals(username, _username, StringComparison.OrdinalIgnoreCase);

    private void LoadMessages()
    {
        _messages.Clear();
        if (_username is null || _conversation is null)
        {
            return;
        }

        _allPollVotes.Clear();
        try
        {
            var dbg = string.Join("\n", _conversation.Messages.OrderBy(m => m.SentAtUnixMs)
                .Select(m => $"{m.Kind}|len={m.Text?.Length}|{(m.Text ?? "").Substring(0, Math.Min(40, m.Text?.Length ?? 0))}"));
            System.IO.File.WriteAllText(@"C:\Users\Administrator\AppData\Local\Temp\opencode\bubbles_debug.txt", dbg);
        }
        catch { }
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var stored in _conversation.Messages
                     .Where(m => m.ExpireAtUnixMs == 0 || m.ExpireAtUnixMs > nowUnixMs)
                     .OrderBy(item => item.SentAtUnixMs))
        {
            var vm = CreateMessageVm(stored, _username);
            if (stored.Kind == MessageKinds.PollVote)
            {
                _allPollVotes.Add(vm);
                continue;
            }
            _messages.Add(vm);
        }

        ApplyPollVotes();
        RefreshDerived();
        _ = LoadImagesAsync(_messages.ToList(), _username);
        if (_session.LinkPreviewEnabled)
        {
            _ = EnsurePreviewsForLoadedAsync();
        }
    }

    private MessageVm CreateMessageVm(StoredMessage stored, string peer)
    {
        var vm = new MessageVm(stored, _session.Username, peer);
        vm.MentionTapped += OnMessageMentionTapped;
        vm.HashtagTapped += OnMessageHashtagTapped;
        vm.LinkTapped += OnMessageLinkTapped;
        vm.PollVoteRequested += OnPollVoteRequested;
        return vm;
    }

    /// <summary>Тап по ссылке в тексте сообщения ЛС.</summary>
    private void OnMessageLinkTapped(MessageVm message, string url) => _ = OpenUrlAsync(url);

    /// <summary>Тап по @упоминанию в сообщении ЛС — открыть профиль участника, как в Telegram.</summary>
    private async void OnMessageMentionTapped(MessageVm message, string username)
    {
        var page = HostPage;
        if (page is null || string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        if (string.Equals(username, _session.Username, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await page.Navigation.PushAsync(new UserProfilePage(username));
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_085"), ex.Message), isError: true);
        }
    }

    private void InsertSorted(MessageVm message)
    {
        var index = _messages.Count;
        while (index > 0 && _messages[index - 1].SentAtUnixMs > message.SentAtUnixMs)
        {
            index--;
        }

        _messages.Insert(index, message);
    }

    private MessageVm? FindMessage(string messageId) =>
        _messages.FirstOrDefault(item => item.Id == messageId);

    private void RefreshDerived()
    {
        var today = DateTime.Today;
        for (var i = 0; i < _messages.Count; i++)
        {
            var current = _messages[i];
            var previous = i > 0 ? _messages[i - 1] : null;
            var next = i + 1 < _messages.Count ? _messages[i + 1] : null;

            current.HasDateLabel = previous is null || previous.LocalDate != current.LocalDate;
            current.DateLabel = FormatDateLabel(current.LocalDate, today);
            current.ShowAvatar = !current.IsMine &&
                (next is null || next.IsMine || next.LocalDate != current.LocalDate ||
                 next.SenderDisplay != current.SenderDisplay);
            current.MatchesSearch = _messageQuery.Length == 0 ||
                current.DisplayText.Contains(_messageQuery, StringComparison.OrdinalIgnoreCase);
        }

        // Старые ответы сохранены без автора: берём его у цитируемого сообщения по идентификатору,
        // если оно есть в загруженной переписке.
        var byId = new Dictionary<string, MessageVm>(StringComparer.Ordinal);
        foreach (var item in _messages)
        {
            byId[item.Id] = item;
        }

        foreach (var reply in _messages.Where(item => item.HasReplyQuote && item.ReplyToMessageId is not null))
        {
            if (byId.TryGetValue(reply.ReplyToMessageId!, out var original))
            {
                reply.SetReplyAuthorFallback(original.ReplyAuthorName, _session.Username);
            }
        }

        UpdateAlbumPreviews();
        RefreshSidePanels();
    }

    private List<MessageVm> GetPinned() =>
        _messages.Where(item => item.IsPinned).OrderByDescending(item => item.SentAtUnixMs).ToList();

    private void RefreshSidePanels()
    {
        var pinned = GetPinned();
        _pinnedIndex = Math.Clamp(_pinnedIndex, 0, Math.Max(0, pinned.Count - 1));

        _pinnedItems.Clear();
        foreach (var item in pinned)
        {
            _pinnedItems.Add(item);
        }

        var hasPinned = pinned.Count > 0;
        var counter = hasPinned ? PinnedCounterText(_pinnedIndex, pinned.Count) : string.Empty;
        PinnedSectionInline.IsVisible = hasPinned;
        PinnedCountLabelInline.Text = counter;
        PinnedLabelInline.Text = hasPinned ? Shorten(pinned[_pinnedIndex].DisplayText, 80) : string.Empty;
        PinnedCountLabel.Text = counter;
        PinnedEmptyLabel.IsVisible = !hasPinned;

        // Медиа и файлы: сгруппированы по датам, как в Telegram.
        _mediaItems.Clear();
        var mediaCount = 0;
        DateTime? currentDate = null;
        foreach (var item in _messages.Reverse().Where(item => item.IsImage || item.IsFile))
        {
            var date = item.LocalDate;
            if (currentDate != date)
            {
                currentDate = date;
                _mediaItems.Add(MediaListItemVm.Header(MediaDateHeader(date)));
            }

            _mediaItems.Add(MediaListItemVm.Item(item));
            mediaCount++;
        }

        MediaEmptyLabel.IsVisible = mediaCount == 0;
    }

    /// <summary>Заголовок группы медиа: «Сегодня», «Вчера» или дата.</summary>
    private static string MediaDateHeader(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
        {
            return Loc.T("rs_142", "Сегодня");
        }

        if (date == today.AddDays(-1))
        {
            return Loc.T("rs_027", "Вчера");
        }

        return date.Year == today.Year
            ? date.ToString("d MMMM")
            : date.ToString("d MMMM yyyy");
    }

    private static string PinnedCounterText(int index, int count) =>
        count >= 4 ? "4+" : $"{index + 1}-{count}";

    private async Task LoadImagesAsync(IReadOnlyList<MessageVm> items, string user)
    {
        foreach (var item in items.Where(item => item.IsImage && item.ImageSource is null))
        {
            var bytes = await _chatManager.GetLocalMediaAsync(item.Id);
            if (bytes is null)
            {
                _ = _chatManager.EnsureMediaAsync(user, item.Id);
                continue;
            }

            var copy = bytes;
            MainThread.BeginInvokeOnMainThread(() =>
                item.SetImageBytes(copy));
        }

        UpdateAlbumPreviews();

        // Видео (кружки и видео-файлы): та же догрузка, что у картинок.
        // Большие видео в память VM не тащим (OOM): файл качается в кэш
        // для нативного плеера, карточка с ▶ покажется сама.
        foreach (var item in items.Where(item => item.IsVideoMedia && !item.IsVideoReady))
        {
            if (item.IsVideoOversize)
            {
                if (!item.MediaDownloaded)
                {
                    _ = _chatManager.EnsureMediaAsync(user, item.Id);
                }

                continue;
            }

            var bytes = await _chatManager.GetLocalMediaAsync(item.Id);
            if (bytes is null)
            {
                _ = _chatManager.EnsureMediaAsync(user, item.Id);
                continue;
            }

            var copy = bytes;
            MainThread.BeginInvokeOnMainThread(() =>
                item.SetVideoBytes(copy));
        }
    }

    /// <summary>
    /// Альбом: первое фото блока показывает сетку 2×2 из фото альбома,
    /// остальные фото альбома в переписке скрыты.
    /// </summary>
    private void UpdateAlbumPreviews()
    {
        foreach (var group in _messages
                     .Where(item => item.IsAlbum)
                     .GroupBy(item => item.AlbumId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(item => item.AlbumIndex).ToList();
            var head = ordered[0];

            var images = ordered
                .Where(item => item.ImageSource is not null)
                .Select(item => item.ImageSource!)
                .Take(4)
                .ToList();

            head.SetAlbumImages(images);
        }
    }

    private async Task ReloadMediaAsync(MessageVm message, string user)
    {
        var conversation = await _chatManager.GetConversationAsync(user);
        var fresh = conversation?.Messages.FirstOrDefault(item => item.Id == message.Id);
        var needsBytes = fresh is not null && (message.IsImage || message.IsVideoMedia);
        var oversizeVideo = needsBytes && message.IsVideoMedia && message.IsVideoOversize;
        if (oversizeVideo)
        {
            // Большое видео в память VM не тащим (OOM): файл качается в кэш,
            // открывается нативным плеером по кнопке ▶ в карточке.
            needsBytes = false;
        }

        byte[]? bytes = needsBytes
            ? await _chatManager.GetLocalMediaAsync(message.Id)
            : null;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (fresh is not null)
            {
                message.Update(fresh, _session.Username, user);
            }

            if (bytes is not null)
            {
                var copy = bytes;
                if (message.IsImage)
                {
                    message.SetImageBytes(copy);
                }
                else if (message.IsVideoMedia)
                {
                    message.SetVideoBytes(copy);
                }
            }

            RefreshSidePanels();
            UpdateAlbumPreviews();
        });
    }

    private void OnMessageAdded(StoredMessage message, string username) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_username is null || !IsCurrent(username))
            {
                return;
            }

            var peer = _username;
            var existing = FindMessage(message.Id);
            if (existing is not null)
            {
                existing.ClearPreviews();
                existing.Update(message, _session.Username, peer);
                RefreshDerived();
                if (_session.LinkPreviewEnabled && existing.HasUrls)
                {
                    _ = EnsurePreviewAsync(existing);
                }
                if (_scrollToMessageId is not null && message.Id == _scrollToMessageId)
                {
                    ScrollToMessage(existing);
                    _scrollToMessageId = null;
                }

                // Обновляем аватар собеседника в шапке чата.
                if (!message.IsMine && !string.IsNullOrWhiteSpace(message.SenderAvatarBase64) &&
                    _conversation is not null &&
                    !string.Equals(_conversation.AvatarBase64, message.SenderAvatarBase64, StringComparison.Ordinal))
                {
                    _conversation.AvatarBase64 = message.SenderAvatarBase64;
                    ContactAvatar.Source = AvatarSource(_conversation.AvatarBase64);
                }

                return;
            }

            var created = CreateMessageVm(message, peer);
            if (message.Kind == MessageKinds.PollVote)
            {
                _allPollVotes.Add(created);
                ApplyPollVotes();
                return;
            }
            InsertSorted(created);
            RefreshDerived();
            ScrollToBottom();
            _ = LoadImagesAsync(new[] { created }, peer);
            if (_session.LinkPreviewEnabled && created.HasUrls)
            {
                _ = EnsurePreviewAsync(created);
            }
            if (_scrollToMessageId is not null && created.Id == _scrollToMessageId)
            {
                ScrollToMessage(created);
                _scrollToMessageId = null;
            }

            // Обновляем аватар собеседника в шапке чата для нового сообщения.
            if (!message.IsMine && !string.IsNullOrWhiteSpace(message.SenderAvatarBase64) &&
                _conversation is not null &&
                !string.Equals(_conversation.AvatarBase64, message.SenderAvatarBase64, StringComparison.Ordinal))
            {
                _conversation.AvatarBase64 = message.SenderAvatarBase64;
                ContactAvatar.Source = AvatarSource(_conversation.AvatarBase64);
            }
        });

    private void OnMessageStatusChanged(string messageId, string status) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var message = FindMessage(messageId);
            if (message is not null)
            {
                message.DeliveryStatus = status;
            }
        });

    private void OnMessageMediaChanged(string messageId, string username) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!IsCurrent(username))
            {
                return;
            }

            var message = FindMessage(messageId);
            if (message is not null)
            {
                _ = ReloadMediaAsync(message, username);
            }
        });

    /// <summary>Прогресс загрузки вложения: обновляем полосу в пузыре сообщения.</summary>
    private void OnMessageMediaProgress(string messageId, string username, long downloaded, long total) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!IsCurrent(username))
            {
                return;
            }

            FindMessage(messageId)?.SetMediaProgress(downloaded, total);
        });

    /// <summary>Кнопка ✕ у загружаемого вложения — отменить загрузку.</summary>
    private void OnCancelMediaDownloadClicked(object sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string messageId })
        {
            _chatManager.CancelMediaDownload(messageId);
        }
    }

    private void OnTypingReceived(string username) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (IsCurrent(username))
            {
                ShowHint(string.Format(Loc.T("rs_006"), username), isError: false);
            }
        });

    private void OnError(string message) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_username is not null)
            {
                ShowHint(message, isError: true);
            }
        });

    private void OnConnectionStateChanged(bool connected) =>
        MainThread.BeginInvokeOnMainThread(() => ApplyConnectionState(connected, offline: !connected));

    private void ApplyConnectionState(bool connected, bool offline)
    {
        if (_username is null)
        {
            return;
        }

        OfflineBanner.IsVisible = offline;
        ContactStatusLabel.Text = connected ? "Онлайн" : offline ? Loc.T("uc_056", "Офлайн") : Loc.T("uc_058", "Подключение…");
        ContactStatusLabel.TextColor = Res(offline && !connected ? "Warning" : "TextMuted");
    }

    private void FillProfile()
    {
        if (_conversation is null)
        {
            return;
        }

        ProfileAvatar.Source = AvatarSource(_conversation.AvatarBase64);
        ProfileName.Text = _conversation.Username;
        ProfileStatus.Text = _conversation.OnlineStatus;
        ProfileBio.IsVisible = false;
        ProfileFingerprint.Text = _conversation.Fingerprint;
        ProfileUserId.Text = _conversation.UserId;
        ProfileCreated.Text = "—";
    }

    private void OnMessageTextChanged(object? sender, TextChangedEventArgs e)
    {
        AttachMenuPanel.IsVisible = false;

        if (_suppressDraft || _username is null)
        {
            return;
        }

        SendTypingIfNeeded();
        ScheduleDraftSave(_username, e.NewTextValue ?? string.Empty);
    }

    private async Task FillMentionMembersAsync()
    {
        try
        {
            // В личном чате тоже есть @упоминания: список кандидатов — контакты
            // (все переписки) и сам собеседник, как список контактов в Telegram.
            var rows = await _chatManager.GetConversationsAsync();
            var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_username))
            {
                usernames.Add(_username);
            }

            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.Username))
                {
                    usernames.Add(row.Username);
                }
            }

            MainThread.BeginInvokeOnMainThread(() => MessageEntry.SetMembers(usernames));
        }
        catch
        {
            // Меню @упоминаний — необязательное оформление: без списка
            // участников оно просто не появится.
        }
    }

    private void SendTypingIfNeeded()
    {
        if (_conversation is null || !_session.ShowTypingStatus || string.IsNullOrEmpty(MessageEntry.Text))
        {
            return;
        }

        if (DateTime.UtcNow - _lastTypingSentUtc < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastTypingSentUtc = DateTime.UtcNow;
        _ = _chatManager.NotifyTypingAsync(_conversation.UserId);
    }

    private void ScheduleDraftSave(string user, string text)
    {
        _draftCts?.Cancel();
        _draftCts = new CancellationTokenSource();
        _ = SaveDraftLaterAsync(user, text, _draftCts.Token);
    }

    private async Task SaveDraftLaterAsync(string user, string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(700, token);
            await _chatManager.SaveDraftAsync(user, text);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (IsCurrent(user))
                {
                    DraftLabel.IsVisible = text.Length > 0;
                }
            });
        }
        catch
        {
            // Отмена или ошибка сохранения черновика не критичны.
        }
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        AttachMenuPanel.IsVisible = false;

        var user = _username;
        if (user is null || _sending || _sendingAttachment)
        {
            return;
        }

        var text = (MessageEntry.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        _sending = true;
        var replyTarget = _replyingTo;
        var isEditing = _editingMessageId is not null;
        var sendOnce = _viewOnceNext;
        MessageEntry.Text = string.Empty;
        try
        {
            if (isEditing)
            {
                await _chatManager.EditMessageAsync(user, _editingMessageId!, text);
                _editingMessageId = null;
                EditBar.IsVisible = false;
            }
            else
            {
                await _chatManager.SendMessageAsync(
                    user,
                    text,
                    replyToMessageId: replyTarget?.Id,
                    replyToText: replyTarget?.Text,
                    replyToFileName: replyTarget?.FileName,
                    replyToSender: replyTarget?.ReplyAuthorName,
                    isOnce: sendOnce,
                    expireAtUnixMs: _ttlNext);
                _ttlNext = null;
                UpdateActionsButton();
                ResetViewOnce();
                _replyingTo = null;
                ReplyBar.IsVisible = false;
            }

            if (!_chatManager.IsOnline)
            {
                ShowHint(Loc.T("rs_118", "Офлайн: сообщение сохранено и будет отправлено после подключения."), isError: false);
            }

            ScrollToBottom();
        }
        catch (Exception ex)
        {
            if (IsCurrent(user))
            {
                MessageEntry.Text = text;
            }

            ShowHint(string.Format(Loc.T("rs_088"), ex.Message), isError: true);
        }
        finally
        {
            _sending = false;
        }
    }

    /// <summary>
    /// Опции выбора файлов. На Windows фильтр расширений не ставим вовсе:
    /// белый список всегда неполный, и файл вне списка просто не виден
    /// в диалоге (выглядит как «пустое окно выбора»). Вид сообщения всё равно
    /// определяется по содержимому в SendPickedFilesAsync. На Android
    /// оставляем MIME-фильтры — там они надёжны.
    /// </summary>
    private static PickOptions PickerOptions(string title, FilePickerFileType? androidFileTypes)
    {
#if WINDOWS
        _ = androidFileTypes;
        return new PickOptions { PickerTitle = title };
#else
        return new PickOptions { PickerTitle = title, FileTypes = androidFileTypes };
#endif
    }

    /// <summary>MIME-фильтр «фото или видео» для Android.</summary>
    private static readonly FilePickerFileType AttachMediaTypes = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, new[] { "image/*", "video/*" } }
        });

    /// <summary>MIME-фильтр аудиофайлов для Android.</summary>
    private static readonly FilePickerFileType AttachAudioTypes = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.Android, new[] { "audio/*" } }
        });

    /// <summary>Кнопка «👁» — включить или выключить одноразовую отправку.</summary>
    private void OnViewOnceToggled(object? sender, EventArgs e)
    {
        _viewOnceNext = !_viewOnceNext;
        ViewOnceButton.BackgroundColor = _viewOnceNext
            ? (Color)Application.Current!.Resources["Accent"]
            : Colors.Transparent;
        ViewOnceButton.TextColor = _viewOnceNext
            ? (Color)Application.Current.Resources["AccentText"]
            : (Color)Application.Current.Resources["TextMuted"];
    }

    /// <summary>Сбросить режим одноразовой отправки после отправки.</summary>
    private void ResetViewOnce()
    {
        if (!_viewOnceNext)
        {
            return;
        }

        _viewOnceNext = false;
        ViewOnceButton.BackgroundColor = Colors.Transparent;
        ViewOnceButton.TextColor = (Color)Application.Current!.Resources["TextMuted"];
    }

    /// <summary>Кнопка 📎 — показать или скрыть меню вложений (как в Telegram по тапу).</summary>
    private void OnAttachClicked(object? sender, EventArgs e)
    {
        if (_sendingAttachment)
        {
            return;
        }

        AttachMenuPanel.IsVisible = !AttachMenuPanel.IsVisible;
        CancelHideAttachMenu();
    }

    private void ShowAttachMenu()
    {
        CancelHideAttachMenu();
        AttachMenuPanel.IsVisible = true;
    }

    private void HideAttachMenuDelayed()
    {
        _attachHideTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _attachHideTimer = null;

        // Небольшая задержка, чтобы меню не скрылось при переводе мыши с кнопки на пункт меню.
        _attachHideTimer = new System.Threading.Timer(_ =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                AttachMenuPanel.IsVisible = false;
                _attachHideTimer = null;
            });
        }, null, 300, Timeout.Infinite);
    }

    private void CancelHideAttachMenu()
    {
        _attachHideTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _attachHideTimer = null;
    }

    /// <summary>Выбор пункта меню скрепки: фото/видео, файл, музыка, голосовое, кружок.</summary>
    private async void OnAttachMenuItemTapped(object? sender, EventArgs e)
    {
        CancelHideAttachMenu();
        AttachMenuPanel.IsVisible = false;
        var user = _username;
        var page = HostPage;
        if (user is null || page is null || _sendingAttachment)
        {
            return;
        }

        var action = (sender as Element)?.StyleId;
        switch (action)
        {
            case "photo":
                // Вид сообщения определяется по расширению в ResolveContentType:
                // картинки сжимаются и уходят как фото, всё остальное — как файл.
                var mediaFiles = await FilePicker.Default.PickMultipleAsync(
                    PickerOptions(Loc.T("rs_036", "Выберите фото или видео"), AttachMediaTypes));
                await SendPickedFilesAsync(user, mediaFiles, messageKind: null);
                break;

            case "file":
                var files = await FilePicker.Default.PickMultipleAsync(
                    new PickOptions { PickerTitle = Loc.T("rs_035", "Выберите файлы") });
                await SendPickedFilesAsync(user, files, MessageKinds.File);
                break;

            case "music":
                var audioFiles = await FilePicker.Default.PickMultipleAsync(
                    PickerOptions(Loc.T("rs_029", "Выберите музыку"), AttachAudioTypes));
                await SendPickedFilesAsync(user, audioFiles, MessageKinds.File);
                break;

            case "voice":
                var recorded = await RecordVoiceMessageAsync(page);
                if (recorded is null)
                {
                    return;
                }

                await SendOneAttachmentAsync(
                    user, "voice.m4a", recorded.Value.ContentType, recorded.Value.Data, MessageKinds.Voice);
                break;

            case "circle":
                var circle = await RecordVideoCircleAsync(page);
                if (circle is null)
                {
                    return;
                }

                await SendOneAttachmentAsync(
                    user, "circle.mp4", circle.Value.ContentType, circle.Value.Data, MessageKinds.VideoCircle);
                break;
        }
    }

    /// <summary>
    /// Читает выбранные файлы и отправляет их одной очередью: фото
    /// пережимаются, слишком большие пропускаются с подсказкой.
    /// </summary>
    private async Task SendPickedFilesAsync(
        string user, IEnumerable<FileResult>? files, string? messageKind)
    {
        var picked = files?.ToList() ?? new List<FileResult>();
        if (picked.Count == 0 || !IsCurrent(user))
        {
            return;
        }

        var items = new List<(string FileName, byte[] Data, string ContentType, string? Kind)>();
        foreach (var file in picked)
        {
            var data = await ReadAttachmentAsync(file);
            if (data is null)
            {
                ShowHint(
                    string.Format(Loc.T("rs_007"), file.FileName, MediaLimits.MaxBytes / (1024 * 1024)),
                    isError: true);
                continue;
            }

            var contentType = ResolveContentType(file);
            var kind = messageKind
                ?? (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : MessageKinds.File);
            items.Add((file.FileName, data, contentType, kind));
        }

        await SendAttachmentsAsync(user, items);
    }

    /// <summary>Отправка одного вложения (записанные голосовое/кружок).</summary>
    private Task SendOneAttachmentAsync(
        string user, string fileName, string contentType, byte[] data, string? messageKind) =>
        SendAttachmentsAsync(user, new[] { (fileName, data, contentType, messageKind) });

    /// <summary>
    /// Ставит вложения в очередь отправки: фото пережимаются, слишком большие
    /// пропускаются с подсказкой. Загрузка каждого файла идет в фоне с
    /// индикатором прогресса и кнопкой отмены в панели очереди.
    /// </summary>
    private async Task SendAttachmentsAsync(
        string user,
        IReadOnlyList<(string FileName, byte[] Data, string ContentType, string? Kind)> items)
    {
        if (items.Count == 0 || !IsCurrent(user))
        {
            return;
        }

        _sendingAttachment = true;
        try
        {
            // Несколько фото отправляются альбомом: у них общий идентификатор
            // и номер в альбоме (как в Telegram).
            var albumId = items.Count > 1 ? Guid.NewGuid().ToString("N") : null;
            var albumIndex = 0;

            // Ответ на сообщение уходит вместе с вложениями, после этого панель ответа закрывается.
            var reply = _replyingTo;
            _replyingTo = null;
            ReplyBar.IsVisible = false;

            foreach (var (fileName, data, contentType, kind) in items)
            {
                var prepared = kind is null
                    ? MediaCompression.CompressImage(data, contentType)
                    : (Data: data, ContentType: contentType);

                _pendingMedia.Add(new PendingMediaItem
                {
                    FileName = fileName,
                    Data = prepared.Data,
                    ContentType = prepared.ContentType,
                    MessageKind = kind,
                    AlbumId = albumId,
                    AlbumIndex = albumIndex,
                    AlbumCount = items.Count,
                    ReplyToMessageId = reply?.Id,
                    ReplyToText = reply?.Text,
                    ReplyToFileName = reply?.FileName,
                    ReplyToSender = reply?.ReplyAuthorName
                });

                albumIndex++;
            }

            SendQueuePanel.IsVisible = true;
            _ = ProcessMediaQueueAsync();
        }
        finally
        {
            // _sendingAttachment сбрасывается в ProcessMediaQueueAsync,
            // когда очередь полностью опустеет.
        }
    }

    /// <summary>
    /// Последовательно загружает файлы из очереди. Каждый файл — отдельный
    /// элемент с прогрессом; отмена любого элемента удаляет его и локальное
    /// сообщение, которое уже было сохранено как ставящееся в очередь.
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
                    await RemoveQueuedItemAsync(item);
                    continue;
                }

                // Отчёт о прогрессе приходит из сетевого потока — маршалим в UI-поток.
                var progress = new Progress<int>(p =>
                    MainThread.BeginInvokeOnMainThread(() => item.Progress = p));

                try
                {
                    if (_username is null)
                    {
                        break;
                    }

                    await _chatManager.SendMediaAsync(
                        _username,
                        item.FileName,
                        item.ContentType,
                        item.Data,
                        messageKind: item.MessageKind,
                        clientMessageId: item.Id,
                        progress: progress,
                        cancellationToken: item.Cts.Token,
                        albumId: item.AlbumId,
                        albumIndex: item.AlbumIndex,
                        albumCount: item.AlbumCount,
                        replyToMessageId: item.ReplyToMessageId,
                        replyToText: item.ReplyToText,
                        replyToFileName: item.ReplyToFileName,
                        replyToSender: item.ReplyToSender);

                    item.Progress = 100;
                    item.Status = Loc.T("set_036", "Готово");
                    await Task.Delay(350);
                    // Успех — убираем только из панели очереди, сообщение в чате остаётся.
                    await RemoveFromQueueAsync(item);
                }
                catch (OperationCanceledException)
                {
                    await RemoveQueuedItemAsync(item);
                }
                catch (Exception ex)
                {
                    item.Status = Loc.T("common_error", "Ошибка");
                    MainThread.BeginInvokeOnMainThread(() =>
                        ShowHint(string.Format(Loc.T("uc_036"), item.FileName, ex.Message), isError: true));
                    await RemoveQueuedItemAsync(item);
                }
            }
        }
        finally
        {
            _processingMediaQueue = false;
            _sendingAttachment = false;
            SendQueuePanel.IsVisible = false;
        }
    }

    private async Task RemoveQueuedItemAsync(PendingMediaItem item)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _pendingMedia.Remove(item);

            var message = _messages.FirstOrDefault(m => m.Id == item.Id);
            if (message is not null)
            {
                _messages.Remove(message);
            }

            SendQueuePanel.IsVisible = _pendingMedia.Count > 0;
        });

        try
        {
            await _chatManager.DeleteMessageAsync(item.Id);
        }
        catch
        {
            // Локальная копия уже убрана из интерфейса.
        }
    }

    /// <summary>Убрать элемент только из панели очереди (сообщение в чате остаётся).</summary>
    private async Task RemoveFromQueueAsync(PendingMediaItem item)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _pendingMedia.Remove(item);
            SendQueuePanel.IsVisible = _pendingMedia.Count > 0;
        });
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

    /// <summary>📄 — экспортировать переписку в самостоятельный HTML-файл.</summary>
    private async void OnExportChatClicked(object sender, EventArgs e)
    {
        var user = _username;
        if (user is null)
        {
            return;
        }

        if (_messages.Count == 0)
        {
            ShowHint(Loc.T("rs_106", "Нет сообщений для экспорта."), isError: true);
            return;
        }

        try
        {
            var html = BuildExportHtml(user, _messages.ToList());
            var fileName = $"SecureChat_{user}_{DateTime.Now:yyyyMMdd_HHmm}.html";
            var path = Path.Combine(FileSystem.CacheDirectory, fileName);
            await File.WriteAllTextAsync(path, html, System.Text.Encoding.UTF8);

            await Launcher.Default.OpenAsync(new OpenFileRequest(fileName, new ReadOnlyFile(path)));
            ShowHint(Loc.T("rs_173", "Экспорт сохранён и открыт в браузере."), isError: false);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_100"), ex.Message), isError: true);
        }
    }

    /// <summary>
    /// Собирает автономную HTML-страницу переписки: даты, пузыри слева/справа,
    /// цитаты ответов, отметки «переслано/отредактировано» и реакции.
    /// </summary>
    private static string BuildExportHtml(string username, IReadOnlyList<MessageVm> messages)
    {
        static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value);

        const string css = """
            * { box-sizing: border-box; }
            body {
                margin: 0; padding: 24px;
                background: #0e1621; color: #e9edf5;
                font: 15px/1.45 -apple-system, "Segoe UI", Roboto, Arial, sans-serif;
            }
            h1 { font-size: 20px; margin: 0 0 4px; }
            .meta { color: #7d8b9e; font-size: 13px; margin-bottom: 20px; }
            .day { text-align: center; margin: 22px 0 12px; }
            .day span {
                background: #182533; color: #7d8b9e;
                padding: 4px 12px; border-radius: 14px; font-size: 12px;
            }
            .msg {
                max-width: 640px; padding: 8px 12px; border-radius: 12px;
                margin: 6px 0;
            }
            .msg.them { background: #182533; }
            .msg.me { background: #2b5278; margin-left: auto; }
            .sender { font-size: 12px; color: #6ab2f2; font-weight: 600; margin-bottom: 2px; }
            .quote {
                border-left: 3px solid #6ab2f2; padding: 2px 8px; margin-bottom: 4px;
                color: #9db2c9; font-size: 13px;
                background: rgba(255,255,255,0.05); border-radius: 4px;
            }
            .text { white-space: pre-wrap; word-break: break-word; }
            .tag { color: #9db2c9; font-size: 12px; }
            .time { text-align: right; font-size: 11px; color: #7d8b9e; margin-top: 4px; }
            .reactions { margin-top: 6px; display: flex; gap: 6px; flex-wrap: wrap; }
            .reactions span {
                background: rgba(255,255,255,0.08); border-radius: 10px;
                padding: 1px 8px; font-size: 12px;
            }
            """;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ru\"><head><meta charset=\"utf-8\">");
        sb.AppendLine(string.Format(Loc.T("fx_002"), Escape(username)));
        sb.AppendLine($"<style>{css}</style></head><body>");
        sb.AppendLine(string.Format(Loc.T("fx_003"), Escape(username)));
        sb.AppendLine(
            string.Format("<div class=\"meta\">{0}</div>", string.Format(Loc.T("fx_004"), DateTime.Now, messages.Count)));

        foreach (var message in messages)
        {
            if (message.HasDateLabel)
            {
                sb.AppendLine($"<div class=\"day\"><span>{Escape(message.DateLabel)}</span></div>");
            }

            var body = message.Kind switch
            {
                MessageKinds.Voice => "🎤 Голосовое сообщение",
                MessageKinds.VideoCircle => "📹 Видеокружок",
                MessageKinds.Image => "📷 Фото",
                MessageKinds.File => $"📎 {message.FileName} ({message.FileSizeText})",
                _ => message.DisplayText
            };

            var quote = message.HasReplyQuote
                ? $"<div class=\"quote\"><b>{Escape(message.ReplyQuoteAuthorText)}</b><br>{Escape(message.ReplyQuoteText)}</div>"
                : string.Empty;

            var flags = string.Empty;
            if (message.IsForwarded)
            {
                flags += $" <span class=\"tag\">{Escape(message.ForwardedText)}</span>";
            }

            if (message.IsEdited)
            {
                flags += " <span class=\"tag\">" + Loc.T("fx_005") + "</span>";
            }

            var reactions = string.Empty;
            if (message.HasReactions)
            {
                var chips = string.Join(
                    " ",
                    message.ReactionChips.Select(c => $"{c.Emoji} {c.CountText}"));
                reactions = $"<div class=\"reactions\"><span>{chips}</span></div>";
            }

            var sender = message.IsMine ? Loc.T("rs_028", "Вы") : message.SenderDisplay;
            sb.AppendLine(
                $"<div class=\"msg {(message.IsMine ? "me" : "them")}\">" +
                $"<div class=\"sender\">{Escape(sender)}</div>" +
                $"<div class=\"text\">{quote}{Escape(body)}{flags}</div>" +
                $"{reactions}" +
                $"<div class=\"time\">{message.TimeText}</div>" +
                "</div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

#if WINDOWS
    private static ChatView? _activeDropTarget;
    private static Microsoft.UI.Xaml.UIElement? _hookedContent;
    private static System.Threading.Timer? _dropSweepTimer;
    private static int _dragOverSeen;
    private static bool _elevatedWarningShown;

    /// <summary>Диагностический лог перетаскивания (временный файл в %TEMP%).</summary>
    private static readonly string DropLogPath = Path.Combine(
        Path.GetTempPath(),
        "securechat-drop.log");

    private static void LogDrop(string message)
    {
        try
        {
            File.AppendAllText(
                DropLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Лог — не критично.
        }
    }

    /// <summary>Процесс запущен с правами администратора (важно для перетаскивания, UIPI).</summary>
    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Включает приём перетаскивания на всех элементах окна: WinUI проверяет
    /// AllowDrop у элемента под курсором, а не только у корня.
    /// </summary>
    private static int EnableDropInTree(Microsoft.UI.Xaml.DependencyObject? root)
    {
        if (root is null)
        {
            return 0;
        }

        var count = 0;
        try
        {
            var children = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < children; i++)
            {
                Microsoft.UI.Xaml.DependencyObject child;
                try
                {
                    child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                }
                catch
                {
                    continue;
                }

                if (child is Microsoft.UI.Xaml.UIElement element)
                {
                    element.AllowDrop = true;
                    count++;
                }

                count += EnableDropInTree(child);
            }
        }
        catch
        {
            // Дерево меняется на лету — частичный обход не страшен.
        }

        return count;
    }
    private bool _pasteHooked;

    /// <summary>
    /// Однократно подключает перетаскивание файлов в окно приложения
    /// и вставку из буфера (Ctrl+V). Файлы уходят в активный чат.
    /// </summary>
    private void EnsureWindowHooks()
    {
        _activeDropTarget = this;

        // WinUI отдаёт события перетаскивания только элементам с AllowDrop=true.
        // Включаем и на корневом элементе окна, и на самом чате: иначе
        // перетаскивание в область переписки не срабатывает.
        if (Handler?.PlatformView is Microsoft.UI.Xaml.UIElement ownView)
        {
            ownView.AllowDrop = true;
        }

        if (TryGetPlatformWindow() is { } platformWindow &&
            platformWindow.Content is Microsoft.UI.Xaml.UIElement content)
        {
            content.AllowDrop = true;

            // Обработчики вешаем на текущее содержимое окна: при навигации
            // корневой элемент может смениться.
            if (!ReferenceEquals(_hookedContent, content))
            {
                _hookedContent = content;

                content.AddHandler(
                    Microsoft.UI.Xaml.UIElement.DragOverEvent,
                    new Microsoft.UI.Xaml.DragEventHandler(OnWindowDragOver),
                    true);
                content.AddHandler(
                    Microsoft.UI.Xaml.UIElement.DropEvent,
                    new Microsoft.UI.Xaml.DragEventHandler(OnWindowDrop),
                    true);
            }

            LogDrop($"hooks: content={content.GetType().Name} elevated={IsRunningElevated()}");
        }

        EnableDropInTree(Handler?.PlatformView as Microsoft.UI.Xaml.DependencyObject);
        StartDropSweep();
        HookPasteAccelerator();
    }

    /// <summary>
    /// Периодически включает AllowDrop на свежих элементах окна: список
    /// сообщений и меню создают новые элементы уже после подключения.
    /// </summary>
    private static void StartDropSweep()
    {
        if (_dropSweepTimer is not null)
        {
            return;
        }

        _dropSweepTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (TryGetPlatformWindow()?.Content is Microsoft.UI.Xaml.DependencyObject root)
                {
                    MainThread.BeginInvokeOnMainThread(() => EnableDropInTree(root));
                }
            }
            catch
            {
                // Фоновый обход — ошибки не важны.
            }
        }, null, 1000, 1500);
    }

    /// <summary>Окно WinUI, в котором живёт чат.</summary>
    private static Microsoft.UI.Xaml.Window? TryGetPlatformWindow()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window platformWindow)
        {
            return platformWindow;
        }

        return HostPageForWindow()?.Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
    }

    private static Page? HostPageForWindow() =>
        Application.Current?.Windows.FirstOrDefault()?.Page;

    private static void OnWindowDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        try
        {
            var target = _activeDropTarget;
            var hasFiles = HasFiles(e) &&
                target is not null &&
                !string.IsNullOrWhiteSpace(target._username);

            if (_dragOverSeen++ < 5)
            {
                LogDrop($"dragover: sender={sender?.GetType().Name} files={hasFiles}");
            }

            e.AcceptedOperation = hasFiles
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

            if (hasFiles)
            {
                e.DragUIOverride.Caption = Loc.T("rs_115", "Отправить как вложение");
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            LogDrop($"dragover error: {ex.Message}");
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    /// <summary>В перетаскивании есть файлы (формат оболочки Windows).</summary>
    private static bool HasFiles(Microsoft.UI.Xaml.DragEventArgs e)
    {
        var view = e.DataView;
        if (view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return true;
        }

        // WinUI 3 на рабочем столе часто отдаёт приватный формат FileDrop.
        try
        {
            if (view.Contains("FileDrop"))
            {
                return true;
            }
        }
        catch
        {
            // Форматы недоступны — считаем, что файлов нет.
        }

        return false;
    }

    private static async void OnWindowDrop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        var target = _activeDropTarget;
        LogDrop($"drop: sender={sender?.GetType().Name} target={target is not null} loaded={target?.IsLoaded}");
        if (target is null || !target.IsLoaded)
        {
            return;
        }

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = new List<(string Name, string Path)>();
            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFile file && !string.IsNullOrEmpty(file.Path))
                {
                    files.Add((file.Name, file.Path));
                }
            }

            if (files.Count > 0)
            {
                await target.SendDroppedFilesAsync(files);
            }
        }
        catch (Exception ex)
        {
            target.ShowHint(string.Format(Loc.T("rs_092"), ex.Message), isError: true);
        }

        e.Handled = true;
    }

    /// <summary>Файлы из drag & drop или буфера уходят как вложения чата.</summary>
    private async Task SendDroppedFilesAsync(IReadOnlyList<(string Name, string Path)> files)
    {
        var user = _username;
        if (user is null || _sendingAttachment || !IsCurrent(user))
        {
            // Чат не открыт — подсказываем, куда бросить файлы.
            ShowHint(Loc.T("rs_112", "Откройте чат, чтобы отправить вложения."), isError: true);
            return;
        }

        var items = new List<(string FileName, byte[] Data, string ContentType, string? Kind)>();
        foreach (var (name, path) in files)
        {
            byte[] data;
            try
            {
                data = await File.ReadAllBytesAsync(path);
            }
            catch
            {
                ShowHint(string.Format(Loc.T("rs_093"), name), isError: true);
                continue;
            }

            if (data.Length == 0)
            {
                continue;
            }

            if (data.Length > MediaLimits.MaxBytes)
            {
                ShowHint(
                    string.Format(Loc.T("rs_008"), name, MediaLimits.MaxBytes / (1024 * 1024)),
                    isError: true);
                continue;
            }

            var contentType = ResolveContentType(name);
            var kind = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? null
                : MessageKinds.File;
            items.Add((name, data, contentType, kind));
        }

        await SendAttachmentsAsync(user, items);
    }

    /// <summary>Ctrl+V в поле ввода: файлы из буфера вставляются вложениями.</summary>
    private void HookPasteAccelerator()
    {
        if (_pasteHooked ||
            MessageEntry.Editable.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement input)
        {
            return;
        }

        _pasteHooked = true;

        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.V,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        accelerator.Invoked += OnPasteAcceleratorInvoked;
        input.KeyboardAccelerators.Add(accelerator);
    }

    private async void OnPasteAcceleratorInvoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                // В буфере текст или картинка-изображение — обычная вставка.
                return;
            }

            // Файлы в буфере — подавляем текстовую вставку, уходим вложениями.
            args.Handled = true;

            var items = await content.GetStorageItemsAsync();
            var files = new List<(string Name, string Path)>();
            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFile file && !string.IsNullOrEmpty(file.Path))
                {
                    files.Add((file.Name, file.Path));
                }
            }

            if (files.Count > 0)
            {
                await SendDroppedFilesAsync(files);
            }
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_080"), ex.Message), isError: true);
        }
    }
#endif

    private static async Task<byte[]?> ReadAttachmentAsync(FileResult file)
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

    private static string ResolveContentType(FileResult file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType) &&
            !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return file.ContentType;
        }

        return ResolveContentType(file.FileName);
    }

    private static string ResolveContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".heic" => "image/heic",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };

    private void OnSearchInChatClicked(object sender, EventArgs e)
    {
        MessageSearchBar.IsVisible = !MessageSearchBar.IsVisible;
        if (MessageSearchBar.IsVisible)
        {
            MessageSearchBar.Focus();
        }
        else
        {
            MessageSearchBar.Text = string.Empty;
        }
    }

    private void OnMessageSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _messageQuery = (e.NewTextValue ?? string.Empty).Trim();
        RefreshDerived();
    }

    private void OnInfoPanelClicked(object sender, EventArgs e)
    {
        if (InfoPanel.IsVisible)
        {
            InfoPanel.IsVisible = false;
            return;
        }

        ShowInfoTab(ProfileTab);
    }

    // ================================================================
    // МЕНЮ ЧАТА (⋮)
    // ================================================================

    /// <summary>Кнопка ⋮ в шапке — меню чата в стиле Telegram.</summary>
    private void OnChatMenuClicked(object sender, EventArgs e)
    {
        ShowChatMenu();
    }

    /// <summary>Меню чата: профиль, обои, экспорт, настройки, очистка и удаление.</summary>
    private void ShowChatMenu()
    {
        ContextMenuItems.Children.Clear();

        AddMenuItem(Loc.T("rs_199", "👤 Профиль, медиа и закреплённые"), () =>
        {
            OnInfoPanelClicked(this, EventArgs.Empty);
            return Task.CompletedTask;
        });

        AddMenuItem(Loc.T("rs_219", "🖼 Обои чата"), SetWallpaperAsync);

        AddMenuItem(Loc.T("rs_206", "📤 Экспорт истории чата"), () =>
        {
            OnExportChatClicked(this, EventArgs.Empty);
            return Task.CompletedTask;
        });

        AddMenuItem(Loc.T("rs_187", "⚙ Настройки"), () =>
        {
            SettingsRequested?.Invoke();
            return Task.CompletedTask;
        });

        // «Избранное» — служебный чат: его нельзя очистить или удалить.
        if (!string.Equals(_username, ChatManager.SelfChatUsername, StringComparison.OrdinalIgnoreCase))
        {
            AddMenuItem("🧽 Очистить историю", ClearChatHistoryAsync);

            AddMenuItem(Loc.T("rs_221", "🗑 Удалить чат"), DeleteChatAsync, isDanger: true);
        }

        // Панель открывается под кнопкой ⋮ в правом верхнем углу.
        var width = 248.0;
        var x = Math.Max(0, (Width <= 0 ? width + 12 : Width - width - 12));
        ContextMenuBorder.WidthRequest = width;
        ContextMenuBorder.Margin = new Thickness(x, 62, 0, 0);
        ContextMenuOverlay.IsVisible = true;
    }

    /// <summary>Обои переписки: выбор картинки из галереи или файла.</summary>
    private async Task SetWallpaperAsync()
    {
        var walls = AppServices.Get<WallpaperService>();

        var applied = DeviceInfo.Platform == DevicePlatform.Android
            ? await walls.PickFromGalleryAsync()
            : await walls.PickFromFileAsync();

        if (!applied)
        {
            // На Android галереи может не быть — пробуем выбор файла.
            applied = await walls.PickFromFileAsync();
        }

        ShowHint(applied ? Loc.T("rs_111", "Обои чата обновлены") : Loc.T("rs_099", "Не удалось установить обои"), isError: !applied);
    }

    /// <summary>Очистка истории чата: «для всех» или «только у себя».</summary>
    private async Task ClearChatHistoryAsync()
    {
        var username = _username;
        var page = HostPage;
        if (string.IsNullOrWhiteSpace(username) || page is null)
        {
            return;
        }

        var scope = await page.DisplayActionSheet(
            Loc.T("dg_042", "Очистить историю?"),
            Loc.T("common_cancel", "Отмена"),
            null,
            Loc.T("dg_074", "Удалить для всех"),
            Loc.T("dg_073", "Только у себя"));
        if (scope is null || scope == Loc.T("common_cancel", "Отмена"))
        {
            return;
        }

        var forEveryone = string.Equals(scope, "Удалить для всех", StringComparison.OrdinalIgnoreCase);

        var confirmed = await page.DisplayAlert(
            Loc.T("dg_041", "Очистить историю"),
            forEveryone
                ? string.Format(Loc.T("dg_031"), username)
                : string.Format(Loc.T("dg_030"), username),
            Loc.T("common_delete", "Удалить"), Loc.T("common_cancel", "Отмена"));
        if (!confirmed)
        {
            return;
        }

        var cleared = await _chatManager.ClearConversationAsync(username, forEveryone);
        ShowHint(cleared
            ? forEveryone ? Loc.T("rs_058", "История удалена у всех") : Loc.T("rs_057", "История удалена только у вас")
            : Loc.T("rs_089", "Не удалось очистить историю"), isError: !cleared);
    }

    /// <summary>Удаление чата: «для всех» или «только у себя».</summary>
    private async Task DeleteChatAsync()
    {
        var username = _username;
        var page = HostPage;
        if (string.IsNullOrWhiteSpace(username) || page is null)
        {
            return;
        }

        var scope = await page.DisplayActionSheet(
            Loc.T("dg_081", "Удалить чат?"),
            Loc.T("common_cancel", "Отмена"),
            null,
            Loc.T("dg_074", "Удалить для всех"),
            Loc.T("dg_075", "Удалить для себя"));
        if (scope is null || scope == Loc.T("common_cancel", "Отмена"))
        {
            return;
        }

        var forEveryone = string.Equals(scope, "Удалить для всех", StringComparison.OrdinalIgnoreCase);

        var confirmed = await page.DisplayAlert(
            Loc.T("dg_080", "Удалить чат"),
            forEveryone
                ? string.Format(Loc.T("dg_046"), username)
                : string.Format(Loc.T("dg_085"), username),
            Loc.T("common_delete", "Удалить"), Loc.T("common_cancel", "Отмена"));
        if (!confirmed)
        {
            return;
        }

        var deleted = await _chatManager.DeleteConversationAsync(username, forEveryone);
        ShowHint(deleted
            ? forEveryone ? Loc.T("rs_172", "Чат удалён у всех") : Loc.T("rs_171", "Чат удалён только у вас")
            : Loc.T("rs_098", "Не удалось удалить чат"), isError: !deleted);

        if (deleted)
        {
            CloseRequested?.Invoke();
        }
    }

    private void OnCloseInfoPanelClicked(object sender, EventArgs e) => InfoPanel.IsVisible = false;

    private void OnProfileTabClicked(object sender, EventArgs e) => ShowInfoTab(ProfileTab);

    private void OnMediaTabClicked(object sender, EventArgs e) => ShowInfoTab(MediaTab);

    private void OnPinnedTabClicked(object sender, EventArgs e) => ShowInfoTab(PinnedTab);

    private void ShowInfoTab(string tab)
    {
        InfoPanel.IsVisible = true;
        ProfileSection.IsVisible = tab == ProfileTab;
        MediaSection.IsVisible = tab == MediaTab;
        PinnedSection.IsVisible = tab == PinnedTab;
        InfoPanelTitle.Text = tab switch
        {
            ProfileTab => "Профиль",
            MediaTab => "Медиа и файлы",
            _ => "Закреплённые"
        };
    }

    private void OnSettingsClicked(object sender, EventArgs e) => SettingsRequested?.Invoke();

    private void OnCloseClicked(object sender, EventArgs e) => CloseRequested?.Invoke();

    private void OnBubbleTapped(object? sender, TappedEventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            SelectMessage(message);
        }
    }

    private void OnBubblePointerPressed(object? sender, PointerEventArgs e)
    {
        if (GetMessage(sender) is not { } message)
        {
            return;
        }

        _menuPosition = e.GetPosition(this) ?? default;

#if WINDOWS
        object? platformArgs = e.PlatformArgs;
        if (platformArgs is Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args &&
            args.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            ShowContextMenu(message);
        }
#else
        // Длинное нажатие открывает контекстное меню.
        _longPressCts = new CancellationTokenSource();
        _ = ShowContextMenuAfterLongPress(message, _longPressCts.Token);
#endif
    }

    private void SelectMessage(MessageVm message)
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = ReferenceEquals(_selected, message) ? null : message;
        if (_selected is not null)
        {
            _selected.IsSelected = true;
        }
    }

    private async void OnActionCopyClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            await CopyMessageAsync(message);
        }
    }

    private void OnActionReplyClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            ReplyTo(message);
        }
    }

    private async void OnActionReactionClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            await PickReactionAsync(message);
        }
    }

    private async void OnActionForwardClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            await ForwardAsync(message);
        }
    }

    private async void OnActionPinClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            await TogglePinAsync(message);
        }
    }

    private void OnActionEditClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            StartEdit(message);
        }
    }

    private async void OnActionDeleteClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            await DeleteAsync(message);
        }
    }

    // Чипы скрытых markdown-целей и карточки превью: три фиксированных слота.
    // Контекст у этих элементов — сам MessageVm, индекс слота известен из имени обработчика.

    private void OnChip0Tapped(object? sender, EventArgs e) => OpenSlotHref(sender, 0, preview: false);
    private void OnChip1Tapped(object? sender, EventArgs e) => OpenSlotHref(sender, 1, preview: false);
    private void OnChip2Tapped(object? sender, EventArgs e) => OpenSlotHref(sender, 2, preview: false);
    private void OnPreview0Tapped(object? sender, EventArgs e) => OpenSlotHref(sender, 0, preview: true);
    private void OnPreview1Tapped(object? sender, EventArgs e) => OpenSlotHref(sender, 1, preview: true);
    private void OnPreview2Tapped(object? sender, EventArgs e) => OpenSlotHref(sender, 2, preview: true);

    private void OpenSlotHref(object? sender, int slot, bool preview)
    {
        if (sender is not BindableObject { BindingContext: MessageVm vm })
        {
            return;
        }

        var url = preview ? vm.HrefForPreview(slot) : vm.HrefForChip(slot);
        if (!string.IsNullOrEmpty(url))
        {
            _ = OpenUrlAsync(url);
        }
    }

    /// <summary>Открывает ссылку: почту и телефон — в системных приложениях,
    /// веб — во встроенном браузере, при неудаче — в системном.</summary>
    private async Task OpenUrlAsync(string url)
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
                ShowHint(string.Format(Loc.T("uc_035"), ex.Message), isError: true);
            }

            return;
        }

        var page = HostPage;
        if (page is null)
        {
            return;
        }

        try
        {
            await page.Navigation.PushAsync(new BrowserPage(url));
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
                ShowHint(string.Format(Loc.T("uc_035"), ex.Message), isError: true);
            }
        }
    }

    private async Task CopyUrlsAsync(MessageVm message)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(string.Join("\n", message.Urls));
            ShowHint(Loc.T("fx_006"), isError: false);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_095"), ex.Message), isError: true);
        }
    }

    private async Task CopyMessageLinkAsync(MessageVm message)
    {
        try
        {
            var link = DeepLink.BuildMessageLink("11", _username!, message.Id);
            await Clipboard.Default.SetTextAsync(link);
            ShowHint(Loc.T("rs_152", "Ссылка на сообщение скопирована."), isError: false);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_094"), ex.Message), isError: true);
        }
    }

    private async Task CopyMessageAsync(MessageVm message)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(
                message.Kind is MessageKinds.Text or MessageKinds.Sticker ? message.Text : message.FileName);
            ShowHint(Loc.T("rs_146", "Скопировано."), isError: false);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_095"), ex.Message), isError: true);
        }
    }

    private void ReplyTo(MessageVm message)
    {
        _replyingTo = message;
        _editingMessageId = null;
        EditBar.IsVisible = false;

        // Цитата ответа — это текст отвечаемого сообщения, а не его собственная цитата.
        var preview = string.IsNullOrWhiteSpace(message.Text)
            ? message.FileName
            : message.Text;
        var author = message.ReplyAuthorName;
        var who = string.IsNullOrWhiteSpace(author)
            ? string.Empty
            : string.Equals(author, _session.Username, StringComparison.OrdinalIgnoreCase)
                ? " на ваше сообщение"
                : $" на сообщение {author}";
        ReplyPreviewLabel.Text = string.Format(Loc.T("uc_072"), who, Shorten(preview, 70));
        ReplyBar.IsVisible = true;
        MessageEntry.FocusInput();
    }

    private void OnCancelReplyClicked(object sender, EventArgs e)
    {
        _replyingTo = null;
        ReplyBar.IsVisible = false;
        MessageEntry.FocusInput();
    }

    private void OnCancelEditClicked(object sender, EventArgs e)
    {
        _editingMessageId = null;
        EditBar.IsVisible = false;
        MessageEntry.Text = string.Empty;
        MessageEntry.FocusInput();
    }

    private async Task ShowContextMenuAfterLongPress(MessageVm message, CancellationToken token)
    {
        try
        {
            await Task.Delay(600, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => ShowContextMenu(message));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartEdit(MessageVm message)
    {
        if (message.Kind != MessageKinds.Text)
        {
            ShowHint(Loc.T("rs_141", "Редактирование доступно только для текстовых сообщений."), isError: true);
            return;
        }

        _editingMessageId = message.Id;
        _replyingTo = null;
        ReplyBar.IsVisible = false;
        EditPreviewLabel.Text = string.Format(Loc.T("uc_075"), message.TimeText);
        EditBar.IsVisible = true;
        MessageEntry.Text = message.Text;
        MessageEntry.FocusInput();
    }

    private void OnBubblePointerReleased(object? sender, PointerEventArgs e)
    {
        _longPressCts?.Cancel();
        _longPressCts = null;
    }

    private void OnBubblePointerExited(object? sender, PointerEventArgs e)
    {
        _longPressCts?.Cancel();
        _longPressCts = null;
    }

    private async Task PickReactionAsync(MessageVm message)
    {
        var page = HostPage;
        if (page is null || _username is null)
        {
            return;
        }

        var choice = await page.DisplayActionSheet(Loc.T("uc_063", "Реакция:"), Loc.T("common_cancel", "Отмена"), null, ReactionEmojis);
        if (string.IsNullOrEmpty(choice) || choice == Loc.T("common_cancel", "Отмена"))
        {
            return;
        }

        try
        {
            await _chatManager.ToggleReactionAsync(message.Id, choice);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_119"), ex.Message), isError: true);
        }
    }

    private async Task ForwardAsync(MessageVm message)
    {
        var page = HostPage;
        if (page is null || _username is null)
        {
            return;
        }

        var targets = (await _chatManager.GetConversationsAsync())
            .Where(item => !string.Equals(item.Username, _username, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (targets.Count == 0)
        {
            ShowHint(Loc.T("rs_101", "Нет других чатов для пересылки."), isError: false);
            return;
        }

        var choice = await page.DisplayActionSheet(
            Loc.T("dg_047", "Переслать в:"),
            Loc.T("common_cancel", "Отмена"),
            null,
            targets.Select(item => item.Username).ToArray());
        var target = targets.FirstOrDefault(item => string.Equals(item.Username, choice, StringComparison.Ordinal));
        if (target is null)
        {
            return;
        }

        try
        {
            // Метка «Переслано от …» — автор исходного сообщения. Если сообщение
            // уже пересланное, сохраняем первоначального автора, а не ретранслятора.
            var origin = !string.IsNullOrWhiteSpace(message.ForwardedFrom)
                ? message.ForwardedFrom
                : !string.IsNullOrWhiteSpace(message.SenderDisplay)
                    ? message.SenderDisplay
                    : _username;

            if (message.Kind is MessageKinds.Text or MessageKinds.Sticker)
            {
                await _chatManager.SendMessageToUserAsync(
                    target.UserId,
                    target.Username,
                    message.Text,
                    target.EncryptionPublicKey,
                    isForwarded: true,
                    kind: message.Kind,
                    forwardedFrom: origin);
            }
            else
            {
                var bytes = await _chatManager.GetLocalMediaAsync(message.Id);
                if (bytes is null)
                {
                    ShowHint(Loc.T("rs_022", "Вложение ещё не загружено на устройство."), isError: true);
                    return;
                }

                await _chatManager.SendMediaAsync(
                    target.Username,
                    message.FileName,
                    message.ContentType,
                    bytes,
                    isForwarded: true,
                    messageKind: message.Kind,
                    forwardedFrom: origin);
            }

            ShowHint(string.Format(Loc.T("rs_125"), target.Username), isError: false);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_090"), ex.Message), isError: true);
        }
    }

    private async Task TogglePinAsync(MessageVm message)
    {
        try
        {
            await _chatManager.TogglePinnedAsync(message.Id, !message.IsPinned);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_082"), ex.Message), isError: true);
        }
    }

    private async Task DeleteAsync(MessageVm message)
    {
        var page = HostPage;
        if (page is null)
        {
            return;
        }

        if (!message.IsMine)
        {
            // Не своё сообщение — можно только скрыть у себя.
            var confirmed = await page.DisplayAlert(
                Loc.T("dg_078", "Удалить сообщение?"),
                Loc.T("dg_062", "Сообщение будет скрыто только у вас. Собеседник увидит его как раньше."),
                Loc.T("dg_060", "Скрыть у себя"),
                Loc.T("common_cancel", "Отмена"));
            if (!confirmed)
            {
                return;
            }

            try
            {
                await _chatManager.DeleteMessageAsync(message.Id, deleteForEveryone: false);
            }
            catch (Exception ex)
            {
                ShowHint(string.Format(Loc.T("rs_096"), ex.Message), isError: true);
            }
            return;
        }

        // Своё сообщение — можно удалить для себя или для всех.
        var choice = await page.DisplayActionSheet(
            Loc.T("dg_078", "Удалить сообщение?"),
            Loc.T("common_cancel", "Отмена"),
            null,
            Loc.T("dg_074", "Удалить для всех"),
            Loc.T("dg_075", "Удалить для себя"));

        if (choice == Loc.T("common_cancel", "Отмена") || choice is null)
        {
            return;
        }

        var deleteForEveryone = string.Equals(choice, "Удалить для всех", StringComparison.OrdinalIgnoreCase);

        try
        {
            await _chatManager.DeleteMessageAsync(message.Id, deleteForEveryone: deleteForEveryone);
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_097"), ex.Message), isError: true);
        }
    }

    private void OnMessageDeleted(string messageId)
    {
        var message = _messages.FirstOrDefault(m => m.Id == messageId);
        if (message is not null)
        {
            _messages.Remove(message);
            if (ReferenceEquals(_selected, message))
            {
                _selected = null;
            }
            RefreshDerived();
        }
    }

    private void OnConversationDeleted(string username)
    {
        // Наш чат удалён собеседником — если это текущий чат, закрываем его.
        if (_username is null || !string.Equals(_username, username, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // В Windows-клиенте чат живёт в сайдбаре — закрываем его там,
        // на Android убираем экран переписки из стека навигации.
        if (HostPage is DesktopMainPage desktop)
        {
            desktop.CloseChatFromPeer();
            return;
        }

        HostPage?.Navigation.PopAsync();
    }

    private void OnConversationCleared(string username)
    {
        // Собеседник очистил всю историю — если это текущий чат, очищаем ленту.
        if (_username is not null && string.Equals(_username, username, StringComparison.OrdinalIgnoreCase))
        {
            _messages.Clear();
            RefreshDerived();
        }
    }

    private async void OnOpenImageClicked(object sender, EventArgs e)
    {
        var message = GetMessage(sender);
        var page = HostPage;
        if (message is null || page is null)
        {
            return;
        }

        try
        {
            var bytes = await _chatManager.GetLocalMediaAsync(message.Id);
            if (bytes is null && !string.IsNullOrEmpty(_username))
            {
                if (!_chatManager.IsOnline)
                {
                    ShowHint(Loc.T("rs_105", "Нет подключения: фото загрузится, когда связь восстановится."), isError: true);
                    return;
                }

                await _chatManager.EnsureMediaAsync(_username, message.Id, manual: true);
                bytes = await _chatManager.GetLocalMediaAsync(message.Id);
            }

            if (bytes is null)
            {
                ShowHint(Loc.T("rs_056", "Изображение ещё не загружено."), isError: true);
                return;
            }

            await page.Navigation.PushModalAsync(
                new NavigationPage(new ImageViewerPage(bytes, message.FileName)));

            // Одноразовое сообщение исчезает после закрытия просмотрщика.
            if (message.IsOnce && !message.IsMine)
            {
                await _chatManager.ConsumeViewOnceAsync(message.Id);
            }
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_086"), ex.Message), isError: true);
        }
    }

    private async void OnMediaActionClicked(object sender, EventArgs e)
    {
        var message = GetMessage(sender);
        var user = _username;
        if (message is null || user is null)
        {
            return;
        }

        // Голосовые и видеокружки имеют свои инлайн-обработчики, не открываем в MediaPlayer
        if (message.IsVoice || message.IsVideoCircle)
        {
            return;
        }

        try
        {
            if (!message.MediaDownloaded)
            {
                if (!_chatManager.IsOnline)
                {
                    ShowHint(Loc.T("rs_103", "Нет подключения: вложение загрузится, когда связь восстановится."), isError: true);
                    return;
                }

                await _chatManager.EnsureMediaAsync(user, message.Id, manual: true);
            }

            var bytes = await _chatManager.GetLocalMediaAsync(message.Id);
            var page = HostPage;
            if (bytes is null || page is null)
            {
                ShowHint(Loc.T("uc_008", "Вложение недоступно на этом устройстве."), isError: true);
                return;
            }

            var path = Path.Combine(FileSystem.CacheDirectory, message.Id + Path.GetExtension(message.FileName));
            await File.WriteAllBytesAsync(path, bytes);

            // Видео и аудио открываем во встроенном плеере, остальное — системным приложением.
            if (IsPlayableMedia(message.ContentType, message.FileName))
            {
                await page.Navigation.PushModalAsync(
                    new NavigationPage(new MediaPlayerPage(path, message.FileName, message.ContentType)));
                if (message.IsOnce && !message.IsMine)
                {
                    await _chatManager.ConsumeViewOnceAsync(message.Id);
                }
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest(message.FileName, new ReadOnlyFile(path)));

            if (message.IsOnce && !message.IsMine)
            {
                await _chatManager.ConsumeViewOnceAsync(message.Id);
            }
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_084"), ex.Message), isError: true);
        }
    }

    private async void OnPlayVoiceClicked(object sender, EventArgs e)
    {
#if ANDROID || WINDOWS
        if (GetMessage(sender) is not { } message || string.IsNullOrEmpty(_username))
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

        // Останавливаем текущее воспроизведение (если играло другое сообщение).
        StopVoicePlayback();

        try
        {
            if (!message.MediaDownloaded)
            {
                if (!_chatManager.IsOnline)
                {
                    ShowHint(Loc.T("rs_104", "Нет подключения: голосовое загрузится, когда связь восстановится."), isError: true);
                    return;
                }

                await _chatManager.EnsureMediaAsync(_username, message.Id, manual: true);
            }

            var bytes = await _chatManager.GetLocalMediaAsync(message.Id);
            if (bytes is null)
            {
                ShowHint(Loc.T("rs_037", "Голосовое недоступно на этом устройстве."), isError: true);
                return;
            }

            var path = Path.Combine(FileSystem.CacheDirectory, message.Id + ".m4a");
            await File.WriteAllBytesAsync(path, bytes);

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
            ShowHint(string.Format(Loc.T("uc_023"), ex.Message), isError: true);
        }
#else
        await Task.CompletedTask;
        ShowHint(Loc.T("uc_009", "Воспроизведение голосовых на этой платформе недоступно."), isError: true);
#endif
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

#if ANDROID || WINDOWS
    private void StartVoiceProgressTimer(MessageVm message)
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
            view.BindingContext is not MessageVm message ||
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

        // Перемотка по координате касания относительно ширины аудиодорожки.
        var ratio = Math.Clamp(point.Value.X / view.Width, 0, 1);
        var position = ratio * duration;
        _voicePlayer.SeekSeconds(position);
        message.UpdatePlayback(position, duration);
#endif
    }

    private async void OnPlayVideoCircleClicked(object sender, EventArgs e)
    {
        if (GetMessage(sender) is not { } message || string.IsNullOrEmpty(_username))
        {
            return;
        }

        try
        {
            if (!message.MediaDownloaded)
            {
                if (!_chatManager.IsOnline)
                {
                    ShowHint(Loc.T("rs_102", "Нет подключения: видеокружок загрузится, когда связь восстановится."), isError: true);
                    return;
                }

                await _chatManager.EnsureMediaAsync(_username, message.Id, manual: true);
            }

            var bytes = await _chatManager.GetLocalMediaAsync(message.Id);
            var page = HostPage;
            if (bytes is null || page is null)
            {
                ShowHint(Loc.T("rs_020", "Видеокружок недоступен на этом устройстве."), isError: true);
                return;
            }

            var path = Path.Combine(FileSystem.CacheDirectory, message.Id + ".mp4");
            await File.WriteAllBytesAsync(path, bytes);

            await page.Navigation.PushModalAsync(
                new NavigationPage(new MediaPlayerPage(path, message.FileName, message.ContentType, isVideoCircle: true)));

            if (message.IsOnce && !message.IsMine)
            {
                await _chatManager.ConsumeViewOnceAsync(message.Id);
            }
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("rs_083"), ex.Message), isError: true);
        }
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

        // Сбрасываем состояние воспроизведения на сообщении
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

    private static bool IsPlayableMedia(string contentType, string fileName)
    {
        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() is
            ".mp4" or ".mov" or ".webm" or ".mkv" or ".mp3" or ".m4a" or ".wav" or ".ogg";
    }

    private void OnMediaItemTapped(object sender, TappedEventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            ScrollToMessage(message);
        }
    }

    private void OnPinnedItemTapped(object sender, TappedEventArgs e)
    {
        if (GetMessage(sender) is { } message)
        {
            ScrollToMessage(message);
        }
    }

    private void OnPinnedBannerTapped(object sender, TappedEventArgs e)
    {
        var pinned = GetPinned();
        if (pinned.Count > 0)
        {
            ScrollToMessage(pinned[Math.Clamp(_pinnedIndex, 0, pinned.Count - 1)]);
        }
    }

    private void OnPinnedUpClicked(object sender, EventArgs e) => MovePinned(-1);

    private void OnPinnedDownClicked(object sender, EventArgs e) => MovePinned(1);

    private void MovePinned(int delta)
    {
        var pinned = GetPinned();
        if (pinned.Count == 0)
        {
            return;
        }

        _pinnedIndex = Math.Clamp(_pinnedIndex + delta, 0, pinned.Count - 1);
        RefreshSidePanels();
        ScrollToMessage(pinned[_pinnedIndex]);
    }

    private async void OnUnpinBannerClicked(object sender, EventArgs e)
    {
        var pinned = GetPinned();
        if (pinned.Count == 0)
        {
            return;
        }

        await TogglePinAsync(pinned[Math.Clamp(_pinnedIndex, 0, pinned.Count - 1)]);
    }

    private void OnContextMenuDismissed(object sender, TappedEventArgs e) => HideContextMenu();

    private void ShowContextMenu(MessageVm message)
    {
        ContextMenuItems.Children.Clear();
        AddMenuItem(Loc.T("rs_201", "📋 Копировать"), () => CopyMessageAsync(message));
        AddMenuItem(Loc.T("rs_185", "↩ Ответить"), () => { ReplyTo(message); return Task.CompletedTask; });
        AddMenuItem(Loc.T("rs_223", "😊 Реакция"), () => PickReactionAsync(message));
        AddMenuItem(Loc.T("rs_210", "🔄 Переслать"), () => ForwardAsync(message));
        AddMenuItem(message.IsPinned ? Loc.T("rs_204", "📌 Открепить") : Loc.T("rs_203", "📌 Закрепить"), () => TogglePinAsync(message));
        if (message.IsMine)
        {
            if (message.Kind == MessageKinds.Text)
            {
                AddMenuItem(Loc.T("rs_192", "✏️ Редактировать"), () => { StartEdit(message); return Task.CompletedTask; });
            }

            AddMenuItem(Loc.T("rs_222", "🗑️ Удалить"), () => DeleteAsync(message));
        }
        if (message.HasUrls)
        {
            foreach (var url in message.Urls.Take(3))
            {
                var u = url;
                AddMenuItem($"🔗 {Shorten(u, 38)}", () => OpenUrlAsync(u));
            }
            AddMenuItem(Loc.T("rs_202", "📋 Копировать ссылку"), () => CopyUrlsAsync(message));
        }
        AddMenuItem(Loc.T("rs_218", "🔗 Ссылка на сообщение"), () => CopyMessageLinkAsync(message));
        if (message.Kind is not MessageKinds.Text and not MessageKinds.Sticker)
        {
            AddMenuItem(Loc.T("rs_200", "💾 Сохранить на устройство"), () => SaveMessageMediaAsync(message));
        }
        if (!string.Equals(_username, ChatManager.SelfChatUsername, StringComparison.OrdinalIgnoreCase))
        {
            AddMenuItem(message.IsArchived ? Loc.T("rs_205", "📤 Разархивировать") : Loc.T("rs_207", "📦 Архивировать"),
                () => ToggleArchiveAsync(message));
        }
        AddMenuItem(Loc.T("rs_184", "ℹ️ Информация"), () => ShowMessageInfo(message));

        ClampContextMenuPosition();
        ContextMenuOverlay.IsVisible = true;
    }

    /// <summary>Сохраняет вложение сообщения в галерею или в папку на устройстве.</summary>
    private async Task SaveMessageMediaAsync(MessageVm message)
    {
        var username = _username;
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        try
        {
            var bytes = await _chatManager.GetLocalMediaAsync(message.Id);
            if (bytes is null)
            {
                if (!_chatManager.IsOnline)
                {
                    ShowHint(Loc.T("rs_103", "Нет подключения: вложение загрузится, когда связь восстановится."), isError: true);
                    return;
                }

                await _chatManager.EnsureMediaAsync(username, message.Id, manual: true);
                bytes = await _chatManager.GetLocalMediaAsync(message.Id);
            }

            if (bytes is null)
            {
                ShowHint(Loc.T("uc_008", "Вложение недоступно на этом устройстве."), isError: true);
                return;
            }

            var fileName = string.IsNullOrWhiteSpace(message.FileName) ? "file" : message.FileName;
            var result = await AppServices.Get<MediaSaveService>()
                .SaveAsync(fileName, message.ContentType, bytes);

            if (string.IsNullOrEmpty(result))
            {
                return;
            }

            ShowHint(
                result.StartsWith(Loc.T("uc_022", "Не удалось"), StringComparison.Ordinal)
                    ? result
                    : string.Format(Loc.T("rs_151"), result),
                isError: result.StartsWith(Loc.T("uc_022", "Не удалось"), StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("uc_045"), ex.Message), isError: true);
        }
    }

    private async Task ToggleArchiveAsync(MessageVm message)    {
        if (_username is null)
        {
            return;
        }

        if (string.Equals(_username, ChatManager.SelfChatUsername, StringComparison.OrdinalIgnoreCase))
        {
            HideContextMenu();
            if (HostPage is not null)
            {
                await HostPage.DisplayAlert("Избранное",
                    Loc.T("dg_089", "Это служебный чат для сохранения сообщений. Его нельзя заархивировать."), "OK");
            }
            return;
        }

        await _chatManager.ArchiveConversationAsync(_username, !message.IsArchived);
        HideContextMenu();
    }

    private void ClampContextMenuPosition()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var x = Math.Clamp(_menuPosition.X, 0, Math.Max(0, Width - 260));
        var y = Math.Clamp(_menuPosition.Y, 0, Math.Max(0, Height - 260));
        ContextMenuBorder.Margin = new Thickness(x, y, 0, 0);
    }

    private async Task ShowMessageInfo(MessageVm message)
    {
        HideContextMenu();
        var page = HostPage;
        if (page is null)
        {
            return;
        }

        var kindLabel = message.Kind switch
        {
            MessageKinds.Image => "Изображение",
            MessageKinds.Voice => "Голосовое сообщение",
            MessageKinds.VideoCircle => "Видеокружок",
            MessageKinds.File => "Файл",
            MessageKinds.Sticker => "Стикер",
            _ => "Текст"
        };

        var text = new StringBuilder();
        text.AppendLine(string.Format(Loc.T("rs_160"), kindLabel));
        text.AppendLine(string.Format(Loc.T("rs_113"), message.SenderDisplay));
        text.AppendLine(string.Format(Loc.T("rs_026"), message.TimeText, message.LocalDate));
        text.AppendLine(string.Format(Loc.T("rs_153"), message.DeliveryStatus));
        if (!string.IsNullOrEmpty(message.FileName))
        {
            text.AppendLine(string.Format(Loc.T("rs_164"), message.FileName, message.FileSizeText));
        }
        if (message.IsForwarded)
        {
            text.AppendLine(string.IsNullOrWhiteSpace(message.ForwardedFrom)
                ? Loc.T("rs_127", "Переслано: да")
                : string.Format(Loc.T("rs_126"), message.ForwardedFrom));
        }
        if (message.IsEdited)
        {
            text.AppendLine(Loc.T("rs_117", "Отредактировано: да"));
        }
        text.AppendLine(string.Format(Loc.T("rs_052"), message.Id));

        await page.DisplayAlert(Loc.T("uc_014", "Информация о сообщении"), text.ToString(), "OK");
    }

    private void AddMenuItem(string text, Func<Task> action, bool isDanger = false)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = Colors.Transparent,
            TextColor = isDanger ? Res("Danger") : Res("TextPrimary"),
            HorizontalOptions = LayoutOptions.Fill
        };
        button.Clicked += async (_, _) =>
        {
            HideContextMenu();
            await action();
        };
        ContextMenuItems.Children.Add(button);
    }

    private void HideContextMenu()
    {
        ContextMenuOverlay.IsVisible = false;
        ContextMenuItems.Children.Clear();
    }

    private static MessageVm? GetMessage(object? sender) => sender switch
    {
        Button { CommandParameter: MessageVm button } => button,
        BindableObject { BindingContext: MediaListItemVm media } => media.Message,
        BindableObject { BindingContext: MessageVm item } => item,
        _ => null
    };

    private void ScrollToMessage(MessageVm message) =>
        MessagesView.ScrollTo(message, position: ScrollToPosition.Center, animate: true);

    /// <summary>Превью для первых ссылок сообщения: загрузки идут последовательно,
    /// чтобы не создавать шквал запросов.</summary>
    private async Task EnsurePreviewAsync(MessageVm message)
    {
        foreach (var url in message.Urls.Take(MessageVm.MaxPreviews))
        {
            if (message.HasPreviewFor(url))
            {
                continue;
            }

            var preview = await _previewService.GetPreviewAsync(url);
            if (preview is not null)
            {
                MainThread.BeginInvokeOnMainThread(() => message.AddPreview(preview));
            }
        }
    }

    /// <summary>Превью ссылок в уже открытой истории: начиная с последних сообщений.</summary>
    private async Task EnsurePreviewsForLoadedAsync()
    {
        foreach (var vm in _messages.Where(m => m.ShowText && m.HasUrls).Reverse().Take(30).ToList())
        {
            await EnsurePreviewAsync(vm);
        }
    }

    private void ScrollToBottom()
    {
        if (_messages.Count > 0)
        {
            MessagesView.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: false);
        }
    }

    private void ShowHint(string text, bool isError)
    {
        HintLabel.Text = text;
        HintLabel.TextColor = Res(isError ? "Danger" : "TextMuted");
        HintLabel.IsVisible = true;

        _hintCts?.Cancel();
        _hintCts = new CancellationTokenSource();
        _ = HideHintLaterAsync(isError ? 6000 : 4000, _hintCts.Token);
    }

    private void HideHint()
    {
        _hintCts?.Cancel();
        HintLabel.IsVisible = false;
    }

    private async Task HideHintLaterAsync(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
            MainThread.BeginInvokeOnMainThread(() => HintLabel.IsVisible = false);
        }
        catch (OperationCanceledException)
        {
            // Новая подсказка заменила предыдущую.
        }
    }

    private static Color Res(string key) => (Color)Application.Current!.Resources[key];

    private static ImageSource? AvatarSource(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch (FormatException)
        {
            return null;
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

        return date.ToString("d MMMM yyyy", RuCulture);
    }

    private static string Shorten(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private async Task<(byte[] Data, string ContentType)?> RecordVoiceMessageAsync(Page page)
    {
#if WINDOWS
        if (!await WindowsRecording.EnsureMicrophoneAllowedAsync(page))
        {
            return null;
        }
#else
        // Запрашиваем право на микрофон перед записью.
        try
        {
            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                ShowHint(Loc.T("uc_053", "Нет права на запись аудио. Разрешите доступ к микрофону в настройках."), isError: true);
                return null;
            }
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("uc_031"), ex.Message), isError: true);
            return null;
        }
#endif

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
            Color = Res("Danger"),
            VerticalOptions = LayoutOptions.Center
        };

        var sendButton = new Button
        {
            Text = Loc.T("rs_114", "Отправить"),
            BackgroundColor = Res("Accent"),
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
            BackgroundColor = Res("Surface"),
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
            ShowHint(string.Format(Loc.T("uc_057"), ex.Message), isError: true);
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
            ShowHint(string.Format(Loc.T("uc_040"), ex.Message), isError: true);
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

    private async Task<(byte[] Data, string ContentType)?> RecordVideoCircleAsync(Page page)
    {
#if WINDOWS || ANDROID
#if WINDOWS
        if (!await WindowsRecording.EnsureCameraReadyAsync(page))
        {
            return null;
        }
#endif
        try
        {
            var status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                ShowHint(Loc.T("uc_054", "Нет права на камеру. Разрешите доступ к камере в настройках."), isError: true);
                return null;
            }
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("uc_030"), ex.Message), isError: true);
            return null;
        }

        try
        {
            // Открываем системную камеру: у неё есть предпросмотр, запись,
            // остановка и подтверждение — пользователь видит, что записывает.
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
                ShowHint(Loc.T("uc_016", "Камера вернула пустой видеофайл."), isError: true);
                return null;
            }

            if (buffer.Length > MediaLimits.MaxBytes)
            {
                ShowHint(string.Format(Loc.T("uc_006"), MediaLimits.MaxBytes / (1024 * 1024)), isError: true);
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
            ShowHint(Loc.T("uc_017", "На устройстве нет приложения камеры, поддерживающего запись видео."), isError: true);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(Loc.T("uc_029"), ex.Message), isError: true);
            return null;
        }
#else
        await Task.CompletedTask;
        ShowHint(Loc.T("uc_011", "Запись видеокружков на этой платформе недоступна."), isError: true);
        return null;
#endif
    }

    /// <summary>Подтверждение смены ключа после сверки отпечатка с собеседником.</summary>
    private async void OnKeyWarningTapped(object? sender, EventArgs e)
    {
        if (_conversation is null)
        {
            return;
        }

        var page = HostPage;
        if (page is null)
        {
            return;
        }

        var confirmed = await page.DisplayAlert(
            Loc.T("bug_key_title", "Проверка ключа"),
            string.Format(Loc.T("bug_key_msg", "Отпечаток ключа {0}\n\nСверьте его с собеседником по другому каналу. Если совпадает — подтвердите."),
                _conversation.Fingerprint),
            Loc.T("bug_key_yes", "Подтверждён"),
            Loc.T("bug_key_no", "Отмена"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _chatManager.AcceptNewKeysAsync(_conversation.Username);
            KeyWarningLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            ShowHint(string.Format(
                Loc.T("bug_key_failed", "Не удалось подтвердить ключ: {0}"), ex.Message),
                isError: true);
        }
    }


    private void OnMessageHashtagTapped(object sender, string tag)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            _ = Navigation.PushAsync(new GlobalSearchPage("#" + tag));
        }
    }

    private async void OnCreatePollClicked(object? sender, EventArgs e)
    {
        var user = _username;
        var page = HostPage;
        if (user is null || page is null) return;

        var createPage = new PollCreatePage();
        createPage.PollCreated += async (_, poll) =>
        {
            var json = JsonSerializer.Serialize(poll);
            await _chatManager.SendMessageAsync(user, json, kind: MessageKinds.Poll);
        };
        await page.Navigation.PushAsync(createPage);
    }

    private void ApplyPollVotes()
    {
        var polls = _messages.Where(m => m.Poll is not null)
            .ToDictionary(m => m.Poll!.Id, m => m.Poll!);
        foreach (var vote in _allPollVotes)
        {
            try
            {
                var data = JsonSerializer.Deserialize<PollVoteData>(vote.Text);
                if (data is null || !polls.TryGetValue(data.PollId, out var poll)) continue;
                var own = AppServices.Get<SecureChat.App.Services.AppSession>().User?.Username;
                var voter = vote.IsMine ? own ?? "me" : vote.SenderDisplay;
                poll.ApplyVote(data.OptionIndex, voter, vote.IsMine);
            }
            catch (JsonException) { }
        }
    }

    private async void OnPollVoteRequested(MessageVm message, int optionIndex)
    {
        var user = _username;
        var poll = message.Poll;
        if (user is null || poll is null) return;

        var data = new PollVoteData { PollId = poll.Id, OptionIndex = optionIndex };
        var ownName = AppServices.Get<SecureChat.App.Services.AppSession>().User?.Username ?? "me";
        poll.ApplyVote(optionIndex, ownName, mine: true);
        try
        {
            await _chatManager.SendMessageAsync(user, JsonSerializer.Serialize(data), kind: MessageKinds.PollVote);
        }
        catch (Exception ex)
        {
            ShowHint(ex.Message, isError: true);
        }
    }

    private async void OnQuickRepliesClicked(object? sender, EventArgs e)
    {
        var page = HostPage;
        if (page is null) return;

        var items = await AppServices.Get<QuickRepliesService>().GetAllAsync();
        var options = items.Count == 0
            ? new[] { "Управление быстрыми ответами" }
            : items.Concat(new[] { "Управление быстрыми ответами" }).ToArray();
        var choice = await page.DisplayActionSheet("Быстрый ответ", "Отмена", null, options);
        if (string.IsNullOrEmpty(choice) || choice == "Отмена") return;

        if (choice == "Управление быстрыми ответами")
        {
            await page.Navigation.PushAsync(new QuickRepliesPage());
            return;
        }

        var text = MessageEntry.Text ?? string.Empty;
        MessageEntry.Text = string.IsNullOrEmpty(text) ? choice : text + " " + choice;
        MessageEntry.CursorPosition = MessageEntry.Text.Length;
        MessageEntry.Focus();
    }

    private async void OnStoriesClicked(object? sender, EventArgs e)
    {
        var page = HostPage;
        if (page is null) return;
        await page.Navigation.PushAsync(new StoriesPage());
    }

    private async void OnTtlClicked(object? sender, EventArgs e)
    {
        var page = HostPage;
        if (page is null) return;
        var choice = await page.DisplayActionSheet(
            "Автоудаление сообщения", "Отмена", "Без автоудаления",
            "Через 1 минуту", "Через 1 час", "Через 1 день");
        _ttlNext = choice switch
        {
            "Через 1 минуту" => DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            "Через 1 час" => DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            "Через 1 день" => DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(),
            _ => null
        };
        UpdateActionsButton();
    }

    private void UpdateActionsButton()
    {
        if (ActionsButton is not null)
            ActionsButton.Text = _ttlNext.HasValue ? "⏲" : "⚡";
    }

    private async void OnActionsClicked(object? sender, EventArgs e)
    {
        var page = HostPage;
        if (page is null) return;
        var choice = await page.DisplayActionSheet(
            "Действия", "Отмена", null,
            "Опрос", "Быстрые ответы", "Автоудаление сообщения");
        switch (choice)
        {
            case "Опрос":
                OnCreatePollClicked(sender, e);
                break;
            case "Быстрые ответы":
                OnQuickRepliesClicked(sender, e);
                break;
            case "Автоудаление сообщения":
                OnTtlClicked(sender, e);
                break;
        }
    }
}

