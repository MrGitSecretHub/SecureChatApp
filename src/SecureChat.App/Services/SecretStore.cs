using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecureChat.App.Services;

/// <summary>
/// Шифрованное хранилище секретов устройства.
/// Windows — DPAPI (CurrentUser), Android — AES-GCM с ключом в Android Keystore.
/// Файл с зашифрованными значениями лежит в приватной папке приложения.
/// </summary>
public sealed partial class SecretStore
{
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("SecureChat.SecretStore.v1"));

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, string>? _values;

    private static string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, "securechat.secrets.json");

    public async Task<string?> GetAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var values = await LoadAsync();
            return values.TryGetValue(key, out var value) ? value : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            var values = await LoadAsync();
            values[key] = value;
            await SaveAsync(values);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            var values = await LoadAsync();
            if (values.Remove(key))
            {
                await SaveAsync(values);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync()
    {
        if (_values is not null)
        {
            return _values;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(FilePath))
        {
            var protectedValues =
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllTextAsync(FilePath))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var pair in protectedValues)
            {
                try
                {
                    var protectedBytes = Convert.FromBase64String(pair.Value);
                    var plainBytes = Unprotect(protectedBytes);
                    values[pair.Key] = Encoding.UTF8.GetString(plainBytes);
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
                catch
                {
                    // Одна повреждённая запись не должна делать всё хранилище
                    // нечитаемым — пропускаем её, остальные ключи остаются доступны.
                }
            }
        }

        _values = values;
        return values;
    }

    private async Task SaveAsync(Dictionary<string, string> values)
    {
        var protectedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var plainBytes = Encoding.UTF8.GetBytes(pair.Value);
            protectedValues[pair.Key] = Convert.ToBase64String(Protect(plainBytes));
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(
            tempPath,
            JsonSerializer.Serialize(protectedValues));
        File.Move(tempPath, FilePath, overwrite: true);
    }

    private partial byte[] Protect(byte[] plaintext);

    private partial byte[] Unprotect(byte[] protectedData);
}
