using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SecureChat.Core.Crypto;

namespace SecureChat.App.Services;

/// <summary>Регистрация, вход и восстановление сохранённой сессии.</summary>
public sealed class AccountService
{
    private static readonly Regex UsernamePattern =
        new("^[A-Za-z0-9_]{3,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ApiClient _apiClient;
    private readonly SessionStore _sessionStore;
    private readonly AppSession _session;
    private readonly LocalStore _localStore;

    public AccountService(
        ApiClient apiClient,
        SessionStore sessionStore,
        AppSession session,
        LocalStore localStore)
    {
        _apiClient = apiClient;
        _sessionStore = sessionStore;
        _session = session;
        _localStore = localStore;
    }

    public async Task RegisterAsync(string serverUrl, string username, string? email, string password)
    {
        Validate(serverUrl, username, password);

        if (email is not null && !IsValidEmail(email.Trim()))
        {
            throw new ArgumentException("Некорректный адрес электронной почты.");
        }

        var identity = IdentityKeys.Create();
        var auth = await _apiClient.RegisterAsync(
            serverUrl,
            username.Trim(),
            email is null ? null : email.Trim(),
            password,
            identity);

        await _sessionStore.SaveIdentityAsync(username, identity);

        _session.SignIn(auth, identity, serverUrl);
        await _sessionStore.SaveProfileAsync(auth.User);
        await _sessionStore.SaveSessionAsync(new SavedSession
        {
            Token = auth.Token,
            ServerUrl = _session.ServerUrl,
            Username = auth.User.Username,
            UserId = auth.User.Id
        });
    }

    public async Task LoginAsync(string serverUrl, string username, string password)
    {
        Validate(serverUrl, username, password);

        var auth = await _apiClient.LoginAsync(serverUrl, username.Trim(), password);
        var identity = await _sessionStore.LoadIdentityAsync(auth.User.Username);

        if (identity is null)
        {
            throw new InvalidOperationException(
                "На этом устройстве нет ключей для этого аккаунта. " +
                "Вход возможен только с устройства, где была создана идентичность.");
        }

        _session.SignIn(auth, identity, serverUrl);
        await _sessionStore.SaveProfileAsync(auth.User);
        await _sessionStore.SaveSessionAsync(new SavedSession
        {
            Token = auth.Token,
            ServerUrl = _session.ServerUrl,
            Username = auth.User.Username,
            UserId = auth.User.Id
        });
    }

    public async Task<bool> TryRestoreAsync()
    {
        var saved = await _sessionStore.LoadSessionAsync();
        if (saved is null)
        {
            return false;
        }

        var identity = await _sessionStore.LoadIdentityAsync(saved.Username);
        if (identity is null)
        {
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var profile = await _apiClient.GetMeAsync(
                saved.ServerUrl,
                saved.Token,
                timeout.Token);

            _session.SignIn(
                new Core.Contracts.AuthResponse { Token = saved.Token, User = profile },
                identity,
                saved.ServerUrl);

            await _sessionStore.SaveProfileAsync(profile);
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Токен отозван — офлайн-профиль тоже больше не актуален.
            await _sessionStore.ClearSessionAsync();
            return false;
        }
        catch
        {
            // Сервер недоступен: входим по сохранённому профилю и работаем
            // с локальной историей. Связь восстановится в фоне.
            var cached = await _sessionStore.LoadProfileAsync(saved.Username)
                ?? await BuildOfflineProfileAsync(saved, identity);

            if (cached is null)
            {
                return false;
            }

            await _sessionStore.SaveProfileAsync(cached);
            _session.SignIn(
                new Core.Contracts.AuthResponse { Token = saved.Token, User = cached },
                identity,
                saved.ServerUrl);
            _session.RestoredOffline = true;
            return true;
        }
    }

    /// <summary>
    /// Собирает профиль для офлайн-входа из сохранённых данных, если кэш
    /// профиля ещё не успел появиться (например, после обновления приложения).
    /// </summary>
    private async Task<Core.Contracts.UserProfileDto?> BuildOfflineProfileAsync(
        SavedSession saved,
        IdentityKeys identity)
    {
        var userId = saved.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            // В старых сохранённых сессиях нет поля UserId, но JWT содержит
            // идентификатор аккаунта в claim "sub" — читаем его без проверки
            // подписи, только для локального профиля.
            userId = TryReadUserIdFromToken(saved.Token);
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                await _localStore.InitializeAsync(saved.Username);
                var self = (await _localStore.GetConversationsAsync())
                    .FirstOrDefault(item => string.Equals(
                        item.Username,
                        ChatManager.SelfChatUsername,
                        StringComparison.OrdinalIgnoreCase));
                userId = self?.UserId;
            }
            catch
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return new Core.Contracts.UserProfileDto
        {
            Id = userId,
            Username = saved.Username,
            EncryptionPublicKey = identity.EncryptionPublicKey,
            SigningPublicKey = identity.SigningPublicKey,
            Fingerprint = identity.Fingerprint,
            CreatedAt = DateTimeOffset.UtcNow,
            OnlineStatus = "Офлайн"
        };
    }

    private static string? TryReadUserIdFromToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');

            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload
            };

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("sub", out var sub)
                ? sub.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Меняет пароль аккаунта. Текущая сессия на этом устройстве остаётся активной.</summary>
    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            throw new ArgumentException("Введите текущий пароль.");
        }

        if (newPassword.Length < 8)
        {
            throw new ArgumentException("Новый пароль должен содержать не менее 8 символов.");
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            throw new ArgumentException("Новый пароль должен отличаться от текущего.");
        }

        await _apiClient.ChangePasswordAsync(
            _session.ServerUrl,
            RequireToken(),
            _session.Username,
            currentPassword,
            newPassword);
    }

    /// <summary>
    /// Удаляет аккаунт на сервере, затем стирает локальную историю, ключи
    /// устройства и сохранённую сессию. Действие необратимо.
    /// </summary>
    public async Task DeleteAccountAsync(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Введите пароль.");
        }

        await _apiClient.DeleteAccountAsync(
            _session.ServerUrl,
            RequireToken(),
            _session.Username,
            password);

        await _localStore.DeleteAllAsync();
        await _sessionStore.ClearAllAsync();
        _session.SignOut();
    }

    private string RequireToken() =>
        _session.Token
        ?? throw new InvalidOperationException("Сессия не активна. Войдите заново.");

    public async Task LogoutAsync()
    {
        await _sessionStore.ClearSessionAsync();
        _session.SignOut();
    }

    public static void Validate(string serverUrl, string username, string password)
    {
        if (!AppSession.IsValidServerUrl(serverUrl))
        {
            throw new ArgumentException(
                "Укажите адрес сервера: домен или IP с портом. Схему (http:// или https://) можно не писать.");
        }

        if (!UsernamePattern.IsMatch(username.Trim()))
        {
            throw new ArgumentException(
                "Имя пользователя: 3–32 символа, только латиница, цифры и «_».");
        }

        if (password.Length < 8)
        {
            throw new ArgumentException("Пароль должен содержать не менее 8 символов.");
        }
    }

    private static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value;
        }
        catch
        {
            return false;
        }
    }
}
