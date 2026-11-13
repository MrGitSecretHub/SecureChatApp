using SecureChat.Core.Contracts;

namespace SecureChat.App.Views;

/// <summary>
/// Строка списка участников: заголовок секции («Владелец», «Администраторы»…)
/// либо участник. Группировка CollectionView на Windows работает ненадёжно,
/// поэтому секции — обычные строки в плоском списке.
/// </summary>
public sealed class MemberRow
{
    private MemberRow(string? header, SpaceMemberVm? member)
    {
        Header = header;
        Member = member;
    }

    public static MemberRow HeaderRow(string name) => new(name, null);
    public static MemberRow MemberRowOf(SpaceMemberVm member) => new(null, member);

    public string? Header { get; }
    public SpaceMemberVm? Member { get; }

    public bool IsHeaderRow => Header is not null && Member is null;
    public bool IsMemberRow => Member is not null;
    public string HeaderText => Header ?? string.Empty;

    // Прокси-свойства участника для шаблона.
    public string Username => Member?.Username ?? string.Empty;
    public string Initial => Member?.Initial ?? "?";
    public Color AvatarColor => Member?.AvatarColor ?? Colors.Gray;
    public string RoleText => Member?.RoleText ?? string.Empty;
    public string CanPostText => Member?.CanPostText ?? string.Empty;
    public string StatusText => Member?.StatusText ?? string.Empty;
}
