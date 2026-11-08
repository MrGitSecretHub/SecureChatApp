namespace SecureChat.Core.Contracts;

public sealed class SendEnvelopeResult
{
    public bool Accepted { get; set; }
    public string? Error { get; set; }
}
