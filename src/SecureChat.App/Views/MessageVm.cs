using System.Text.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using SecureChat.App.Services;
using SecureChat.Core.Contracts;
using SecureChat.Core.Text;

namespace SecureChat.App.Views;

/// <summary>Одно сообщение в переписке: текст, вложение, реакции и состояние выделения.</summary>
public sealed class MessageVm : INotifyPropertyChanged
{
    private static readonly string[] AvatarPalette =
    {
        "#5865F2", "#3BA55D", "#FAA81A", "#ED4245", "#EB459E", "#0088CC", "#8B5CF6"
    };

    private string _text = string.Empty;
    private FormattedString? _textFormatted;
    private string _fileName = string.Empty;
    private byte[]? _gifBytes;
    private string _contentType = string.Empty;
    private long _mediaSize;
    private bool _mediaDownloaded;
    private bool _isPinned;
    private bool _isEdited;
    private string? _editedText;
    private bool _isForwarded;
    private bool _isPlaying;
    private double _playbackProgress;
    private double _playbackPositionSeconds;
    private double _playbackDurationSeconds;
    private bool _isDraft;
    private string? _replyToText;
    private string _deliveryStatus = string.Empty;
    private string _senderDisplay = string.Empty;
    private string? _senderAvatarBase64;
    private ImageSource? _senderAvatarImageSource;
    private bool _isSelected;
    private bool _matchesSearch = true;
    private bool _hasDateLabel;
    private string _dateLabel = string.Empty;
    private bool _showAvatar;
    private ImageSource? _imageSource;
    private string? _username;
    private bool _isArchived;
    private readonly List<string> _urls = new();
    private readonly List<LinkChip> _hiddenLinks = new();

    public MessageVm(StoredMessage message, string ownName, string peerName)
    {
        Id = message.Id;
        Kind = string.IsNullOrEmpty(message.Kind) ? MessageKinds.Text : message.Kind;
        IsMine = message.IsMine;
        SentAtUnixMs = message.SentAtUnixMs;
        IsOnce = message.IsOnce;
        var pollData = Kind == MessageKinds.Poll
            ? TryParsePoll(message.Text, strict: true)
            : TryParsePoll(message.Text, strict: false);
        if (pollData is not null && pollData.Options.Count > 0)
        {
            Poll = new PollVm(pollData.Id, pollData.Question, pollData.Options,
                index => PollVoteRequested?.Invoke(this, index));
        }
        Update(message, ownName, peerName);
    }

    /// <summary>Разбор опроса: строгий для Kind=poll, эвристический для утерянного Kind.</summary>
    private static PollData? TryParsePoll(string? text, bool strict)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!strict && (text[0] != '{' ||
            !text.Contains("\"Question\"", StringComparison.Ordinal) ||
            !text.Contains("\"Options\"", StringComparison.Ordinal)))
        {
            return null;
        }
        try { return JsonSerializer.Deserialize<PollData>(text); }
        catch (JsonException) { return null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Нажали на @упоминание в тексте: второй аргумент — логин упомянутого.</summary>
    public event Action<MessageVm, string>? MentionTapped;

    /// <summary>Тап по #хештегу в тексте сообщения.</summary>
    public event Action<MessageVm, string>? HashtagTapped;

    /// <summary>Нажали на ссылку в тексте: второй аргумент — адрес для открытия.</summary>
    public event Action<MessageVm, string>? LinkTapped;
    /// <summary>Голос за вариант опроса: второй аргумент — индекс варианта.</summary>
    public event Action<MessageVm, int>? PollVoteRequested;

    /// <summary>Опрос в этом сообщении (Kind=Poll).</summary>
    public PollVm? Poll { get; private set; }
    public bool HasPoll => Poll is not null;

    public string Id { get; }
    public string Kind { get; }
    public bool IsMine { get; }
    public long SentAtUnixMs { get; }

    public DateTime LocalDate => ToLocal().Date;
    public string TimeText => ToLocal().ToString("HH:mm");

    public bool IsImage => Kind == MessageKinds.Image;
    public bool IsFile => Kind == MessageKinds.File;
    public bool IsVoice => Kind == MessageKinds.Voice;
    public bool IsVideoCircle => Kind == MessageKinds.VideoCircle;

    /// <summary>Стикер — крупный эмодзи-глиф (Kind=Sticker), как большие эмодзи в Telegram.</summary>
    public bool IsSticker => Kind == MessageKinds.Sticker;

    /// <summary>Видео-файл: Kind=File с video/*-типом.</summary>
    public bool IsVideoFile => IsFile && ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Видео вообще (кружок или видео-файл).</summary>
    public bool IsVideoMedia => IsVideoCircle || IsVideoFile;

    /// <summary>Байты расшифрованного видео (заполняются после загрузки).</summary>
    public byte[]? VideoBytes { get; private set; }

    public bool IsVideoReady => IsVideoMedia && !IsVideoOversize && VideoBytes is not null;

    /// <summary>Видео больше InlineVideoMaxBytes: инлайн-плеер небезопасен по памяти,
    /// показываем карточку с нативным плеером (как в Telegram для больших файлов).</summary>
    public bool IsVideoOversize => IsVideoMedia && MediaSize > MediaLimits.InlineVideoMaxBytes;

    /// <summary>Показать карточку с кнопкой ▶ вместо встроенного видео.</summary>
    public bool ShowVideoFallback => IsVideoOversize;

    public bool IsVideoPending => IsVideoMedia && !IsVideoOversize && VideoBytes is null;

    /// <summary>Иконка вложения по виду: фото, видео, голосовое, кружок, аудио, документ.</summary>
    public string MediaIcon => Kind switch
    {
        MessageKinds.Image => "📷",
        MessageKinds.VideoCircle => "📹",
        MessageKinds.Voice => "🎤",
        MessageKinds.Sticker => "🧩",
        _ when IsVideoFile => "🎬",
        _ when ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "🎵",
        _ => "📄"
    };

    /// <summary>Подпись вида вложения для вкладки «Медиа и файлы».</summary>
    public string MediaTypeText => Kind switch
    {
        MessageKinds.Image => "Фото",
        MessageKinds.VideoCircle => "Видеокружок",
        MessageKinds.Voice => "Голосовое сообщение",
        _ when IsVideoFile => "Видео",
        _ when ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "Аудио",
        _ => "Файл"
    };

    /// <summary>Показывать иконку вида вложения (фото без превью — тоже иконка).</summary>
    public bool ShowMediaIcon => !IsImage || !IsImageReady;

    /// <summary>Одно фото обычным пузырём (в альбоме фото показываются сеткой).</summary>
    public bool ShowSingleImage => IsImage && !IsAlbumHead;

    /// <summary>Идентификатор альбома (пусто — одиночное вложение).</summary>
    public string AlbumId { get; private set; } = string.Empty;

    /// <summary>Номер вложения в альбоме (с нуля).</summary>
    public int AlbumIndex { get; private set; }

    /// <summary>Всего вложений в альбоме.</summary>
    public int AlbumCount { get; private set; }

    /// <summary>Вложение входит в альбом.</summary>
    public bool IsAlbum => !string.IsNullOrEmpty(AlbumId) && AlbumCount > 1;

    /// <summary>Первое вложение альбома — в нём показываем весь блок сеткой.</summary>
    public bool IsAlbumHead => IsAlbum && AlbumIndex == 0;

    /// <summary>Продолжение альбома — скрыто, фото показано в первом вложении.</summary>
    public bool IsAlbumTail => IsAlbum && AlbumIndex > 0;

    /// <summary>Превью фото альбома (до четырёх).</summary>
    public ObservableCollection<ImageSource> AlbumImages { get; } = new();

    /// <summary>Фото альбома по слотам сетки 2×2.</summary>
    public ImageSource? AlbumImage0 { get; private set; }
    public ImageSource? AlbumImage1 { get; private set; }
    public ImageSource? AlbumImage2 { get; private set; }
    public ImageSource? AlbumImage3 { get; private set; }

    public bool HasAlbumImage0 => AlbumImage0 is not null;
    public bool HasAlbumImage1 => AlbumImage1 is not null;
    public bool HasAlbumImage2 => AlbumImage2 is not null;
    public bool HasAlbumImage3 => AlbumImage3 is not null;

    /// <summary>Подпись «2 из 5» под альбомом.</summary>
    public string AlbumLabel => IsAlbum ? $"{AlbumIndex + 1} из {AlbumCount}" : string.Empty;

    /// <summary>Подпись «3 фото» под блоком альбома.</summary>
    public string AlbumCountLabel => IsAlbum ? $"{AlbumCount} фото" : string.Empty;

    /// <summary>Превью фото альбома для сетки 2×2.</summary>
    public void SetAlbumImages(IReadOnlyList<ImageSource> images)
    {
        AlbumImage0 = images.Count > 0 ? images[0] : null;
        AlbumImage1 = images.Count > 1 ? images[1] : null;
        AlbumImage2 = images.Count > 2 ? images[2] : null;
        AlbumImage3 = images.Count > 3 ? images[3] : null;

        Notify(nameof(AlbumImage0));
        Notify(nameof(AlbumImage1));
        Notify(nameof(AlbumImage2));
        Notify(nameof(AlbumImage3));
        Notify(nameof(HasAlbumImage0));
        Notify(nameof(HasAlbumImage1));
        Notify(nameof(HasAlbumImage2));
        Notify(nameof(HasAlbumImage3));
    }

    /// <summary>Прогресс загрузки вложения (downloaded = -1 — загрузка закончена).</summary>
    public void SetAlbum(string? albumId, int albumIndex, int albumCount)
    {
        AlbumId = albumId ?? string.Empty;
        AlbumIndex = albumIndex;
        AlbumCount = albumCount;
        Notify(nameof(AlbumId));
        Notify(nameof(AlbumIndex));
        Notify(nameof(AlbumCount));
        Notify(nameof(IsAlbum));
        Notify(nameof(IsAlbumHead));
        Notify(nameof(IsAlbumTail));
        Notify(nameof(AlbumLabel));
        Notify(nameof(AlbumCountLabel));
        Notify(nameof(ShowSingleImage));
    }

    /// <summary>Идёт загрузка вложения (фото, видео или файла).</summary>
    public bool IsMediaDownloading { get; private set; }

    /// <summary>Показывать простую надпись «Загрузка…» (когда прогресса ещё нет).</summary>
    public bool ShowMediaDownloadText => !IsMediaDownloading;

    /// <summary>Прогресс загрузки вложения: 0..1.</summary>
    public double MediaProgress { get; private set; }

    /// <summary>Текст прогресса: «37% · 12,4 МБ из 33 МБ».</summary>
    public string MediaProgressText => MediaProgress > 0
        ? $"{MediaProgress * 100:0}% · {FormatSize(_mediaProgressDownloaded)} из {FormatSize(_mediaTotal)}"
        : "Загрузка…";

    private long _mediaProgressDownloaded;
    private long _mediaTotal;

    /// <summary>Обновляет прогресс загрузки вложения (downloaded = -1 — загрузка закончена).</summary>
    public void SetMediaProgress(long downloaded, long total)
    {
        if (downloaded < 0)
        {
            if (!IsMediaDownloading)
            {
                return;
            }

            IsMediaDownloading = false;
            MediaProgress = 0;
            _mediaProgressDownloaded = 0;
            _mediaTotal = 0;
            Notify(nameof(IsMediaDownloading));
            Notify(nameof(ShowMediaDownloadText));
            Notify(nameof(MediaProgress));
            Notify(nameof(MediaProgressText));
            return;
        }

        _mediaProgressDownloaded = downloaded;
        _mediaTotal = total;
        MediaProgress = total > 0 ? Math.Clamp((double)downloaded / total, 0, 1) : 0;
        IsMediaDownloading = true;
        Notify(nameof(IsMediaDownloading));
        Notify(nameof(ShowMediaDownloadText));
        Notify(nameof(MediaProgress));
        Notify(nameof(MediaProgressText));
    }

    /// <summary>Длительность видео из mp4-заголовка, «ММ:СС»; пусто — неизвестна.</summary>
    public string VideoDurationText => _videoDurationText;

    public bool HasVideoDuration => !string.IsNullOrEmpty(_videoDurationText);

    private string _videoDurationText = string.Empty;

    /// <summary>Заполняет встроенное видео в пузыре (и длительность из mp4).</summary>
    public void SetVideoBytes(byte[] bytes)
    {
        if (bytes is not { Length: > 0 } data || VideoBytes is not null)
        {
            return;
        }

        VideoBytes = data;
        _videoDurationText = ReadVideoDuration(data);
        Notify(nameof(VideoBytes));
        Notify(nameof(IsVideoReady));
        Notify(nameof(IsVideoPending));
        Notify(nameof(VideoDurationText));
        Notify(nameof(HasVideoDuration));
    }

    /// <summary>Длительность mp4: ищем бокс mvhd (version 0/1). Big-endian по спецификации.</summary>
    private static string ReadVideoDuration(byte[] data)
    {
        try
        {
            for (var i = 0; i + 36 < data.Length; i++)
            {
                if (data[i] != 'm' || data[i + 1] != 'v' || data[i + 2] != 'h' || data[i + 3] != 'd')
                {
                    continue;
                }

                long duration;
                long timescale;
                if (data[i + 4] == 1)
                {
                    // v1: creation(8) + modification(8) + timescale(4) + duration(8)
                    timescale = ReadInt32BE(data, i + 24);
                    duration = ReadInt64BE(data, i + 28);
                }
                else
                {
                    // v0: creation(4) + modification(4) + timescale(4) + duration(4)
                    timescale = ReadInt32BE(data, i + 16);
                    duration = ReadInt32BE(data, i + 20);
                }

                if (timescale <= 0 || duration < 0)
                {
                    return string.Empty;
                }

                var seconds = (long)Math.Round(duration / (double)timescale);
                var time = TimeSpan.FromSeconds(seconds);
                return time.TotalHours >= 1
                    ? $"{(int)time.TotalHours:0}:{time.Minutes:00}:{time.Seconds:00}"
                    : $"{time.Minutes:00}:{time.Seconds:00}";
            }
        }
        catch
        {
            // Повреждённый/нестандартный заголовок — бейдж длительности просто скрыт.
        }

        return string.Empty;
    }

    private static uint ReadInt32BE(byte[] data, int index) =>
        ((uint)data[index] << 24) | ((uint)data[index + 1] << 16) | ((uint)data[index + 2] << 8) | data[index + 3];

    private static long ReadInt64BE(byte[] data, int index) =>
        (((long)data[index] << 56) | ((long)data[index + 1] << 48) | ((long)data[index + 2] << 40) |
         ((long)data[index + 3] << 32) | ((long)data[index + 4] << 24) | ((long)data[index + 5] << 16) |
         ((long)data[index + 6] << 8) | data[index + 7]) & 0x7FFFFFFFFFFFFFFF;

    public bool ShowText => Kind == MessageKinds.Text && Poll is null;
    public bool CanOpenFile => IsFile;

    public string Text
    {
        get => _text;
        private set
        {
            if (Set(ref _text, value))
            {
                Notify(nameof(DisplayText));
                RebuildUrls(value);
                RebuildRichText();
            }
        }
    }

    /// <summary>Текст с кликабельными ссылками и подсвеченными @упоминаниями
    /// (null — показываем обычный DisplayText).</summary>
    public FormattedString? TextFormatted => _textFormatted;

    /// <summary>Показывать простой текст — когда стилизовать нечего.</summary>
    public bool ShowPlainText => ShowText && _textFormatted is null;

    /// <summary>Показывать текст с подсветкой ссылок и упоминаний.</summary>
    public bool ShowRichText => ShowText && _textFormatted is not null;

    /// <summary>
    /// Кликабельный текст личного чата: ссылки (URL, домены без схемы, e-mail,
    /// телефон, markdown [текст](url)) и подсветка @упоминаний. Тап по упоминанию
    /// открывает профиль, тап по ссылке — адрес. Списка участников в ЛС нет,
    /// поэтому подсвечиваем любой логин после «@».
    /// </summary>
    private void RebuildRichText()
    {
        var color = IsMine ? Res("AccentText") : Res("Accent");
        var formatted = InlineTextBuilder.Build(
            _text,
            SpaceMentions.AllTokens(_text),
            color,
            color,
            href => LinkTapped?.Invoke(this, href),
            name => MentionTapped?.Invoke(this, name),
            color,
            tag => HashtagTapped?.Invoke(this, tag));

        _textFormatted = formatted;
        Notify(nameof(TextFormatted));
        Notify(nameof(ShowPlainText));
        Notify(nameof(ShowRichText));
    }

    private void RebuildUrls(string? text)
    {
        _urls.Clear();
        _hiddenLinks.Clear();
        foreach (var segment in TextLinks.Segment(text))
        {
            if (segment.Href is not { } href || segment.Kind != LinkKind.Web)
            {
                continue;
            }

            if (!_urls.Contains(href, StringComparer.OrdinalIgnoreCase))
            {
                _urls.Add(href);
            }

            // Цель спрятана за markdown-меткой: показываем её отдельным чипом.
            if (segment.IsHiddenTarget && !_hiddenLinks.Any(c => c.Href == href))
            {
                _hiddenLinks.Add(new LinkChip(TextLinks.ShortenHref(href), href));
            }
        }

        Notify(nameof(Urls));
        Notify(nameof(HasUrls));
        NotifyChipSlots();
    }

    public string DisplayText => _text;

    /// <summary>Веб-адреса сообщения (https://…, включая цели markdown-ссылок) в порядке текста.</summary>
    public IReadOnlyList<string> Urls => _urls;

    public bool HasUrls => _urls.Count > 0;

    private readonly List<LinkPreview> _previews = new();

    /// <summary>Сколько карточек превью помещается в пузырь (лимит и у загрузки).</summary>
    public const int MaxPreviews = 3;

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
        NotifyChipSlots();
    }

    public void ClearPreviews()
    {
        if (_previews.Count == 0)
        {
            return;
        }

        _previews.Clear();
        NotifyPreviewSlots();
        NotifyChipSlots();
    }

    // Слоты превью и чипов: BindableLayout с DataTemplate внутри пузыря в этой версии
    // MAUI ведёт себя ненадёжно (пустые карточки вместо текста), поэтому рисуются
    // три фиксированных слота — как сетка альбома.

    private LinkPreview? SlotPreview(int index) => index < _previews.Count ? _previews[index] : null;

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

    public string? HrefForPreview(int index) => SlotPreview(index)?.Url;

    /// <summary>Скрытые markdown-цели, ещё не раскрытые превью-карточкой.</summary>
    private LinkChip? SlotChip(int index)
    {
        var visible = VisibleChips();
        return index < visible.Count ? visible[index] : null;
    }

    private List<LinkChip> VisibleChips() => [.. _hiddenLinks.Where(c => !HasPreviewFor(c.Href))];

    public bool HasHiddenChips => _hiddenLinks.Any(c => !HasPreviewFor(c.Href));
    public bool ShowChip0 => SlotChip(0) is not null;
    public string Chip0Text => SlotChip(0)?.Display ?? string.Empty;
    public bool ShowChip1 => SlotChip(1) is not null;
    public string Chip1Text => SlotChip(1)?.Display ?? string.Empty;
    public bool ShowChip2 => SlotChip(2) is not null;
    public string Chip2Text => SlotChip(2)?.Display ?? string.Empty;

    public string? HrefForChip(int index) => SlotChip(index)?.Href;

    private void NotifyPreviewSlots()
    {
        Notify(nameof(HasLinkPreviews));
        for (var i = 0; i < MaxPreviews; i++)
        {
            Notify($"ShowPreview{i}");
            Notify($"Preview{i}HasImage");
            Notify($"Preview{i}Image");
            Notify($"Preview{i}Title");
            Notify($"Preview{i}Description");
            Notify($"Preview{i}Domain");
        }
    }

    private void NotifyChipSlots()
    {
        Notify(nameof(HasHiddenChips));
        for (var i = 0; i < MaxPreviews; i++)
        {
            Notify($"ShowChip{i}");
            Notify($"Chip{i}Text");
        }
    }

    public string FileName
    {
        get => _fileName;
        private set => Set(ref _fileName, value);
    }

    public string ContentType
    {
        get => _contentType;
        private set
        {
            if (Set(ref _contentType, value))
            {
                Notify(nameof(IsGif));
            }
        }
    }

    /// <summary>GIF-изображение — показывается бейдж и анимация.</summary>
    public bool IsGif => _contentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase);

    public long MediaSize
    {
        get => _mediaSize;
        private set
        {
            if (Set(ref _mediaSize, value))
            {
                Notify(nameof(FileSizeText));
            }
        }
    }

    public bool MediaDownloaded
    {
        get => _mediaDownloaded;
        private set
        {
            if (Set(ref _mediaDownloaded, value))
            {
                Notify(nameof(IsFilePending));
                Notify(nameof(ActionButtonText));
            }
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        private set => Set(ref _isPinned, value);
    }

    public bool IsEdited
    {
        get => _isEdited;
        private set => Set(ref _isEdited, value);
    }

    public string? EditedText
    {
        get => _editedText;
        private set => Set(ref _editedText, value);
    }

    /// <summary>Одноразовое сообщение: исчезает после просмотра получателем.</summary>
    public bool IsOnce { get; set; }

    public bool IsForwarded
    {
        get => _isForwarded;
        private set
        {
            if (Set(ref _isForwarded, value))
            {
                Notify(nameof(ForwardedText));
            }
        }
    }

    private string? _forwardedFrom;

    /// <summary>Автор исходного сообщения, если оно переслано (логин).</summary>
    public string? ForwardedFrom
    {
        get => _forwardedFrom;
        private set
        {
            if (Set(ref _forwardedFrom, value))
            {
                Notify(nameof(ForwardedText));
            }
        }
    }

    /// <summary>Метка пересылки: «↪ Переслано от anna» или просто «↪ Переслано».</summary>
    public string ForwardedText => string.IsNullOrWhiteSpace(_forwardedFrom)
        ? "↪ Переслано"
        : $"↪ Переслано от {_forwardedFrom}";


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

    /// <summary>Текущая позиция воспроизведения.</summary>
    public double PlaybackPositionSeconds => _playbackPositionSeconds;

    /// <summary>Полная длительность записи. 0, пока длительность неизвестна.</summary>
    public double PlaybackDurationSeconds => _playbackDurationSeconds;

    public string PlaybackTimeText => FormatPlaybackTime(_playbackPositionSeconds);

    public string PlaybackDurationText =>
        _playbackDurationSeconds > 0 ? FormatPlaybackTime(_playbackDurationSeconds) : string.Empty;

    /// <summary>Позиция и длительность в формате 0:12 / 0:48.</summary>
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

        Notify(nameof(PlaybackProgress));
        Notify(nameof(PlaybackPositionSeconds));
        Notify(nameof(PlaybackDurationSeconds));
        Notify(nameof(PlaybackTimeText));
        Notify(nameof(PlaybackDurationText));
        Notify(nameof(PlaybackTimerText));
    }

    /// <summary>Сбрасывает воспроизведение в начало, сохраняя известную длительность.</summary>
    public void ResetPlayback()
    {
        IsPlaying = false;
        UpdatePlayback(0, _playbackDurationSeconds);
    }

    public bool IsDraft
    {
        get => _isDraft;
        private set => Set(ref _isDraft, value);
    }

    public string? ReplyToText
    {
        get => _replyToText;
        private set
        {
            if (Set(ref _replyToText, value))
            {
                Notify(nameof(HasReplyQuote));
                Notify(nameof(ReplyQuoteText));
            }
        }
    }

    public bool HasReplyQuote => !string.IsNullOrEmpty(_replyToText);
    public string ReplyQuoteText => _replyToText ?? string.Empty;

    private string _replyAuthorDisplay = string.Empty;

    /// <summary>
    /// Автор, на которого ссылается цитата ответа: для пересланного — исходный
    /// автор, иначе отправитель. Так цитата указывает на того, чьё сообщение.
    /// </summary>
    public string ReplyAuthorName => IsForwarded && !string.IsNullOrWhiteSpace(_forwardedFrom)
        ? _forwardedFrom!
        : SenderDisplay;

    /// <summary>Подпись автора в цитате: «↩ anna» или «↩ Вы»; без автора — «↩ Ответ».</summary>
    public string ReplyQuoteAuthorText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(_replyAuthorDisplay) ? _replyAuthorFallback : _replyAuthorDisplay;
            return string.IsNullOrWhiteSpace(name) ? "↩ Ответ" : $"↩ {name}";
        }
    }

    private string _replyAuthorFallback = string.Empty;

    /// <summary>
    /// Автор цитаты для старых ответов, сохранённых без имени: берётся у цитируемого
    /// сообщения, если оно есть в загруженной переписке.
    /// </summary>
    public void SetReplyAuthorFallback(string author, string ownName)
    {
        var display = string.IsNullOrWhiteSpace(author)
            ? string.Empty
            : string.Equals(author, ownName, StringComparison.OrdinalIgnoreCase) ? Loc.T("rs_028", "Вы") : author;
        if (string.Equals(display, _replyAuthorFallback, StringComparison.Ordinal))
        {
            return;
        }

        _replyAuthorFallback = display;
        Notify(nameof(ReplyQuoteAuthorText));
    }

    /// <summary>Идентификатор сообщения, на которое отвечают.</summary>
    public string? ReplyToMessageId { get; private set; }

    /// <summary>Имя вложения исходного сообщения (если отвечают на медиа).</summary>
    public string? ReplyToFileName { get; private set; }

    public string DeliveryStatus
    {
        get => _deliveryStatus;
        set
        {
            if (Set(ref _deliveryStatus, value))
            {
                Notify(nameof(DeliveryColor));
            }
        }
    }

    public string SenderDisplay
    {
        get => _senderDisplay;
        private set
        {
            if (Set(ref _senderDisplay, value))
            {
                Notify(nameof(SenderInitial));
                Notify(nameof(SenderAvatarColor));
                Notify(nameof(PinnedAuthorText));
            }
        }
    }

    public string? SenderAvatarBase64
    {
        get => _senderAvatarBase64;
        private set
        {
            if (Set(ref _senderAvatarBase64, value))
            {
                _senderAvatarImageSource = string.IsNullOrWhiteSpace(value)
                    ? null
                    : ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(value)));
                Notify(nameof(SenderAvatarImageSource));
            }
        }
    }

    public ImageSource? SenderAvatarImageSource => _senderAvatarImageSource;

    public string? Username => _username;
    public bool IsArchived
    {
        get => _isArchived;
        set { _isArchived = value; }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool MatchesSearch
    {
        // Голоса — внутренние данные опроса, в ленте не показываются.
        get => _matchesSearch && Kind != SecureChat.Core.Contracts.MessageKinds.PollVote;
        set => Set(ref _matchesSearch, value);
    }

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

    public bool ShowAvatar
    {
        get => _showAvatar;
        set => Set(ref _showAvatar, value);
    }

    public ImageSource? ImageSource => _imageSource;

    public bool IsImageReady => IsImage && _imageSource is not null;
    public bool IsImagePending => IsImage && _imageSource is null;
    public bool IsFilePending => IsFile && !_mediaDownloaded;
    public string ActionButtonText => _mediaDownloaded ? "Открыть" : "Загрузить";
    public string FileSizeText => FormatSize(_mediaSize);

    public ObservableCollection<ReactionChipVm> ReactionChips { get; } = new();
    public bool HasReactions => ReactionChips.Count > 0;

    public Color MetaColor => Res("TextMuted");

    public Color DeliveryColor => DeliveryStatus switch
    {
        "Доставлено" => Res("Success"),
        "Прочитано" => Res("Accent"),
        "Отправлено" => Res("TextMuted"),
        "Отправка" => Res("TextMuted"),
        "В очереди" => Res("Warning"),
        "Ошибка" or "Не отправлено" => Res("Danger"),
        _ => Res("TextDisabled")
    };

    public Style TextStyle => StyleRes(IsMine ? "MessageTextMe" : "MessageTextThem");
    public Style BubbleStyle => StyleRes(IsMine ? "MessageBubbleMe" : "MessageBubbleThem");

    public string SenderInitial =>
        string.IsNullOrEmpty(_senderDisplay) ? "?" : _senderDisplay[..1].ToUpperInvariant();

    public Color SenderAvatarColor => AvatarColorFor(_senderDisplay);

    public string PinnedAuthorText => IsMine ? Loc.T("rs_028", "Вы") : _senderDisplay;

    public void SetImage(ImageSource? source)
    {
        if (Set(ref _imageSource, source, nameof(ImageSource)))
        {
            Notify(nameof(IsImageReady));
            Notify(nameof(IsImagePending));
        }
    }

    /// <summary>Байты расшифрованного GIF (заполняются после скачивания).</summary>
    public byte[]? GifBytes => _gifBytes;

    /// <summary>Анимированный GIF: по content-type или по расширению файла.</summary>
    public bool IsAnimatedGif =>
        _contentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase) ||
        _fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Обычная статичная картинка (не GIF).</summary>
    public bool IsStaticImage => IsImage && !IsAnimatedGif;

    /// <summary>Цвет подложки под GIF (в тон пузыря), HTML #RRGGBB.</summary>
    public string GifSurfaceHex => ResolveBubbleHex(IsMine);

    /// <summary>Картинка по байтам: ImageSource + хранение GIF-байтов для GifWebView.</summary>
    public void SetImageBytes(byte[] bytes)
    {
        SetImage(ImageSource.FromStream(() => new MemoryStream(bytes)));
        if (IsAnimatedGif)
        {
            _gifBytes = bytes;
            Notify(nameof(GifBytes));
        }
    }

    private static string ResolveBubbleHex(bool mine)
    {
        try
        {
            var style = StyleRes(mine ? "MessageBubbleMe" : "MessageBubbleThem");
            var setter = style.Setters.FirstOrDefault(item =>
                item.Property == Microsoft.Maui.Controls.Border.BackgroundColorProperty);

            if (setter?.Value is Color color)
            {
                return HtmlHex(color);
            }

            if (setter?.Value is SolidColorBrush brush)
            {
                return HtmlHex(brush.Color);
            }
        }
        catch
        {
            // Нет стиля — нейтральная подложка ниже.
        }

        return mine ? "#2B5278" : "#182533";
    }

    private static string HtmlHex(Color color) =>
        $"#{(int)Math.Round(color.Red * 255):X2}" +
        $"{(int)Math.Round(color.Green * 255):X2}" +
        $"{(int)Math.Round(color.Blue * 255):X2}";

    public void Update(StoredMessage message, string ownName, string peerName)
    {
        Text = message.Text;
        FileName = message.FileName;
        ContentType = message.ContentType;
        MediaSize = message.MediaSize;
        MediaDownloaded = message.MediaDownloaded;
        IsPinned = message.IsPinned;
        IsEdited = message.IsEdited;
        EditedText = message.EditedText;
        IsForwarded = message.IsForwarded;
        ForwardedFrom = message.ForwardedFrom;
        var replyAuthor = message.ReplyToSender ?? string.Empty;
        _replyAuthorDisplay = replyAuthor.Length == 0
            ? string.Empty
            : string.Equals(replyAuthor, ownName, StringComparison.OrdinalIgnoreCase)
                ? Loc.T("rs_028", "Вы")
                : replyAuthor;
        Notify(nameof(ReplyQuoteAuthorText));
        IsDraft = message.IsDraft;
        ReplyToText = message.ReplyToText;
        ReplyToMessageId = message.ReplyToMessageId;
        ReplyToFileName = message.ReplyToFileName;
        DeliveryStatus = message.DeliveryStatus;
        SenderDisplay = IsMine
            ? ownName
            : string.IsNullOrEmpty(message.SenderUsername) ? peerName : message.SenderUsername;
        SenderAvatarBase64 = message.SenderAvatarBase64;
        _username = peerName;
        SetAlbum(message.AlbumId, message.AlbumIndex, message.AlbumCount);
        SetReactions(LocalStore.ReadReactions(message.ReactionsJson), ownName);
    }

    private void SetReactions(List<Reaction> reactions, string ownName)
    {
        ReactionChips.Clear();
        foreach (var group in reactions.GroupBy(item => item.Emoji))
        {
            ReactionChips.Add(new ReactionChipVm
            {
                Emoji = group.Key,
                Count = group.Count(),
                IsMine = group.Any(item => string.Equals(item.UserId, ownName, StringComparison.OrdinalIgnoreCase))
            });
        }

        Notify(nameof(HasReactions));
    }

    private DateTimeOffset ToLocal() =>
        DateTimeOffset.FromUnixTimeMilliseconds(SentAtUnixMs).ToLocalTime();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Notify(name);
        return true;
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Color Res(string key) => (Color)Application.Current!.Resources[key];

    private static Style StyleRes(string key) => (Style)Application.Current!.Resources[key];

    private static Color AvatarColorFor(string name)
    {
        var sum = 0;
        foreach (var ch in name)
        {
            sum += ch;
        }

        return Color.FromArgb(AvatarPalette[sum % AvatarPalette.Length]);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / (1024.0 * 1024):0.#} МБ"
    };

    private static string FormatPlaybackTime(double seconds)
    {
        var total = (int)Math.Floor(Math.Max(0, seconds));
        var time = TimeSpan.FromSeconds(total);
        return total >= 3600 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
