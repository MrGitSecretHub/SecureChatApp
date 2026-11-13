namespace SecureChat.App.Views;

/// <summary>Строка «Чаты в папке» на странице редактирования папки.</summary>
public sealed class FolderChatItem
{
    public FolderChatItem(string username, bool isChecked)
    {
        Username = username;
        IsChecked = isChecked;
    }

    public string Username { get; }
    public bool IsChecked { get; set; }
}
