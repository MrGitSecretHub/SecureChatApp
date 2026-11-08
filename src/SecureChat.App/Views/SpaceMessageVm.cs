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

    public required string Id { get; init; }
    public required string SenderId { get; init; }
    public required string Sender { get; init; }
    public bool IsMine { get; init; }
    public string Kind { get; init; } = "text";
    public SpaceMediaAttachment? Media { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? ReplyToText { get; init; }
    public bool IsForwarded { get; init; }
    public string TimeText { get; init; } = string.Empty;
    public Style? BubbleStyle { get; init; }
    public Style? TextStyle { get; init; }
    public Color MetaColor { get; init; } = Colors.Gray;
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
        }
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

    public bool IsImageReady => ImageSource is not null;
    public bool IsImagePending => IsImage && ImageSource is null;
    public bool HasText => !string.IsNullOrEmpty(Text);
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
        bool canModerate)
    {
        var mine = string.Equals(source.SenderId, myUserId, StringComparison.Ordinal);
        var vm = new SpaceMessageVm
        {
            Id = source.Id,
            SenderId = source.SenderId,
            Sender = source.SenderUsername,
            IsMine = mine,
            Kind = payload.Kind,
            Media = payload.Media,
            ReplyToMessageId = payload.ReplyToMessageId,
            ReplyToText = payload.ReplyToText,
            IsForwarded = payload.IsForwarded,
            TimeText = DateTimeOffset.FromUnixTimeMilliseconds(source.SentAtUnixMs)
                .ToLocalTime()
                .ToString("HH:mm"),
            BubbleStyle = (Style)Application.Current!.Resources[mine ? "MessageBubbleMe" : "MessageBubbleThem"],
            TextStyle = (Style)Application.Current!.Resources[mine ? "MessageTextMe" : "MessageTextThem"],
            MetaColor = mine ? Color.FromArgb("#BFDBFE") : Color.FromArgb("#94A3B8")
        };

        vm._text = payload.Text;
        vm._isEdited = source.EditedAtUnixMs > 0;
        vm._isPinned = source.IsPinned;
        vm._canModerate = canModerate;
        vm.SetReactions(source.Reactions, myUserId);
        return vm;
    }

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
