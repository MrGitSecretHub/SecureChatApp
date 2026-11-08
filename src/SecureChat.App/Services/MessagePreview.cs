namespace SecureChat.App.Services;

/// <summary>Короткое однострочное превью сообщения для тостов и уведомлений.</summary>
public static class MessagePreview
{
    public static string Build(string text, int maxLength = 140)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Новое сообщение";
        }

        var singleLine = string.Join(
            ' ',
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "…";
    }
}
