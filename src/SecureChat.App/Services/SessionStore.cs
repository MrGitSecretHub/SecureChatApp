using System.Security.Cryptography;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;
using SecureChat.Core.Serialization;

namespace SecureChat.App.Services;

public sealed class SavedSession
{
    public required string Token { get; set; }
    public required string ServerUrl { get; set; }
    public required string Username { get; set; }

    /// <summary>Идентификатор аккаунта (для восстановления профиля без сервера).</summary>
    public string? UserId { get; set; }
}

/// <summary>Долговременное хранение сессии и ключей идентичности.</summary>
public sealed class SessionStore
{
    private const string SessionKey = "session";
    private const string IdentityPrefix = "identity:";
    private const string ProfilePrefix = "profile:";

    private readonly SecretStore _secrets;

    public SessionStore(SecretStore secrets) => _secrets = secrets;

    public async Task<SavedSession?> LoadSessionAsync()
    {
        var json = await _secrets.GetAsync(SessionKey);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : ChatJson.Deserialize<SavedSession>(json);
    }

    public Task SaveSessionAsync(SavedSession session) =>
        _secrets.SetAsync(SessionKey, ChatJson.Serialize(session));

    /// <summary>Сохраняет профиль для входа в офлайн-режиме без сервера.</summary>
    public Task SaveProfileAsync(UserProfileDto profile) =>
        _secrets.SetAsync(
            ProfilePrefix + profile.Username.ToLowerInvariant(),
            ChatJson.Serialize(profile));

    public async Task<UserProfileDto?> LoadProfileAsync(string username)
    {
        var json = await _secrets.GetAsync(ProfilePrefix + username.ToLowerInvariant());
        return string.IsNullOrWhiteSpace(json)
            ? null
            : ChatJson.Deserialize<UserProfileDto>(json);
    }

    public Task ClearProfileAsync(string username) =>
        _secrets.RemoveAsync(ProfilePrefix + username.ToLowerInvariant());

    public async Task ClearSessionAsync()
    {
        var saved = await LoadSessionAsync();
        if (saved is not null)
        {
            await ClearProfileAsync(saved.Username);
        }

        await _secrets.RemoveAsync(SessionKey);
    }

    public async Task<IdentityKeys?> LoadIdentityAsync(string username)
    {
        var json = await _secrets.GetAsync(IdentityPrefix + username.ToLowerInvariant());
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var identity = ChatJson.Deserialize<IdentityKeys>(json);
        identity.Validate();
        return identity;
    }

    public Task SaveIdentityAsync(string username, IdentityKeys identity) =>
        _secrets.SetAsync(
            IdentityPrefix + username.ToLowerInvariant(),
            ChatJson.Serialize(identity));

    public Task ClearAllAsync() => _secrets.ClearAsync();
}
