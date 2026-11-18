using SecureChat.Core.Contracts;
using SecureChat.App.Services;

namespace SecureChat.App.Views;

/// <summary>Модель пространства (группа, канал, сообщество, форум) для списка.</summary>
public sealed class SpaceVm
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Icon { get; init; } = "👥";
    public string TypeBadge { get; init; } = Loc.T("rs_040", "Группа");
    public string MembersText { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;

    /// <summary>Последнее сообщение (расшифровать нельзя — сервер хранит только текст заголовков).</summary>
    public string LastMessage { get; init; } = string.Empty;

    public bool IsPublic { get; init; }

    /// <summary>Вторая строка карточки: последнее сообщение, а если его нет — описание.</summary>
    public string PreviewLine => string.IsNullOrWhiteSpace(LastMessage) ? Description : LastMessage;

    /// <summary>Публичное пространство, участником которого я не состою.</summary>
    public bool IsJoinable => IsPublic && string.IsNullOrEmpty(MyRole);

    public int TopicCount { get; init; }
    public bool CanPost { get; init; } = true;

    /// <summary>Моя роль в пространстве: владелец, администратор или участник.</summary>
    public string MyRole { get; init; } = string.Empty;

    /// <summary>Могу ли я закреплять и удалять сообщения пространства.</summary>
    public bool CanModerate =>
        MyRole is SpaceRoles.Owner or SpaceRoles.Admin;

    public static SpaceVm From(SpaceDto source)
    {
        var typeBadge = source.Type switch
        {
            SpaceTypes.Channel => "Канал",
            SpaceTypes.Community => "Сообщество",
            SpaceTypes.Forum => "Форум",
            _ => "Группа"
        };

        var icon = source.Type switch
        {
            SpaceTypes.Channel => "📢",
            SpaceTypes.Community => "🌐",
            SpaceTypes.Forum => "🧵",
            _ => "👥"
        };

        var members = source.MemberCount > 0
            ? $"{source.MemberCount} участников"
            : "Нет участников";

        if (source.TopicCount > 0)
        {
            members += $", {source.TopicCount} тем";
        }

        var time = source.LastMessageAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(source.LastMessageAtUnixMs)
                .ToLocalTime().ToString("dd.MM HH:mm")
            : string.Empty;

        return new SpaceVm
        {
            Id = source.Id,
            Name = source.Name,
            Type = source.Type,
            Description = source.Description,
            Icon = icon,
            TypeBadge = typeBadge,
            MembersText = members,
            TimeText = time,
            LastMessage = source.LastMessageText,
            IsPublic = source.IsPublic,
            TopicCount = source.TopicCount,
            CanPost = source.CanPost,
            MyRole = source.MyRole,
        };
    }
}
