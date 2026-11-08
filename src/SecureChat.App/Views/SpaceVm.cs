using SecureChat.Core.Contracts;

namespace SecureChat.App.Views;

/// <summary>Модель пространства (группа, канал, сообщество, форум) для списка.</summary>
public sealed class SpaceVm
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Icon { get; init; } = "👥";
    public string TypeBadge { get; init; } = "Группа";
    public string MembersText { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;
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
            TopicCount = source.TopicCount,
            CanPost = source.CanPost
        };
    }
}
