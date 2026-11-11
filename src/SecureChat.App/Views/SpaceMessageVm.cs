using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SecureChat.Core.Contracts;

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
    private ImageSource? _imageSource;
    private FormattedString? _textFormatted;
    private byte[]? _gifBytes;
    private readonly bool _isChannel;
    private int _commentsCount;

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
    public required string Sender { get; init; }
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

        var formatted = new FormattedString();
        foreach (var (chunk, mention) in SpaceMentions.Split(_text, RenderMentions()))
        {
            var span = new Span { Text = chunk };

            if (mention is not null)
            {
                // Как в Telegram: полужирное упоминание с подчёркиванием.
                // В своём пузыре фон акцентный — текст упоминания там светлый,
                // иначе сливается с фоном пузыря.
                span.TextColor = ResolveMentionColor();
                span.TextDecorations = TextDecorations.Underline;
                span.FontAttributes = FontAttributes.Bold;

                var target = this;
                var recognizer = new TapGestureRecognizer { CommandParameter = mention };
                recognizer.Tapped += (_, _) => target.MentionTapped?.Invoke(target, mention);
                span.GestureRecognizers.Add(recognizer);
            }

            formatted.Spans.Add(span);
        }

        _textFormatted = formatted;
        HandleTextFormatted(formatted);
    }

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

    private void HandleTextFormatted(FormattedString formatted)
    {
        _textFormatted = formatted;
        OnPropertyChanged(nameof(TextFormatted));
    }

    private static Color ResolveAccent() =>
        (Application.Current?.Resources["Accent"] as Color) ?? Color.FromArgb("#0088CC");

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
    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>Текст с подсветкой упоминаний (для Label FormattedText); null — обычный текст.</summary>
    public FormattedString? TextFormatted => _textFormatted;

    /// <summary>Число известных клиенту комментариев к посту.</summary>
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
    public bool HasReply => !string.IsNullOrEmpty(ReplyToText);
    public bool HasReactions => ReactionChips.Count > 0;
    public bool ShowAvatar => !IsMine;
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

    public static SpaceMessageVm From(
        SpaceMessageDto source,
        SpaceMessagePayload payload,
        string myUserId,
        bool canModerate,
        bool isChannel = false)
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
            TimeText = DateTimeOffset.FromUnixTimeMilliseconds(source.SentAtUnixMs)
                .ToLocalTime()
                .ToString("HH:mm"),
            BubbleStyle = (Style)Application.Current!.Resources[mine ? "MessageBubbleMe" : "MessageBubbleThem"],
            TextStyle = (Style)Application.Current!.Resources[mine ? "MessageTextMe" : "MessageTextThem"],
            MetaColor = mine ? Color.FromArgb("#BFDBFE") : Color.FromArgb("#94A3B8")
        };

        vm.SenderNameColor = ResolveSenderNameColor(mine);
        vm._text = payload.Text;
        vm._isEdited = source.EditedAtUnixMs > 0;
        vm._isPinned = source.IsPinned;
        vm._canModerate = canModerate;
        vm.IsAnimatedGif = IsGifMedia(payload.Media);
        vm.GifSurfaceHex = ResolveBubbleHex(mine);
        vm.RebuildTextFormatted();
        vm.SetReactions(source.Reactions, myUserId);
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
}
