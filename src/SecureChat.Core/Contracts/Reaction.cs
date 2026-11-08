namespace SecureChat.Core.Contracts;

/// <summary>
/// Реакция на сообщение. Хранит эмодзи и автора, поэтому разные люди
/// ставят разные реакции и каждую можно снять отдельно.
/// </summary>
public sealed class Reaction
{
    public required string Emoji { get; set; }
    public required string UserId { get; set; }
}
