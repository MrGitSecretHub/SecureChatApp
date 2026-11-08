using System.Text.Json;

namespace SecureChat.Core.Contracts;

/// <summary>Вложение пространства: файл шифруется своим ключом, а ключ лежит внутри зашифрованного содержимого.</summary>
public sealed class SpaceMediaAttachment
{
    public required string Id { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Key { get; set; }
    public required string Nonce { get; set; }
    public required string Tag { get; set; }
}

/// <summary>Содержимое сообщения пространства. Шифруется общим ключом пространства целиком.</summary>
public sealed class SpaceMessagePayload
{
    /// <summary>"text", "image" или "file".</summary>
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = string.Empty;
    public string? ReplyToMessageId { get; set; }
    public string? ReplyToText { get; set; }
    public bool IsForwarded { get; set; }
    public SpaceMediaAttachment? Media { get; set; }
}

/// <summary>Кодирование содержимого. Старые сообщения были обычным текстом без префикса.</summary>
public static class SpacePayloadCodec
{
    private const string Prefix = "\u0001sp1:";

    public static string Encode(SpaceMessagePayload payload) =>
        Prefix + JsonSerializer.Serialize(payload);

    public static SpaceMessagePayload Decode(string plaintext)
    {
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return JsonSerializer.Deserialize<SpaceMessagePayload>(plaintext[Prefix.Length..])
                ?? new SpaceMessagePayload();
        }

        return new SpaceMessagePayload { Text = plaintext };
    }
}
