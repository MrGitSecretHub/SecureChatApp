using System.Text.Json;

namespace SecureChat.App.Services;

/// <summary>Быстрые ответы: список заготовок, хранится в зашифрованном хранилище.</summary>
public sealed class QuickRepliesService
{
    private const string Key = "chat.quickreplies";
    private readonly SecretStore _secrets;

    public QuickRepliesService(SecretStore secrets) => _secrets = secrets;

    public async Task<List<string>> GetAllAsync()
    {
        var json = await _secrets.GetAsync(Key);
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public async Task SaveAsync(IEnumerable<string> items)
    {
        var clean = items.Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal).Take(30).ToList();
        await _secrets.SetAsync(Key, JsonSerializer.Serialize(clean));
    }

    public async Task AddAsync(string text)
    {
        var items = await GetAllAsync();
        if (items.Any(x => string.Equals(x, text, StringComparison.Ordinal))) return;
        items.Add(text);
        await SaveAsync(items);
    }

    public async Task RemoveAsync(string text)
    {
        var items = await GetAllAsync();
        items.RemoveAll(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(items);
    }
}
