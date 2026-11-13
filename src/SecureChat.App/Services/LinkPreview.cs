namespace SecureChat.App.Services;

/// <summary>Результат извлечения Open Graph-метаданных ссылки.</summary>
public sealed class LinkPreview
{
    private ImageSource? _imageSource;

    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public required string Domain { get; init; }

    public ImageSource? ImageSource =>
        _imageSource ??= ImageUrl is not null && Uri.TryCreate(ImageUrl, UriKind.Absolute, out var uri)
            ? ImageSource.FromUri(uri)
            : null;

    /// <summary>Есть ли картинка: без неё карточка показывается только текстом.</summary>
    public bool HasImage => ImageSource is not null;
}
