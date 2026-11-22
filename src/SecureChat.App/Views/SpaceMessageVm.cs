using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SecureChat.App.Services;
using SecureChat.Core.Contracts;
using SecureChat.Core.Text;

namespace SecureChat.App.Views;

/// <summary>Сообщение пространства: текст, вложение, ответ, пересылка, реакции и права на действия.</summary>
public sealed class SpaceMessageVm : INotifyPropertyChanged
{
    private string _text = string.Empty;

    private bool _isSelected;
    private bool _isPinned;
    private bool _isEdited;
    private bool _canModerate;
    private bool _isPlaying;
    private double _playbackProgress;
    private double _playbackPositionSeconds;
    private double _playbackDurationSeconds;
    private bool _mediaDownloaded;
    private int _transferProgress;
    /// <summary>Прогресс передачи вложения 0..100 (отправка или скачивание).</summary>
    public int TransferProgress
    {
        get => _transferProgress;
        set { if (Set(ref _transferProgress, value)) OnPropertyChanged(nameof(TransferProgressText)); }
    }

    public string TransferProgressText => TransferProgress + "%";

    /// <summary>Вложение в процессе передачи — показываем круговой прогресс.</summary>
    public bool IsTransferring => TransferProgress >= 0 && TransferProgress < 100;

    private ImageSource? _imageSource;
    private FormattedString? _textFormatted;
    private byte[]? _gifBytes;
    private readonly bool _isChannel;
    private int _commentsCount;
    private int _viewCount;
    private bool _hasDateLabel;
    private string _dateLabel = string.Empty;
    private readonly List<string> _urls = new();
    private readonly List<LinkChip> _hiddenLinks = new();
    private readonly List<LinkPreview> _previews = new();

    /// <summary>
    /// Кэш логинов участников по пространствам (spaceId → логины). Нужен для
    /// подсветки @упоминаний в сообщениях, отправленных старыми версиями
    /// клиента, у которой payload не содержит списка упомянутых.
    /// Заполняется страницами пространств через <see cref="SetFallbackMembers"/>.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> FallbackMemberUsernames =
        new(StringComparer.Ordinal);

    public static void SetFallbackMembers(string spaceId, IEnumerable<string> usernames)
    {
        lock (FallbackMemberUsernames)
        {
            FallbackMemberUsernames[spaceId] =
                new HashSet<string>(usernames ?? [], StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Идентификатор сообщения пространства (совпадает с DTO Id).</summary>
    public required string Id { get; init; }

    /// <summary>Пространство, к которому относится сообщение (для фолбэк-подсветки упоминаний).</summary>
    public string SpaceId { get; init; } = string.Empty;

    /// <summary>Тема форума, к которой относится сообщение (null — без темы).</summary>
    public string? TopicId { get; init; }
    public required string SenderId { get; init; }
    public required string Sender { get; set; }

    /// <summary>
    /// Автор как он показан в ленте: в канале — название канала, а с подписями —
    /// «Канал через @админ». В группах совпадает с Sender.
    /// </summary>
    public string AuthorText { get; set; } = string.Empty;
    public bool IsMine { get; init; }
    public string Kind { get; init; } = "text";
    public SpaceMediaAttachment? Media { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? ReplyToText { get; init; }

    /// <summary>Логины упомянутых участников (@username), из расшифрованного payload.</summary>
    public List<string> Mentions { get; init; } = new();

    /// <summary>Пост канала, к которому относится комментарий. Заполнено только у комментариев.</summary>
    public string? CommentToMessageId { get; init; }

    /// <summary>Это комментарий к посту, а не сообщение ленты.</summary>
    public bool IsComment => CommentToMessageId is not null;
    public bool IsForwarded { get; init; }
    public string TimeText { get; init; } = string.Empty;

    /// <summary>Показывать время у постов (настройка «Время у сообщений»).</summary>
    public bool ShowTimestamps =>
        Application.Current?.Resources.TryGetValue("ShowTimestamps", out var value) == true &&
        value is bool show && show;

    /// <summary>Метка времени сервера — нужна для разделителей дат в ленте.</summary>
    public long SentAtUnixMs { get; init; }

    public DateTime LocalDate => DateTimeOffset.FromUnixTimeMilliseconds(SentAtUnixMs).ToLocalTime().Date;

    public bool HasDateLabel
    {
        get => _hasDateLabel;
        set => Set(ref _hasDateLabel, value);
    }

    public string DateLabel
    {
        get => _dateLabel;
        set => Set(ref _dateLabel, value);
    }
    public Style? BubbleStyle { get; init; }
    public Style? TextStyle { get; init; }
    public Color MetaColor { get; init; } = Colors.Gray;

    private Color _senderNameColor = Colors.Gray;

    /// <summary>Цвет имени отправителя: свой — по настройке «Цвет вашего имени»,
    /// чужой — акцент темы (как «Your name color» в Telegram).</summary>
    public Color SenderNameColor
    {
        get => _senderNameColor;
        set
        {
            if (_senderNameColor == value)
            {
                return;
            }

            _senderNameColor = value;
            OnPropertyChanged();
        }
    }
    public ObservableCollection<ReactionChipVm> ReactionChips { get; } = new();

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasText));
            RebuildTextFormatted();
            RebuildLinks();
        }
    }

    /// <summary>Пересобирает FormattedText: обычные куски без цвета, упоминания — акцент с подчёркиванием.</summary>
    private void RebuildTextFormatted()
    {
        if (string.IsNullOrEmpty(_text))
        {
            _textFormatted = null;
            OnPropertyChanged(nameof(TextFormatted));
            return;
        }

        var formatted = InlineTextBuilder.Build(
            _text,
            RenderMentions(),
            IsMine ? ResolveAccentText() : ResolveAccent(),
            ResolveMentionColor(),
            href => LinkTapped?.Invoke(this, href),
            name => MentionTapped?.Invoke(this, name),
            IsMine ? ResolveAccentText() : ResolveAccent(),
            tag => HashtagTapped?.Invoke(this, tag));

        // Текст пространства всегда рисуется через FormattedText: если стилизовать
        // нечего (ни ссылок, ни упоминаний), возвращаемся к одному обычному спану.
        _textFormatted = formatted ?? new FormattedString { Spans = { new Span { Text = _text } } };
        OnPropertyChanged(nameof(TextFormatted));
    }

    // ================================================================
    // ССЫЛКИ: веб-адреса, скрытые markdown-цели (чипы), превью-карточки.
    // Слоты фиксированные (три), как в личных чатах: BindableLayout внутри
    // пузыря не получает контекст элемента.
    // ================================================================

    public const int MaxPreviews = 3;

    private void RebuildLinks()
    {
        _urls.Clear();
        _hiddenLinks.Clear();
        foreach (var segment in TextLinks.Segment(_text))
        {
            if (segment.Href is not { } href || segment.Kind != LinkKind.Web)
            {
                continue;
            }

            if (!_urls.Contains(href, StringComparer.OrdinalIgnoreCase))
            {
                _urls.Add(href);
            }

            if (segment.IsHiddenTarget && !_hiddenLinks.Any(c => c.Href == href))
            {
                _hiddenLinks.Add(new LinkChip(TextLinks.ShortenHref(href), href));
            }
        }

        NotifyChips();
    }

    /// <summary>Веб-адреса сообщения в порядке текста.</summary>
    public IReadOnlyList<string> Urls => _urls;

    public bool HasUrls => _urls.Count > 0;

    private LinkChip? SlotChip(int index)
    {
        var visible = _hiddenLinks.Where(c => !HasPreviewFor(c.Href)).ToList();
        return index < visible.Count ? visible[index] : null;
    }

    public bool HasHiddenChips => _hiddenLinks.Any(c => !HasPreviewFor(c.Href));
    public bool ShowChip0 => SlotChip(0) is not null;
    public string Chip0Text => SlotChip(0)?.Display ?? string.Empty;
    public bool ShowChip1 => SlotChip(1) is not null;
    public string Chip1Text => SlotChip(1)?.Display ?? string.Empty;
    public bool ShowChip2 => SlotChip(2) is not null;
    public string Chip2Text => SlotChip(2)?.Display ?? string.Empty;

    public string? HrefForChip(int index) => SlotChip(index)?.Href;

    private LinkPreview? SlotPreview(int index) => index < _previews.Count ? _previews[index] : null;

    public bool HasLinkPreviews => _previews.Count > 0;

    public bool HasPreviewFor(string url) =>
        _previews.Any(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));

    public void AddPreview(LinkPreview preview)
    {
        if (HasPreviewFor(preview.Url) || _previews.Count >= MaxPreviews)
        {
            return;
        }

        _previews.Add(preview);
        NotifyPreviewSlots();
        NotifyChips();
    }

    public void ClearPreviews()
    {
        if (_previews.Count == 0)
        {
            return;
        }

        _previews.Clear();
        NotifyPreviewSlots();
        NotifyChips();
    }

    public string? HrefForPreview(int index) => SlotPreview(index)?.Url;

    public bool ShowPreview0 => SlotPreview(0) is not null;
    public bool Preview0HasImage => SlotPreview(0)?.HasImage == true;
    public ImageSource? Preview0Image => SlotPreview(0)?.ImageSource;
    public string Preview0Title => SlotPreview(0)?.Title ?? string.Empty;
    public string Preview0Description => SlotPreview(0)?.Description ?? string.Empty;
    public string Preview0Domain => SlotPreview(0)?.Domain ?? string.Empty;

    public bool ShowPreview1 => SlotPreview(1) is not null;
    public bool Preview1HasImage => SlotPreview(1)?.HasImage == true;
    public ImageSource? Preview1Image => SlotPreview(1)?.ImageSource;
    public string Preview1Title => SlotPreview(1)?.Title ?? string.Empty;
    public string Preview1Description => SlotPreview(1)?.Description ?? string.Empty;
    public string Preview1Domain => SlotPreview(1)?.Domain ?? string.Empty;

    public bool ShowPreview2 => SlotPreview(2) is not null;
    public bool Preview2HasImage => SlotPreview(2)?.HasImage == true;
    public ImageSource? Preview2Image => SlotPreview(2)?.ImageSource;
    public string Preview2Title => SlotPreview(2)?.Title ?? string.Empty;
    public string Preview2Description => SlotPreview(2)?.Description ?? string.Empty;
    public string Preview2Domain => SlotPreview(2)?.Domain ?? string.Empty;

    private void NotifyPreviewSlots()
    {
        OnPropertyChanged(nameof(HasLinkPreviews));
        for (var i = 0; i < MaxPreviews; i++)
        {
            OnPropertyChanged($"ShowPreview{i}");
            OnPropertyChanged($"Preview{i}HasImage");
            OnPropertyChanged($"Preview{i}Image");
            OnPropertyChanged($"Preview{i}Title");
            OnPropertyChanged($"Preview{i}Description");
            OnPropertyChanged($"Preview{i}Domain");
        }
    }

    private void NotifyChips()
    {
        OnPropertyChanged(nameof(HasHiddenChips));
        for (var i = 0; i < MaxPreviews; i++)
        {
            OnPropertyChanged($"ShowChip{i}");
            OnPropertyChanged($"Chip{i}Text");
        }
    }

    /// <summary>В этом сообщении упомянули лично меня.</summary>
    public bool MentionedMe { get; private set; }

    /// <summary>Упомянули всех через @all (без личного упоминания).</summary>
    public bool MentionedByAll { get; private set; }

    /// <summary>Это ответ на моё сообщение.</summary>
    public bool RepliedToMe { get; private set; }

    /// <summary>Плашка в ленте: упоминание или ответ, адресованные мне.</summary>
    public bool ShowMentionBadge => !IsMine && (MentionedMe || MentionedByAll || RepliedToMe);

    public string MentionBadgeText => MentionedMe
        ? "🔔 Упомянули вас"
        : RepliedToMe
            ? "↩ Ответ вам"
            : "📢 Упомянули всех";

    /// <summary>Имена для подсветки: из payload или (для старых сообщений) по текущим участникам.</summary>
    private IReadOnlyCollection<string> RenderMentions()
    {
        if (Mentions.Count > 0)
        {
            return Mentions;
        }

        HashSet<string>? members;
        lock (FallbackMemberUsernames)
        {
            FallbackMemberUsernames.TryGetValue(SpaceId, out members);
        }

        if (members is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        return SpaceMentions.Parse(_text, members);
    }

    private static Color ResolveAccent() =>
        (Application.Current?.Resources["Accent"] as Color) ?? Color.FromArgb("#0088CC");

    private static Color ResolveAccentText() =>
        (Application.Current?.Resources["AccentText"] as Color) ?? Colors.White;

    /// <summary>Цвет упоминания: в своём пузыре (акцентный фон) — светлый, в чужом — акцент.</summary>
    private Color ResolveMentionColor()
    {
        if (IsMine)
        {
            return (Application.Current?.Resources["AccentText"] as Color) ?? Colors.White;
        }

        return ResolveAccent();
    }

    public bool IsEdited
    {
        get => _isEdited;
        set
        {
            if (_isEdited == value)
            {
                return;
            }

            _isEdited = value;
            OnPropertyChanged();
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value)
            {
                return;
            }

            _isPinned = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool CanModerate
    {
        get => _canModerate;
        set
        {
            if (_canModerate == value)
            {
                return;
            }

            _canModerate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanPin));
        }
    }

    public ImageSource? ImageSource
    {
        get => _imageSource;
        private set
        {
            _imageSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsImageReady));
            OnPropertyChanged(nameof(IsImagePending));
        }
    }

    public bool IsImage => Kind == "image";
    public bool IsFile => Kind == "file";
    public bool IsVoice => Kind == MessageKinds.Voice;
    public bool IsVideoCircle => Kind == MessageKinds.VideoCircle;

    /// <summary>Анимированный GIF: рисуется через GifWebView, кадры идут сами.</summary>
    public bool IsAnimatedGif { get; private set; }

    /// <summary>Обычная статичная картинка (не GIF).</summary>
    public bool IsStaticImage => IsImage && !IsAnimatedGif;

    /// <summary>Байты расшифрованного GIF (заполняются после скачивания).</summary>
    public byte[]? GifBytes => _gifBytes;

    /// <summary>Цвет подложки под GIF (в тон пузыря), HTML #RRGGBB.</summary>
    public string GifSurfaceHex { get; private set; } = "#3B4048";

    public bool IsImageReady => ImageSource is not null;
    public bool IsImagePending => IsImage && ImageSource is null;
    public bool HasText => !string.IsNullOrEmpty(Text) && Poll is null;

    /// <summary>Текст с подсветкой упоминаний (для Label FormattedText); null — обычный текст.</summary>
    public FormattedString? TextFormatted => _textFormatted;

    /// <summary>Число известных клиенту комментариев к посту.</summary>

    public int ViewCount
    {
        get => _viewCount;
        set
        {
            if (_viewCount == value) return;
            _viewCount = value;
            OnPropertyChanged(nameof(ViewCount));
            OnPropertyChanged(nameof(StatsText));
            OnPropertyChanged(nameof(HasStats));
        }
    }

    public string StatsText => ViewCount > 0 ? $"👁 {ViewCount}" : string.Empty;
    public bool HasStats => ViewCount > 0;

    public int CommentsCount
    {
        get => _commentsCount;
        set
        {
            if (Set(ref _commentsCount, value))
            {
                OnPropertyChanged(nameof(CommentsText));
            }
        }
    }

    /// <summary>Показывать ли кнопку «Комментарии» — посты каналов (не сами комментарии).</summary>
    public bool ShowComments => _isChannel && !IsComment;

    /// <summary>Подпись кнопки: «Ответить» без комментариев, иначе число с русскими формами.</summary>
    public string CommentsText => CommentsCount switch
    {
        0 => "💬 Ответить",
        1 => "💬 1 комментарий",
        < 5 => $"💬 {CommentsCount} комментария",
        _ => $"💬 {CommentsCount} комментариев"
    };

    /// <summary>Нажали на упоминание в тексте: (сообщение, логин упомянутого).</summary>
    public event Action<SpaceMessageVm, string>? MentionTapped;

    /// <summary>Тап по #хештегу в тексте сообщения.</summary>
    public event Action<SpaceMessageVm, string>? HashtagTapped;

    /// <summary>Голос за вариант опроса: второй аргумент — индекс варианта.</summary>
    public event Action<SpaceMessageVm, int>? PollVoteRequested;

    /// <summary>Опрос в сообщении пространства.</summary>
    public PollVm? Poll { get; private set; }
    public bool HasPoll => Poll is not null;
    /// <summary>Голоса в опросе не показываются отдельными сообщениями.</summary>
    public bool VisibleInFeed => Kind != SecureChat.Core.Contracts.MessageKinds.PollVote;

    /// <summary>Нажали на ссылку в тексте: (сообщение, адрес для открытия).</summary>
    public event Action<SpaceMessageVm, string>? LinkTapped;

    public bool HasReply => !string.IsNullOrEmpty(ReplyToText);
    public bool HasReactions => ReactionChips.Count > 0;
    public bool ShowAvatar => !_isChannel && !IsMine;

    /// <summary>Показывать строку автора: в канале — всегда (это «Название канала
    /// через @админ»), в группе — только у чужих сообщений. В канале без подписей
    /// страница сама оставляет подпись только у первого поста серии.</summary>
    public bool ShowAuthor { get; set; }

    /// <summary>Это сообщение канала (а не группы): автор — сам канал.</summary>
    public bool IsChannelPost => _isChannel;
    public string SenderInitial => string.IsNullOrEmpty(Sender) ? "?" : Sender[..1].ToUpperInvariant();
    public string FileName => Media?.FileName ?? string.Empty;
    public string FileSizeText => Media is null ? string.Empty : FormatSize(Media.Size);
    public bool CanEdit => IsMine && Kind == "text";
    public bool CanDelete => IsMine || CanModerate;
    public bool CanPin => CanModerate;

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    /// <summary>Заполнение аудиодорожки в процентах (0–100) для ProgressBar.</summary>
    public double PlaybackProgress
    {
        get => _playbackProgress;
        private set => Set(ref _playbackProgress, value);
    }

    public double PlaybackPositionSeconds => _playbackPositionSeconds;

    public double PlaybackDurationSeconds => _playbackDurationSeconds;

    public string PlaybackTimeText => FormatPlaybackTime(_playbackPositionSeconds);

    public string PlaybackDurationText =>
        _playbackDurationSeconds > 0 ? FormatPlaybackTime(_playbackDurationSeconds) : string.Empty;

    public string PlaybackTimerText => _playbackDurationSeconds > 0
        ? $"{PlaybackTimeText} / {PlaybackDurationText}"
        : PlaybackTimeText;

    /// <summary>Обновляет позицию и длительность реального аудиоплеера (в секундах).</summary>
    public void UpdatePlayback(double positionSeconds, double durationSeconds)
    {
        positionSeconds = double.IsFinite(positionSeconds) ? Math.Max(0, positionSeconds) : 0;
        durationSeconds = double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0;

        _playbackPositionSeconds = positionSeconds;
        _playbackDurationSeconds = durationSeconds;
        _playbackProgress = durationSeconds > 0
            ? Math.Clamp(positionSeconds / durationSeconds * 100.0, 0, 100)
            : 0;

        OnPropertyChanged(nameof(PlaybackProgress));
        OnPropertyChanged(nameof(PlaybackPositionSeconds));
        OnPropertyChanged(nameof(PlaybackDurationSeconds));
        OnPropertyChanged(nameof(PlaybackTimeText));
        OnPropertyChanged(nameof(PlaybackDurationText));
        OnPropertyChanged(nameof(PlaybackTimerText));
    }

    /// <summary>Сбрасывает воспроизведение в начало, сохраняя известную длительность.</summary>
    public void ResetPlayback()
    {
        IsPlaying = false;
        UpdatePlayback(0, _playbackDurationSeconds);
    }

    public bool MediaDownloaded
    {
        get => _mediaDownloaded;
        set
        {
            if (Set(ref _mediaDownloaded, value))
            {
                OnPropertyChanged(nameof(IsFilePending));
            }
        }
    }

    public bool IsFilePending => IsFile && !MediaDownloaded;

    public void SetImageBytes(byte[] bytes) =>
        ImageSource = ImageSource.FromStream(() => new MemoryStream(bytes));

    /// <summary>Для GIF дополнительно храним байты: их рисует GifWebView с полными кадрами.</summary>
    public void SetImageContent(byte[] bytes)
    {
        SetImageBytes(bytes);
        TransferProgress = 100;
        if (IsAnimatedGif)
        {
            _gifBytes = bytes;
            OnPropertyChanged(nameof(GifBytes));
        }
    }

    public void SetReactions(IEnumerable<Reaction> reactions, string myUserId)
    {
        ReactionChips.Clear();
        foreach (var group in reactions.GroupBy(item => item.Emoji, StringComparer.Ordinal))
        {
            ReactionChips.Add(new ReactionChipVm
            {
                Emoji = group.Key,
                Count = group.Count(),
                IsMine = group.Any(item => string.Equals(item.UserId, myUserId, StringComparison.Ordinal))
            });
        }

        OnPropertyChanged(nameof(HasReactions));
    }

    private static PollData? TryParsePollJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '{' ||
            !text.Contains("\"Question\"", StringComparison.Ordinal) ||
            !text.Contains("\"Options\"", StringComparison.Ordinal))
        {
            return null;
        }
        try { return JsonSerializer.Deserialize<PollData>(text); }
        catch (JsonException) { return null; }
    }

    public static SpaceMessageVm From(
        SpaceMessageDto source,
        SpaceMessagePayload payload,
        string myUserId,
        bool canModerate,
        bool isChannel = false,
        string? myUsername = null)
    {
        var mine = string.Equals(source.SenderId, myUserId, StringComparison.Ordinal);
        var vm = new SpaceMessageVm(isChannel)
        {
            Id = source.Id,
            SpaceId = source.SpaceId,
            TopicId = source.TopicId,
            SenderId = source.SenderId,
            Sender = source.SenderUsername,
            IsMine = mine,
            Kind = payload.Kind,
            Media = payload.Media,
            ReplyToMessageId = payload.ReplyToMessageId,
            ReplyToText = payload.ReplyToText,
            Mentions = payload.Mentions,
            CommentToMessageId = payload.CommentToMessageId,
            IsForwarded = payload.IsForwarded,
            SentAtUnixMs = source.SentAtUnixMs,
            TimeText = DateTimeOffset.FromUnixTimeMilliseconds(source.SentAtUnixMs)
                .ToLocalTime()
                .ToString("HH:mm"),
            AuthorText = source.SenderUsername,
            // В канале строка «Название канала через @админ» показывается у
            // каждого поста — иначе «от кого» пропадает у части ленты.
            // В группах и сообществах имя показывается у чужих сообщений.
            ShowAuthor = isChannel || !mine,
            BubbleStyle = (Style)Application.Current!.Resources[mine ? "MessageBubbleMe" : "MessageBubbleThem"],
            TextStyle = (Style)Application.Current!.Resources[mine ? "MessageTextMe" : "MessageTextThem"],
            MetaColor = mine ? Color.FromArgb("#BFDBFE") : Color.FromArgb("#94A3B8")
        };

        if (payload.Kind == SecureChat.Core.Contracts.MessageKinds.Poll)
        {
            var data = payload.Poll ?? TryParsePoll(payload.Text);
            if (data is not null && data.Options.Count > 0)
            {
                var vmForVote = vm;
                vm.Poll = new PollVm(data.Id, data.Question, data.Options,
                    index => vm.PollVoteRequested?.Invoke(vmForVote, index));
            }
        }

        vm.SenderNameColor = ResolveSenderNameColor(mine);
        vm._text = payload.Text;

        var pollData = payload.Kind == SecureChat.Core.Contracts.MessageKinds.Poll
            ? payload.Poll ?? TryParsePollJson(payload.Text)
            : TryParsePollJson(payload.Text);
        if (pollData is not null && pollData.Options.Count > 0)
        {
            var vmForVote = vm;
            vm.Poll = new PollVm(pollData.Id, pollData.Question, pollData.Options,
                index => vm.PollVoteRequested?.Invoke(vmForVote, index));
        }
        vm._isEdited = source.EditedAtUnixMs > 0;
        vm._isPinned = source.IsPinned;
        vm._canModerate = canModerate;
        vm.IsAnimatedGif = IsGifMedia(payload.Media);
        vm.GifSurfaceHex = ResolveBubbleHex(mine);
        vm.RebuildTextFormatted();
        vm.RebuildLinks();
        vm.SetReactions(source.Reactions, myUserId);

        if (!string.IsNullOrEmpty(myUsername))
        {
            vm.MentionedMe = vm.Mentions.Any(name =>
                string.Equals(name, myUsername, StringComparison.OrdinalIgnoreCase));
            vm.MentionedByAll = !vm.MentionedMe && vm.Mentions.Contains(
                SpaceMentions.AllToken, StringComparer.OrdinalIgnoreCase);
            vm.RepliedToMe = string.Equals(payload.ReplyToSender, myUsername,
                StringComparison.OrdinalIgnoreCase);
        }

        return vm;
    }

    /// <summary>Цвет имени: для своих сообщений — настройка «Цвет вашего имени»
    /// (ресурс SenderNameColor), для чужих — акцент темы.</summary>
    public static Color ResolveSenderNameColor(bool mine)
    {
        var res = Application.Current?.Resources;
        if (res is null)
        {
            return Colors.Gray;
        }

        if (mine && res["SenderNameColor"] is Color nameColor)
        {
            return nameColor;
        }

        return res["Accent"] is Color accent ? accent : Colors.Gray;
    }

    /// <summary>GIF-вложение: по типу содержимого или по расширению файла.</summary>
    private static bool IsGifMedia(SpaceMediaAttachment? media) =>
        media is not null &&
        (media.ContentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase) ||
         media.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase));

    /// <summary>Подложка GIF — цвет текущего пузыря (я/не я), HTML #RRGGBB.</summary>
    private static string ResolveBubbleHex(bool mine)
    {
        try
        {
            var style = (Style)Application.Current!.Resources[mine ? "MessageBubbleMe" : "MessageBubbleThem"];
            var setter = style.Setters.FirstOrDefault(item =>
                item.Property == Microsoft.Maui.Controls.Border.BackgroundColorProperty);

            if (setter?.Value is Color color)
            {
                return ToHtmlHex(color);
            }

            if (setter?.Value is SolidColorBrush brush)
            {
                return ToHtmlHex(brush.Color);
            }
        }
        catch
        {
            // Нет стиля — берём нейтральную подложку ниже.
        }

        return mine ? "#2B5278" : "#182533";
    }

    private static string ToHtmlHex(Color color) =>
        $"#{(int)Math.Round(color.Red * 255):X2}" +
        $"{(int)Math.Round(color.Green * 255):X2}" +
        $"{(int)Math.Round(color.Blue * 255):X2}";

    public static SpaceMessageVm From(
        SpaceMessageDto source,
        string text,
        string myUserId,
        bool canModerate)
    {
        return From(source, new SpaceMessagePayload { Kind = "text", Text = text }, myUserId, canModerate);
    }

    private static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} Б",
        < 1024 * 1024 => $"{size / 1024.0:0.#} КБ",
        _ => $"{size / (1024.0 * 1024.0):0.#} МБ"
    };

    private static string FormatPlaybackTime(double seconds)
    {
        var total = (int)Math.Floor(Math.Max(0, seconds));
        var time = TimeSpan.FromSeconds(total);
        return total >= 3600 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private SpaceMessageVm(bool isChannel)
    {
        _isChannel = isChannel;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private static PollData? TryParsePoll(string text)
    {
        try { return JsonSerializer.Deserialize<PollData>(text); }
        catch (JsonException) { return null; }
    }
}
