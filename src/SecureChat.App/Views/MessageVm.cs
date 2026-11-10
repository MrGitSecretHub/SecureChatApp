using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SecureChat.App.Services;
using SecureChat.Core.Contracts;

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

    public MessageVm(StoredMessage message, string ownName, string peerName)
    {
        Id = message.Id;
        Kind = string.IsNullOrEmpty(message.Kind) ? MessageKinds.Text : message.Kind;
        IsMine = message.IsMine;
        SentAtUnixMs = message.SentAtUnixMs;
        Update(message, ownName, peerName);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Нажали на @упоминание в тексте: второй аргумент — логин упомянутого.</summary>
    public event Action<MessageVm, string>? MentionTapped;

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
    public bool ShowText => Kind == MessageKinds.Text;
    public bool CanOpenFile => IsFile;

    public string Text
    {
        get => _text;
        private set
        {
            if (Set(ref _text, value))
            {
                Notify(nameof(DisplayText));
                RebuildMentions();
                RebuildUrls(value);
            }
        }
    }

    /// <summary>Текст с выделенными @упоминаниями (null — показываем обычный DisplayText).</summary>
    public FormattedString? TextFormatted => _textFormatted;

    /// <summary>Показывать простой текст — когда упоминаний не нашлось.</summary>
    public bool ShowPlainText => ShowText && _textFormatted is null;

    /// <summary>Показывать текст с подсветкой упоминаний.</summary>
    public bool ShowRichText => ShowText && _textFormatted is not null;

    /// <summary>
    /// Подсветка @упоминаний в тексте личного чата (как в Telegram: цвет темы
    /// и подчёркивание в чужих сообщениях, светлый текст в своих — пузырь
    /// акцентного цвета). Тап открывает профиль. Списка участников в ЛС нет,
    /// поэтому подсвечиваем любой логин после «@».
    /// </summary>
    private void RebuildMentions()
    {
        var mentions = SpaceMentions.AllTokens(_text);

        if (mentions.Count == 0)
        {
            if (_textFormatted is not null)
            {
                _textFormatted = null;
                Notify(nameof(TextFormatted));
                Notify(nameof(ShowPlainText));
                Notify(nameof(ShowRichText));
            }
            return;
        }

        var formatted = new FormattedString();
        foreach (var (chunk, mention) in SpaceMentions.Split(_text, mentions))
        {
            var span = new Span { Text = chunk };

            if (mention is not null)
            {
                span.TextColor = IsMine ? Res("AccentText") : Res("Accent");
                span.TextDecorations = TextDecorations.Underline;

                span.GestureRecognizers.Add(new TapGestureRecognizer
                {
                    Command = new Command<string>(name => MentionTapped?.Invoke(this, name)),
                    CommandParameter = mention
                });
            }

            formatted.Spans.Add(span);
        }

        _textFormatted = formatted;
        Notify(nameof(TextFormatted));
        Notify(nameof(ShowPlainText));
        Notify(nameof(ShowRichText));
    }

    private void RebuildUrls(string? text)
    {
        _urls.Clear();
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (Match m in UrlRegex.Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');
                if (url.Length > 0 && !_urls.Contains(url))
                {
                    _urls.Add(url);
                }
            }
        }

        Notify(nameof(Urls));
        Notify(nameof(HasUrls));
    }

    public string DisplayText => _text;

    private static readonly Regex UrlRegex = new(@"https?://\S+|www\.\S+", RegexOptions.Compiled);

    /// <summary>Ссылки, найденные в тексте сообщения.</summary>
    public IReadOnlyList<string> Urls => _urls;

    public bool HasUrls => _urls.Count > 0;

    private LinkPreview? _linkPreview;

    /// <summary>Превью первой ссылки (если включено в настройках и загружено).</summary>
    public LinkPreview? LinkPreview
    {
        get => _linkPreview;
        set
        {
            if (Set(ref _linkPreview, value))
            {
                Notify(nameof(ShowUrlChips));
            }
        }
    }

    public bool HasLinkPreview => _linkPreview is not null;

    /// <summary>Показывать сырые ссылки-чипы (скрыты, когда есть превью).</summary>
    public bool ShowUrlChips => HasUrls && _linkPreview is null;

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

    public bool IsForwarded
    {
        get => _isForwarded;
        private set => Set(ref _isForwarded, value);
    }

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
        get => _matchesSearch;
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

    public string PinnedAuthorText => IsMine ? "Вы" : _senderDisplay;

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
