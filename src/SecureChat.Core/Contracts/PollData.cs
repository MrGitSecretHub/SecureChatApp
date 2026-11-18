namespace SecureChat.Core.Contracts;

/// <summary>Опрос внутри зашифрованного сообщения. Сервер видит только шифротекст.</summary>
public sealed class PollData
{
    public string Id { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}

/// <summary>Голос за вариант опроса — отдельное зашифрованное сообщение.</summary>
public sealed class PollVoteData
{
    public string PollId { get; set; } = string.Empty;
    public int OptionIndex { get; set; }
}
