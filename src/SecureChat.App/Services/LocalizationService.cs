using System.Reflection;
using System.Text.Json;

namespace SecureChat.App.Services;

/// <summary>
/// Локализация интерфейса. Языковые словари — JSON-ресурсы в сборке,
/// ключи доступны в XAML через {DynamicResource s_...} и в коде через T().
/// При смене языка словарь подменяется на лету, без перезапуска.
/// </summary>
/// <summary>Статический доступ к переводам — можно вызывать где угодно,
/// включая инициализаторы полей и статические методы.</summary>
public static class Loc
{
    public static string T(string key, string? russianFallback = null) =>
        AppServices.Get<LocalizationService>().T(key, russianFallback);
}

public sealed class LocalizationService
{
    private const string LanguageKey = "ui.language";
    private const string ResourcePrefix = "SecureChat.App.Resources.Localization.";

    private readonly SecretStore _secrets;
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private ResourceDictionary? _dictionary;

    /// <summary>Доступные языки: код → название на языке.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[]
    {
        ("ru", "Русский"),
        ("en", "English"),
        ("es", "Español"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("tr", "Türkçe"),
        ("ar", "العربية"),
        ("zh", "中文"),
        ("hi", "हिन्दी"),
        ("pt", "Português"),
        ("it", "Italiano")
    };

    public event Action? LanguageChanged;

    public string Language { get; private set; } = "en";

    public LocalizationService(SecretStore secrets)
    {
        _secrets = secrets;
    }

    /// <summary>Немедленно загружает словарь по умолчанию (без обращения к хранилищу) —
    /// безопасно вызывать в конструкторе приложения.</summary>
    public void InitializeDefault()
    {
        Language = "en";
        Apply();
    }

    /// <summary>Восстанавливает сохранённый язык из зашифрованного хранилища.</summary>
    public async Task RestoreAsync()
    {
        try
        {
            var saved = await _secrets.GetAsync(LanguageKey);
            if (!string.IsNullOrWhiteSpace(saved) &&
                Languages.Any(item => item.Code == saved) &&
                !string.Equals(saved, Language, StringComparison.Ordinal))
            {
                Language = saved!;
                Apply();
            }
        }
        catch
        {
            // Хранилище недоступно — остаётся английский.
        }
    }

    /// <summary>Меняет язык и сохраняет выбор.</summary>
    public async Task SetLanguageAsync(string code)
    {
        if (!Languages.Any(item => item.Code == code) || code == Language)
        {
            return;
        }

        Language = code;
        await _secrets.SetAsync(LanguageKey, code);
        Apply();
    }

    /// <summary>Перевод по ключу; при отсутствии — русский, затем сам ключ.</summary>
    public string T(string key, string? russianFallback = null)
    {
        if (_strings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (russianFallback is not null)
        {
            return russianFallback;
        }

        // Fallback из русского словаря.
        if (!string.Equals(Language, "ru", StringComparison.Ordinal) &&
            LoadRaw("ru").TryGetValue(key, out var ru))
        {
            return ru;
        }

        return key;
    }

    private void Apply()
    {
        _strings.Clear();
        foreach (var pair in LoadRaw(Language))
        {
            _strings[pair.Key] = pair.Value;
            // XAML обращается к переводам как к {DynamicResource s_...},
            // поэтому тот же текст доступен и под s_-префиксом.
            _strings["s_" + pair.Key] = pair.Value;
        }

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        // Заменяем только наш словарь — стили и цвета не трогаем.
        if (_dictionary is not null)
        {
            app.Resources.MergedDictionaries.Remove(_dictionary);
        }

        _dictionary = new ResourceDictionary();
        foreach (var pair in _strings)
        {
            _dictionary[pair.Key] = pair.Value;
        }

        app.Resources.MergedDictionaries.Add(_dictionary);
        LanguageChanged?.Invoke();
    }

    private static Dictionary<string, string> LoadRaw(string language)
    {
        var resourceName = ResourcePrefix + language + ".json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
