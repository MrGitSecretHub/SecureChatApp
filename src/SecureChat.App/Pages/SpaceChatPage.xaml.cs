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
    private readonly IAudioRecorder _audioRecorder = AppServices.Get<IAudioRecorder>();
    private readonly ObservableCollection<SpaceMessageVm> _messages = new();
    private readonly string _spaceId;
    private readonly string _spaceType;
    private string? _topicId;
    private string? _topicTitle;
    private SpaceMessageVm? _pinnedMessage;
    private string? _myUserId;
    private bool _canModerate;
    private bool _canPost = true;
    private SpaceMessageVm? _replyingTo;
    private readonly ObservableCollection<SpaceMessageVm> _pinnedItems = new();
    private int _currentPinnedIndex = -1;

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
        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopVoicePlayback();
        _spaces.SpaceMessageReceived -= OnSpaceMessageReceived;
        _spaces.SpaceMessageUpdated -= OnSpaceMessageUpdated;
        _spaces.SpaceMessageDeleted -= OnSpaceMessageDeleted;
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
            var messages = await _spaces.GetMessagesAsync(_spaceId, _topicId);
            _messages.Clear();
            foreach (var message in messages)
            {
                var payload = await _spaces.DecryptPayloadAsync(_spaceId, message);
                _messages.Add(SpaceMessageVm.From(
                    message,
                    payload ?? new SpaceMessagePayload
                    {
                        Kind = "text",
                        Text = "🔒 Сообщение не удалось расшифровать"
                    },
                    _myUserId ?? string.Empty,
                    _canModerate));
            }

            // Канал только для чтения — скрываем composер и показываем баннер.
            Composer.IsVisible = _canPost;
            ReadOnlyBanner.IsVisible = !_canPost;

            var members = await _spaces.GetMembersAsync(_spaceId);
            SpaceInfoLabel.Text = _topicTitle is null
                ? $"{members.Count} участников"
                : $"Тема: {_topicTitle} · {members.Count} участников";

            UpdatePinnedBanner();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

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
            if (_messages.Any(item => item.Id == message.Id))
            {
                return;
            }

            _messages.Add(SpaceMessageVm.From(
                message,
                payload ?? new SpaceMessagePayload
                {
                    Kind = "text",
                    Text = "🔒 Сообщение не удалось расшифровать"
                },
                _myUserId ?? string.Empty,
                _canModerate));
        });
    }

    private async void OnSendClicked(object sender, EventArgs e)
    {
        if (!_canPost)
        {
            await DisplayAlert("Только чтение", "В этом канале сообщения могут публиковать только администраторы.", "OK");
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
            var sent = await _spaces.SendMessageAsync(
                _spaceId,
                _topicId,
                text,
                replyToMessageId: replyTarget?.Id,
                replyToText: replyTarget?.ReplyToText);

            if (sent is not null && !_messages.Any(item => item.Id == sent.Id))
            {
                var payload = new SpaceMessagePayload
                {
                    Kind = "text",
                    Text = text,
                    ReplyToMessageId = replyTarget?.Id,
                    ReplyToText = replyTarget?.ReplyToText
                };
                _messages.Add(SpaceMessageVm.From(sent, payload, _myUserId ?? string.Empty, _canModerate));
            }
        }
        catch (Exception ex)
        {
            MessageEntry.Text = text;
            await DisplayAlert("Ошибка", ex.Message, "OK");
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
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
        MessageEntry.Focus();
    }

    private void OnCancelReplyClicked(object sender, EventArgs e)
    {
        _replyingTo = null;
        ReplyBar.IsVisible = false;
        MessageEntry.Focus();
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }

    private async void OnActionForwardClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: SpaceMessageVm message })
        {
            return;
        }

        MessageEntry.Text = $"↩️ {message.Text}";
        MessageEntry.Focus();
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
        MessageEntry.Focus();
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
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
                    message.SetImageBytes(bytes);
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
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

    /// <summary>Кнопка 😊 — показать или скрыть эмодзи-панель.</summary>
    private void OnEmojiButtonClicked(object sender, EventArgs e)
    {
        EmojiPicker.IsVisible = !EmojiPicker.IsVisible;
    }

    /// <summary>Эмодзи из панели — вставить в позицию курсора поля ввода.</summary>
    private void OnEmojiPicked(string emoji)
    {
        var text = MessageEntry.Text ?? string.Empty;
        var position = Math.Clamp(MessageEntry.CursorPosition, 0, text.Length);
        MessageEntry.Text = text.Insert(position, emoji);
        MessageEntry.CursorPosition = Math.Min(position + emoji.Length, MessageEntry.Text.Length);
        MessageEntry.Focus();
    }

    private async void OnAttachClicked(object sender, EventArgs e)
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null)
        {
            return;
        }

        var choice = await page.DisplayActionSheet("Прикрепить", "Отмена", null, "Фото или GIF", "Файл", "Голосовое сообщение", "Видеокружок");
        switch (choice)
        {
            case "Фото или GIF":
                var photos = await FilePicker.Default.PickMultipleAsync(
                    new PickOptions { PickerTitle = "Выберите фото или GIF" });
                if (photos is null || !photos.Any()) return;
                await SendAttachmentsAsync(photos, isPhoto: true);
                break;
            case "Файл":
                var files = await FilePicker.Default.PickMultipleAsync(
                    new PickOptions { PickerTitle = "Выберите файлы" });
                if (files is null || !files.Any()) return;
                await SendAttachmentsAsync(files, isPhoto: false);
                break;
            case "Голосовое сообщение":
                var voice = await RecordVoiceMessageAsync(page);
                if (voice is null) return;
                EnqueueMediaItem("audio.m4a", voice.Value.ContentType, voice.Value.Data, MessageKinds.Voice);
                break;
            case "Видеокружок":
                var video = await RecordVideoCircleAsync();
                if (video is null) return;
                EnqueueMediaItem("circle.mp4", video.Value.ContentType, video.Value.Data, MessageKinds.VideoCircle);
                break;
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
            await DisplayAlert("Ошибка", ex.Message, "OK");
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
        options.AddRange(topics.Select(topic => topic.Title));

        var selected = await DisplayActionSheet(
            "Темы форума",
            "Отмена",
            null,
            options.ToArray());
        if (string.IsNullOrWhiteSpace(selected) || selected == "Отмена")
        {
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

        var topic = topics.FirstOrDefault(item => item.Title == selected);
        if (topic is not null)
        {
            _topicId = topic.Id;
            _topicTitle = topic.Title;
            await LoadAsync();
        }
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

