using System.Security.Cryptography;
using SecureChat.Core.Crypto;
using SecureChat.Core.Serialization;

namespace SecureChat.App.Services;

public sealed class KeyBackupService
{
    private readonly SessionStore _sessions;
    private readonly AppSession _session;

    public KeyBackupService(SessionStore sessions, AppSession session)
    {
        _sessions = sessions;
        _session = session;
    }

    public async Task<string> ExportAsync(string password)
    {
        var identity = _session.Identity ?? throw new InvalidOperationException("Нет ключей устройства.");
        var wrapper = new KeyBackupPayload
        {
            Username = _session.Username,
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Identity = identity
        };
        return KeyBackupCrypto.Encrypt(ChatJson.Serialize(wrapper), password);
    }

    public async Task<IdentityKeys> ImportAsync(string payload, string password, string expectedUsername)
    {
        var json = KeyBackupCrypto.Decrypt(payload, password);
        var wrapper = ChatJson.Deserialize<KeyBackupPayload>(json)
            ?? throw new CryptographicException("Бэкап пуст.");
        wrapper.Identity.Validate();
        if (!string.IsNullOrWhiteSpace(expectedUsername) &&
            !string.Equals(wrapper.Username, expectedUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("Бэкап от другого аккаунта.");
        }
        await _sessions.SaveIdentityAsync(expectedUsername, wrapper.Identity);
        return wrapper.Identity;
    }

    private sealed class KeyBackupPayload
    {
        public string Username { get; set; } = string.Empty;
        public long CreatedAtUnixMs { get; set; }
        public IdentityKeys Identity { get; set; } = null!;
    }
}
