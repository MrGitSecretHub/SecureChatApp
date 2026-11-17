using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Services;

public sealed class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ApiException(HttpStatusCode statusCode, string message) : base(message) =>
        StatusCode = statusCode;
}

    /// <summary>
    /// HTTP-клиент REST API сервера. Системный прокси отключён намеренно:
    /// сервер находится в локальной сети, а прокси (например, 127.0.0.1:10809
    /// в Windows) рвёт TLS-соединение, из-за чего появляется ошибка
    /// «The SSL connection could not be established».
    /// </summary>
    public sealed class ApiClient : IDisposable
    {
        private readonly HttpClient _http = new(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = ServerCertificatePin.Validate
            }
        })
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

    public ApiClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SecureChat/1.0");
    }

    public async Task<AuthResponse> RegisterAsync(
        string serverUrl,
        string username,
        string? email,
        string password,
        Core.Crypto.IdentityKeys identity,
        CancellationToken cancellationToken = default)
    {
        var request = new RegisterRequest
        {
            Username = username,
            Email = email,
            AuthSecret = Core.Crypto.AuthSecret.Derive(password, username),
            EncryptionPublicKey = identity.EncryptionPublicKey,
            SigningPublicKey = identity.SigningPublicKey
        };

        using var response = await _http.PostAsJsonAsync(
            BuildUri(serverUrl, "api/auth/register"),
            request,
            cancellationToken);

        return await ReadAsync<AuthResponse>(response, cancellationToken);
    }

    public async Task<AuthResponse> LoginAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var request = new LoginRequest
        {
            Username = username,
            AuthSecret = Core.Crypto.AuthSecret.Derive(password, username)
        };

        using var response = await _http.PostAsJsonAsync(
            BuildUri(serverUrl, "api/auth/login"),
            request,
            cancellationToken);

        return await ReadAsync<AuthResponse>(response, cancellationToken);
    }

    public async Task ChangePasswordAsync(
        string serverUrl,
        string token,
        string username,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var request = new ChangePasswordRequest
        {
            CurrentAuthSecret = Core.Crypto.AuthSecret.Derive(currentPassword, username),
            NewAuthSecret = Core.Crypto.AuthSecret.Derive(newPassword, username)
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(serverUrl, "api/auth/change-password"))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task DeleteAccountAsync(
        string serverUrl,
        string token,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var request = new DeleteAccountRequest
        {
            AuthSecret = Core.Crypto.AuthSecret.Derive(password, username)
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(serverUrl, "api/auth/delete-account"))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<UserProfileDto> GetUserAsync(
        string serverUrl,
        string token,
        string username,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(serverUrl, $"api/users/{Uri.EscapeDataString(username)}"));
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync<UserProfileDto>(response, cancellationToken);
    }

    public async Task<UserProfileDto> GetMeAsync(
        string serverUrl,
        string token,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(serverUrl, "api/users/me"));
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync<UserProfileDto>(response, cancellationToken);
    }

    /// <summary>Обновляет публичный профиль пользователя на сервере.</summary>
    public async Task<UserProfileDto> UpdateProfileAsync(
        string serverUrl,
        string token,
        string nickname,
        string bio,
        string avatarBase64,
        string statusMessage,
        CancellationToken cancellationToken = default)
    {
        var request = new UpdateProfileRequest
        {
            Nickname = nickname,
            Bio = bio,
            AvatarBase64 = avatarBase64,
            StatusMessage = statusMessage
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Put,
            BuildUri(serverUrl, "api/users/me/profile"))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(message, cancellationToken);
        return await ReadAsync<UserProfileDto>(response, cancellationToken);
    }

    public async Task<DateTimeOffset?> GetServerTimeAsync(        string serverUrl,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            BuildUri(serverUrl, "api/health"),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("serverTime", out var value)
            && value.TryGetDateTimeOffset(out var time)
            ? time
            : null;
    }

    public async Task UploadMediaAsync(
        string serverUrl,
        string token,
        string mediaId,
        string recipientId,
        byte[] ciphertext,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        string? spaceId = null)
    {
        var query = spaceId is null
            ? $"recipientId={Uri.EscapeDataString(recipientId)}"
            : $"spaceId={Uri.EscapeDataString(spaceId)}";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(serverUrl, $"api/media/{mediaId}?{query}"));
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        Stream contentStream = new MemoryStream(ciphertext, writable: false);
        if (progress is not null)
        {
            contentStream = new ProgressStream(contentStream, progress, ciphertext.Length);
        }

        request.Content = new StreamContent(contentStream);
        request.Content.Headers.ContentLength = ciphertext.Length;
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Поток, сообщающий о количестве переданных байт.</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly IProgress<long> _progress;
        private readonly long _total;
        private long _sent;

        public ProgressStream(Stream inner, IProgress<long> progress, long total)
        {
            _inner = inner;
            _progress = progress;
            _total = total;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _total;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _sent += read;
            _progress.Report(_sent);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(buffer, offset, count, cancellationToken).ContinueWith(
                t =>
                {
                    _sent += t.Result;
                    _progress.Report(_sent);
                    return t.Result;
                },
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public async Task<byte[]> DownloadMediaAsync(
        string serverUrl,
        string token,
        string mediaId,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(serverUrl, $"api/media/{Uri.EscapeDataString(mediaId)}"));
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        if (response.Content.Headers.ContentLength is > MediaLimits.MaxBytes)
        {
            throw new ApiException(response.StatusCode, "Вложение слишком большое.");
        }

        // Читаем поток частями, чтобы показывать прогресс и не держать
        // в памяти две копии файла.
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[128 * 1024];
        long downloaded = 0;

        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count <= 0)
            {
                break;
            }

            downloaded += count;
            if (downloaded > MediaLimits.MaxBytes)
            {
                throw new ApiException(response.StatusCode, "Вложение слишком большое.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            progress?.Report(downloaded);
        }

        return output.ToArray();
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message;
        try
        {
            var error = JsonSerializer.Deserialize<ApiError>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            message = error?.Error ?? "Ошибка сервера.";
        }
        catch (JsonException)
        {
            message = $"Ошибка сервера ({(int)response.StatusCode}).";
        }

        throw new ApiException(response.StatusCode, message);
    }

    private static Uri BuildUri(string serverUrl, string relative)
    {
        var normalized = AppSession.NormalizeServerUrl(serverUrl);
        if (!Uri.TryCreate(normalized + "/", UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("Некорректный адрес сервера.");
        }

        return new Uri(baseUri, relative);
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return JsonSerializer.Deserialize<T>(
                       body,
                       new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? throw new ApiException(response.StatusCode, "Пустой ответ сервера.");
        }

        string message;
        try
        {
            var error = JsonSerializer.Deserialize<ApiError>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            message = error?.Error ?? "Ошибка сервера.";
        }
        catch (JsonException)
        {
            message = $"Ошибка сервера ({(int)response.StatusCode}).";
        }

        throw new ApiException(response.StatusCode, message);
    }

    public void Dispose() => _http.Dispose();
}
