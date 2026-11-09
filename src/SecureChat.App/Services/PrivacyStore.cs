using System.Text.Json;
using SecureChat.Core.Crypto;

namespace SecureChat.App.Services;

/// <summary>
/// Настройки конфиденциальности устройства. Хранятся в зашифрованном SecretStore
/// и применяются при запуске, при отправке статусов и при приёме сообщений.
/// </summary>
public static class PrivacyStore
{
    private const string BlockedKey = "privacy.blocked";
    private const string PasscodeSaltKey = "privacy.passcode.salt";
    private const string PasscodeHashKey = "privacy.passcode.hash";

    public static async Task LoadAsync(AppSession session, SecretStore secrets)
    {
        session.HideOnlineStatus = await ReadFlagAsync(secrets, "privacy.hideOnline", false);
        session.HideReadReceipts = await ReadFlagAsync(secrets, "privacy.hideRead", false);
        session.ShowTypingStatus = await ReadFlagAsync(secrets, "privacy.typing", true);
        session.ShowNotificationPreview = await ReadFlagAsync(secrets, "privacy.preview", true);
        session.SoundsEnabled = await ReadFlagAsync(secrets, "privacy.sounds", true);
        session.RingtoneId = Ringtones.Normalize(await secrets.GetAsync("privacy.ringtone"));
        session.AcceptOnlyFromContacts = await ReadFlagAsync(secrets, "privacy.contactsOnly", false);

        var blockedJson = await secrets.GetAsync(BlockedKey);
        session.BlockedUsernames = string.IsNullOrWhiteSpace(blockedJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(blockedJson) ?? new List<string>();
    }

    public static async Task SaveAsync(AppSession session, SecretStore secrets)
    {
        await WriteFlagAsync(secrets, "privacy.hideOnline", session.HideOnlineStatus);
        await WriteFlagAsync(secrets, "privacy.hideRead", session.HideReadReceipts);
        await WriteFlagAsync(secrets, "privacy.typing", session.ShowTypingStatus);
        await WriteFlagAsync(secrets, "privacy.preview", session.ShowNotificationPreview);
        await WriteFlagAsync(secrets, "privacy.sounds", session.SoundsEnabled);
        await secrets.SetAsync("privacy.ringtone", session.RingtoneId);
        await WriteFlagAsync(secrets, "privacy.contactsOnly", session.AcceptOnlyFromContacts);
        await secrets.SetAsync(BlockedKey, JsonSerializer.Serialize(session.BlockedUsernames));
    }

    public static async Task<bool> HasPasscodeAsync(SecretStore secrets) =>
        !string.IsNullOrEmpty(await secrets.GetAsync(PasscodeHashKey));

    public static async Task SetPasscodeAsync(SecretStore secrets, string passcode)
    {
        var (salt, hash) = await Task.Run(() => PasscodeHasher.Create(passcode));
        await secrets.SetAsync(PasscodeSaltKey, salt);
        await secrets.SetAsync(PasscodeHashKey, hash);
    }

    public static async Task RemovePasscodeAsync(SecretStore secrets)
    {
        await secrets.RemoveAsync(PasscodeSaltKey);
        await secrets.RemoveAsync(PasscodeHashKey);
    }

    public static async Task<bool> VerifyPasscodeAsync(SecretStore secrets, string passcode)
    {
        var salt = await secrets.GetAsync(PasscodeSaltKey);
        var hash = await secrets.GetAsync(PasscodeHashKey);
        if (salt is null || hash is null)
        {
            return false;
        }

        return await Task.Run(() => PasscodeHasher.Verify(passcode, salt, hash));
    }

    private static async Task<bool> ReadFlagAsync(SecretStore secrets, string key, bool fallback)
    {
        var value = await secrets.GetAsync(key);
        return value is null ? fallback : value == "1";
    }

    private static Task WriteFlagAsync(SecretStore secrets, string key, bool value) =>
        secrets.SetAsync(key, value ? "1" : "0");
}
