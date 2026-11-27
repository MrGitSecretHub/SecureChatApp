using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;
using SecureChat.Core.Serialization;
using SecureChat.Server.Data;
using SecureChat.Server.Services;

namespace SecureChat.Server.Hubs;

[Authorize]
public sealed class ChatHub : Hub
{
    private const int MaxEnvelopeBytes = 100_000;
    private readonly ChatRepository _repository;
    private readonly SpaceRepository _spaces;
    private readonly EnvelopeRouter _router;
    private readonly PresenceTracker _presence;
    private readonly PushNotificationService _push;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        ChatRepository repository,
        SpaceRepository spaces,
        EnvelopeRouter router,
        PresenceTracker presence,
        PushNotificationService push,
        ILogger<ChatHub> logger)
    {
        _repository = repository;
        _spaces = spaces;
        _router = router;
        _presence = presence;
        _push = push;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId))
        {
            Context.Abort();
            return;
        }

        var user = await _repository.GetUserByIdAsync(userId);
        if (user is null)
        {
            Context.Abort();
            return;
        }

        if (await _repository.IsUserBannedAsync(userId))
        {
            _logger.LogWarning("Забаненный пользователь {UserId} попытался подключиться", userId);
            Context.Abort();
            return;
        }

        _presence.Connect(userId);
        await _repository.TouchLastSeenAsync(userId);

        // Добавляем пользователя во все группы его пространств, чтобы
        // приглашённые участники получали сообщения в реальном времени.
        var mySpaces = await _spaces.ListForUserAsync(userId);
        foreach (var space in mySpaces)
        {
            await Groups.AddToGroupAsync($"space:{space.Id}", userId);
        }

        var pending = await _repository.GetPendingAsync(userId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var item in pending)
        {
            try
            {
                var envelope = ChatJson.Deserialize<EncryptedEnvelope>(item.Json);
                if (envelope.ExpireAtUnixMs.HasValue && envelope.ExpireAtUnixMs.Value <= now)
                {
                    continue; // просрочено — не доставляем
                }
                await Clients.Caller.SendAsync("ReceiveEnvelope", envelope);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Повреждённый конверт {MessageId} пропущен", item.Id);
            }
        }

        _logger.LogInformation(
            "Пользователь {Username} подключён, недоставленных сообщений: {Count}",
            user.Username,
            pending.Count);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
        {
            _presence.Disconnect(userId);
            await _repository.TouchLastSeenAsync(userId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<SendEnvelopeResult> SendEnvelope(EncryptedEnvelope envelope)
    {
        var route = await _router.RouteAsync(
            envelope,
            Context.User ?? new ClaimsPrincipal(new ClaimsIdentity()));

        if (!route.Accepted)
        {
            return Fail(route.Error ?? "Сообщение отклонено.");
        }

        await Clients.User(route.RecipientUserId!).SendAsync("ReceiveEnvelope", route.Envelope);

        var recipient = await _repository.GetUserByIdAsync(route.RecipientUserId!);
        var senderName = Context.User?.Identity?.Name ?? "SecureChat";
        if (!string.IsNullOrEmpty(recipient?.PushTopic))
            await _push.NotifyAsync(recipient.PushTopic, senderName);

        await Clients.Caller.SendAsync(
            "EnvelopeAccepted",
            envelope.Id,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return new SendEnvelopeResult { Accepted = true };
    }

    public async Task<int> AckMessages(string[] messageIds)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || messageIds is null || messageIds.Length == 0)
        {
            return 0;
        }

        var delivered = await _repository.MarkDeliveredAsync(userId, messageIds);
        foreach (var group in delivered.GroupBy(item => item.SenderId))
        {
            await Clients.User(group.Key).SendAsync(
                "MessagesDelivered",
                group.Select(item => item.Id).ToArray());
        }

        return delivered.Count;
    }

    public async Task ReadMessages(string[] messageIds)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || messageIds is null || messageIds.Length == 0)
        {
            return;
        }

        var messages = await _repository.GetRecipientMessagesAsync(userId, messageIds);
        foreach (var group in messages.GroupBy(item => item.SenderId))
        {
            await Clients.User(group.Key).SendAsync(
                "MessagesRead",
                group.Select(item => item.Id).ToArray());
        }
    }

    public async Task Typing(string recipientId, string activity = "typing")
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(recipientId))
        {
            return;
        }

        var sender = await _repository.GetUserByIdAsync(userId);
        if (sender is null)
        {
            return;
        }

        // Ограничиваем допустимые типы активности, чтобы клиент не мог прислать произвольный текст.
        var allowed = new[] { "typing", "uploading_photo", "uploading_video", "recording_voice", "recording_video", "seen" };
        if (!allowed.Contains(activity, StringComparer.OrdinalIgnoreCase)) activity = "typing";
        await Clients.User(recipientId).SendAsync("Typing", sender.Username, activity);
    }

    public Task SetPresenceHidden(bool hidden)
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
        {
            _presence.SetHidden(userId, hidden);
        }

        return Task.CompletedTask;
    }

    // ============================================================
    // ПРОСТРАНСТВА: ГРУППЫ, КАНАЛЫ, СООБЩЕСТВА, ФОРУМЫ
    // ============================================================

    public async Task<List<SpaceDto>> ListSpaces()
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId))
        {
            return new List<SpaceDto>();
        }

        return await _spaces.ListForUserAsync(userId);
    }

    public async Task<SpaceDto?> CreateSpace(CreateSpaceRequest request)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(request.Name))
        {
            return null;
        }

        var type = request.Type is SpaceTypes.Channel or SpaceTypes.Community or SpaceTypes.Forum
            ? request.Type
            : SpaceTypes.Group;

        var owner = await _repository.GetUserByIdAsync(userId);
        if (owner is null)
        {
            return null;
        }

        var space = await _spaces.CreateAsync(
            userId,
            owner.Username,
            new CreateSpaceRequest
            {
                Name = request.Name,
                Type = type,
                Description = request.Description,
                IsPublic = request.IsPublic,
                Members = request.Members
            });

        // Общий ключ пространства шифруется для каждого участника отдельно.
        var spaceKey = SpaceCrypto.CreateSpaceKey();
        await _spaces.SaveSpaceKeyAsync(
            space.Id,
            userId,
            ChatJson.Serialize(SpaceCrypto.EncryptSpaceKey(
                spaceKey,
                space.Id,
                userId,
                owner.EncryptionPublicKey)));

        // Заготавливаем конверты для ВСЕХ пользователей заранее: новый участник
        // получит ключ сразу, даже если при вступлении никто не в сети.
        var allUsers = await _repository.ListUsersAsync();
        foreach (var user in allUsers.Take(500))
        {
            if (string.IsNullOrWhiteSpace(user.EncryptionPublicKey)) continue;
            await _spaces.SaveSpaceKeyAsync(
                space.Id,
                user.Id,
                ChatJson.Serialize(SpaceCrypto.EncryptSpaceKey(
                    spaceKey, space.Id, user.Id, user.EncryptionPublicKey)));
        }

        foreach (var member in request.Members.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(member) || string.Equals(member, userId, StringComparison.Ordinal))
            {
                continue;
            }

            var invited = await _repository.GetUserByUsernameAsync(member);
            if (invited is null)
            {
                continue;
            }

            // В каналах, сообществах и форумах обычные участники только читают;
            // право публиковать выдаёт владелец/админ отдельно.
            var canPost = type == SpaceTypes.Group;
            await _spaces.AddMemberAsync(space.Id, invited.Id, SpaceRoles.Member, canPost: canPost);
            await _spaces.SaveSpaceKeyAsync(
                space.Id,
                invited.Id,
                ChatJson.Serialize(SpaceCrypto.EncryptSpaceKey(
                    spaceKey,
                    space.Id,
                    invited.Id,
                    invited.EncryptionPublicKey)));
        }

        await Groups.AddToGroupAsync($"space:{space.Id}", userId);
        return space;
    }

    public async Task<SpaceKeyEnvelope?> GetSpaceKey(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return null;
        }

        var envelope = await _spaces.GetSpaceKeyAsync(spaceId, userId);
        if (envelope is null)
        {
            return null;
        }

        try
        {
            return ChatJson.Deserialize<SpaceKeyEnvelope>(envelope);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Приглашает пользователя в пространство (только владелец или админ).
    /// Добавляет участника; ключ пространства клиент передаёт отдельно
    /// через SaveSpaceMemberKey — сервер сохраняет конверт, не зная ключа.
    /// </summary>
    public async Task<InviteSpaceMemberResult> InviteSpaceMember(string spaceId, string targetUserId)
    {
        var requesterId = Context.UserIdentifier;
        var result = new InviteSpaceMemberResult();

        if (string.IsNullOrEmpty(requesterId) ||
            string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(targetUserId))
        {
            result.Error = "error";
            return result;
        }

        var space = await _spaces.GetAsync(spaceId, requesterId);
        if (space is null)
        {
            result.Error = "space_not_found";
            return result;
        }

        if (!IsAdmin(space.MyRole))
        {
            result.Error = "no_rights";
            return result;
        }

        var invited = await _repository.GetUserByIdAsync(targetUserId);
        if (invited is null)
        {
            result.Error = "user_not_found";
            return result;
        }

        var members = await _spaces.GetMembersAsync(spaceId);
        if (members.Any(item => string.Equals(item.UserId, invited.Id, StringComparison.Ordinal)))
        {
            result.Error = "already_member";
            return result;
        }

        // Как при создании: писать могут только в группах; в канал/форум/сообщество
        // приглашённый приходит читателем, право даёт админ.
        var added = await _spaces.AddMemberAsync(
            spaceId,
            invited.Id,
            SpaceRoles.Member,
            canPost: string.Equals(space.Type, SpaceTypes.Group, StringComparison.OrdinalIgnoreCase));
        if (!added)
        {
            result.Error = "error";
            return result;
        }

        result.Success = true;
        result.UserId = invited.Id;
        result.Username = invited.Username;

        // В реальном времени сообщаем приглашённому о новом пространстве,
        // чтобы его клиент подтянул участие в SignalR-группе.
        try
        {
            await Clients.User(invited.Id).SendAsync("SpaceAdded", space);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось уведомить {UserId} о пространстве {SpaceId}", invited.Id, spaceId);
        }

        return result;
    }

    /// <summary>Сохраняет «конверт» с ключом пространства для участника (шифрует клиент).</summary>
    /// <summary>Участник без ключа просит группу прислать его ещё раз.</summary>
    /// <summary>Список участников, которым ещё не выдан ключ пространства.</summary>
    public async Task<List<string>> GetSpaceMembersWithoutKey(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return new List<string>();
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return new List<string>();
        }

        var keys = await _spaces.GetSpaceKeysAsync(spaceId);
        var members = await _spaces.GetMembersAsync(spaceId);
        return members.Where(m => !keys.ContainsKey(m.UserId)).Select(m => m.UserId).ToList();
    }

    /// <summary>Пакетная выдача ключа (только админ/владелец).</summary>
    public async Task<bool> SaveSpaceMemberKeys(string spaceId, Dictionary<string, string> envelopes)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) || envelopes.Count == 0)
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return false;
        }

        var members = await _spaces.GetMembersAsync(spaceId);
        var valid = members.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in envelopes)
        {
            if (!valid.Contains(pair.Key) || pair.Value.Length > 64 * 1024) continue;
            await _spaces.SaveSpaceKeyAsync(spaceId, pair.Key, pair.Value);
        }
        return true;
    }

    public async Task<bool> RequestSpaceKey(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || string.IsNullOrEmpty(space.MyRole))
        {
            return false;
        }

        var key = await _spaces.GetSpaceKeyAsync(spaceId, userId);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return true; // ключ уже есть — повторный запрос не нужен
        }

        var profile = await _repository.GetUserByIdAsync(userId);
        await Clients.Group($"space:{spaceId}").SendAsync(
            "SpaceMemberNeedKey",
            spaceId,
            userId,
            profile?.Username ?? string.Empty);
        return true;
    }

    public async Task<bool> SaveSpaceMemberKey(string spaceId, string targetUserId, string envelopeJson)
    {
        var requesterId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(requesterId) ||
            string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(targetUserId) ||
            string.IsNullOrWhiteSpace(envelopeJson))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, requesterId);
        // Ключ может передать ЛЮБОЙ участник: так устроен авто-вход в публичные
        // пространства (приглашение же остаётся админской операцией).
        if (space is null || string.IsNullOrEmpty(space.MyRole))
        {
            return false;
        }

        var members = await _spaces.GetMembersAsync(spaceId);
        if (!members.Any(item => string.Equals(item.UserId, targetUserId, StringComparison.Ordinal)))
        {
            return false;
        }

        // Ключ можно выдать участнику только один раз. Перезапись уже выданного
        // ключа позволила бы подменить групповой ключ — это могут только админы.
        var existingKey = await _spaces.GetSpaceKeyAsync(spaceId, targetUserId);
        if (!string.IsNullOrWhiteSpace(existingKey) && !IsAdmin(space.MyRole))
        {
            return false;
        }

        try
        {
            ChatJson.Deserialize<SpaceKeyEnvelope>(envelopeJson);
        }
        catch (JsonException)
        {
            return false;
        }

        await _spaces.SaveSpaceKeyAsync(spaceId, targetUserId, envelopeJson);

        // Сообщаем новому участнику: ключ есть — пространство расшифровуемо.
        try
        {
            await Clients.User(targetUserId).SendAsync("SpaceAdded", space);
        }
        catch
        {
            // Уведомление — дело второе, ключ уже сохранён.
        }

        return true;
    }

    /// <summary>Добавляет текущее подключение во все группы пространств пользователя.</summary>
    public async Task<bool> SyncSpaceGroups()
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var spaces = await _spaces.ListForUserAsync(userId);
        foreach (var space in spaces)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"space:{space.Id}");

            // Если у нас есть ключ, подскажем, кому из участников он ещё не доставлен —
            // запрос при вступлении мог никого не застать онлайн.
            var keys = await _spaces.GetSpaceKeysAsync(space.Id);
            if (!keys.ContainsKey(userId)) continue;
            var members = await _spaces.GetMembersAsync(space.Id);
            foreach (var member in members.Where(m => !keys.ContainsKey(m.UserId)))
            {
                var profile = await _repository.GetUserByIdAsync(member.UserId);
                await Clients.Caller.SendAsync(
                    "SpaceMemberNeedKey",
                    space.Id,
                    member.UserId,
                    profile?.Username ?? string.Empty);
            }
        }

        return true;
    }

    public async Task<List<SpaceMemberDto>> GetSpaceMembers(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return new List<SpaceMemberDto>();
        }

        if (await _spaces.GetAsync(spaceId, userId) is null)
        {
            return new List<SpaceMemberDto>();
        }

        var members = await _spaces.GetMembersAsync(spaceId);
        foreach (var member in members)
        {
            member.OnlineStatus = _presence.IsOnline(member.UserId) ? "Онлайн" : "Неактивен";
        }

        return members;
    }

    // ============================== ЗАЯВКИ НА ВСТУПЛЕНИЕ ==============================

    /// <summary>Подать заявку на вступление в приватное пространство.</summary>
    public async Task<bool> RequestToJoinSpace(string spaceId, string message)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null)
        {
            return false;
        }

        if (space.IsPublic)
        {
            // Публичное пространство не требует заявки — вступаем сразу.
            return await JoinSpace(spaceId);
        }

        var members = await _spaces.GetMembersAsync(spaceId);
        if (members.Any(item => string.Equals(item.UserId, userId, StringComparison.Ordinal)))
        {
            return true; // уже участник
        }

        var created = await _spaces.CreateJoinRequestAsync(spaceId, userId, message ?? string.Empty);

        // Уведомляем администраторов о новой заявке.
        var admins = members
            .Where(item => item.Role is SpaceRoles.Owner or SpaceRoles.Admin)
            .Select(item => item.UserId)
            .ToList();
        foreach (var adminId in admins)
        {
            await Clients.User(adminId).SendAsync("JoinRequestReceived", spaceId);
        }

        return created;
    }

    /// <summary>Список ожидающих заявок (только владелец и администраторы).</summary>
    public async Task<List<SpaceJoinRequestDto>> GetJoinRequests(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return new List<SpaceJoinRequestDto>();
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return new List<SpaceJoinRequestDto>();
        }

        return await _spaces.ListPendingJoinRequestsAsync(spaceId);
    }

    /// <summary>Принять или отклонить заявку (только владелец и администраторы).</summary>
    public async Task<bool> ResolveJoinRequest(string requestId, bool approved)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        // Одобрять заявку может только владелец или администратор пространства.
        var pending = await _spaces.GetJoinRequestAsync(requestId);
        if (pending is null)
        {
            return false;
        }
        var ownSpace = await _spaces.GetAsync(pending.SpaceId, userId);
        if (ownSpace is null || !IsAdmin(ownSpace.MyRole))
        {
            return false;
        }

        var request = await _spaces.ResolveJoinRequestAsync(requestId, approved, userId);
        if (request is null)
        {
            return false;
        }

        var space = await _spaces.GetAsync(request.SpaceId, userId);
        var applicant = await _repository.GetUserByIdAsync(request.UserId);

        if (approved)
        {
            // При одобрении добавляем в участники и в группу SignalR —
            // дальше E2E-ключ доставляется по уже работающему механизму.
            var canPost = string.Equals(space?.Type, SpaceTypes.Group, StringComparison.OrdinalIgnoreCase);
            await _spaces.AddMemberAsync(request.SpaceId, request.UserId, SpaceRoles.Member, canPost);
            await Groups.AddToGroupAsync($"space:{request.SpaceId}", request.UserId);

            if (space is not null)
            {
                await Clients.User(request.UserId).SendAsync("SpaceAdded", space);
            }

            await Clients.Group($"space:{request.SpaceId}").SendAsync(
                "SpaceMemberNeedKey",
                request.SpaceId,
                request.UserId,
                applicant?.Username ?? request.Username);
        }

        // Уведомляем заявителя о решении.
        await Clients.User(request.UserId).SendAsync(
            "JoinRequestResolved",
            request.SpaceId,
            approved,
            request.SpaceName);

        // Обновляем списки заявок у администраторов.
        var members = await _spaces.GetMembersAsync(request.SpaceId);
        foreach (var admin in members.Where(item => item.Role is SpaceRoles.Owner or SpaceRoles.Admin))
        {
            await Clients.User(admin.UserId).SendAsync("JoinRequestReceived", request.SpaceId);
        }

        return true;
    }

    public async Task<bool> JoinSpace(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !space.IsPublic)
        {
            return false;
        }

        var members = await _spaces.GetMembersAsync(spaceId);
        if (members.Any(item => string.Equals(item.UserId, userId, StringComparison.Ordinal)))
        {
            return true; // уже участник
        }

        // Публичное пространство: вступление без приглашения. Право писать — по типу,
        // как при приглашении (в группах пишут все, в канал/форум/сообщество — по назначению).
        var canPost = string.Equals(space.Type, SpaceTypes.Group, StringComparison.OrdinalIgnoreCase);
        var added = await _spaces.AddMemberAsync(spaceId, userId, SpaceRoles.Member, canPost);
        await Groups.AddToGroupAsync($"space:{spaceId}", userId);

        // E2E-ключ新ему участнику передаёт любой онлайн-участник: просим группу.
        var joiner = await _repository.GetUserByIdAsync(userId);
        await Clients.Group($"space:{spaceId}")
            .SendAsync("SpaceMemberNeedKey", spaceId, userId, joiner?.Username ?? string.Empty);

        return added;
    }

    /// <summary>Активность участника в пространстве (печатает, загрузка, запись).</summary>
    /// <summary>Сменяет общий ключ пространства и выдаёт его всем участникам.</summary>
    public async Task<bool> RotateSpaceKey(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId)) return false;
        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || space.MyRole is not (SpaceRoles.Owner or SpaceRoles.Admin)) return false;

        var newKey = SpaceCrypto.CreateSpaceKey();
        var members = await _spaces.GetMembersAsync(spaceId);
        foreach (var member in members)
        {
            var profile = await _repository.GetUserByIdAsync(member.UserId);
            if (profile is null || string.IsNullOrWhiteSpace(profile.EncryptionPublicKey)) continue;
            var envelope = SpaceCrypto.EncryptSpaceKey(
                newKey, spaceId, member.UserId, profile.EncryptionPublicKey);
            await _spaces.SaveSpaceKeyAsync(spaceId, member.UserId, ChatJson.Serialize(envelope));
        }

        await Clients.Group($"space:{spaceId}").SendAsync("SpaceKeyRotated", spaceId);
        return true;
    }

    public async Task SpaceActivity(string spaceId, string activity)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId)) return;
        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || string.IsNullOrEmpty(space.MyRole)) return;
        var allowed = new[] { "typing", "uploading_photo", "uploading_video", "recording_voice", "recording_video", "seen" };
        if (!allowed.Contains(activity, StringComparer.OrdinalIgnoreCase)) activity = "typing";
        var sender = await _repository.GetUserByIdAsync(userId);
        if (sender is null) return;
        await Clients.GroupExcept($"space:{spaceId}", Context.ConnectionId)
            .SendAsync("SpaceActivity", spaceId, sender.Username, activity);
    }

    public async Task<bool> LeaveSpace(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        await Groups.RemoveFromGroupAsync($"space:{spaceId}", userId);
        return await _spaces.RemoveMemberAsync(spaceId, userId);
    }

    public async Task<SpaceMessageDto?> SendSpaceMessage(SendSpaceMessageRequest request)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(request.SpaceId))
        {
            return null;
        }

        var space = await _spaces.GetAsync(request.SpaceId, userId);
        if (space is null || !space.CanPost)
        {
            return null;
        }

        if (await _spaces.IsMemberMutedAsync(request.SpaceId, userId))
        {
            _logger.LogWarning(
                "Замученный пользователь {UserId} попытался отправить в пространство {SpaceId}",
                userId, request.SpaceId);
            return null;
        }

        var sender = await _repository.GetUserByIdAsync(userId);
        if (sender is null)
        {
            return null;
        }

        var cipher = request.Cipher;
        if (cipher is null || string.IsNullOrWhiteSpace(cipher.Ciphertext))
        {
            return null;
        }

        // Подпись проверяется сервером: участник не может подделать чужое сообщение.
        if (!SpaceCrypto.VerifyMessage(
                request.SpaceId,
                cipher.MessageId,
                userId,
                cipher,
                sender.SigningPublicKey))
        {
            _logger.LogWarning(
                "Отклонено сообщение пространства {MessageId} с неверной подписью",
                cipher.MessageId);
            return null;
        }

        var message = new SpaceMessageDto
        {
            Id = cipher.MessageId,
            SpaceId = request.SpaceId,
            TopicId = request.TopicId ?? cipher.TopicId,
            SenderId = userId,
            SenderUsername = sender.Username,
            Nonce = cipher.Nonce,
            Tag = cipher.Tag,
            Ciphertext = cipher.Ciphertext,
            Signature = cipher.Signature,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await _spaces.SaveEncryptedMessageAsync(message);
        await Clients.Group($"space:{request.SpaceId}").SendAsync("SpaceMessage", message);

        var memberIds = await _spaces.GetMemberIdsAsync(request.SpaceId);
        var topics = new List<string>();
        foreach (var memberId in memberIds.Where(id => id != userId))
        {
            var member = await _repository.GetUserByIdAsync(memberId);
            if (!string.IsNullOrEmpty(member?.PushTopic)) topics.Add(member.PushTopic);
        }
        await _push.NotifyManyAsync(topics, sender.Username);

        return message;
    }

    public async Task<SpaceMessageDto?> EditSpaceMessage(string spaceId, SpaceCipher cipher)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) || cipher is null)
        {
            return null;
        }

        var member = await FindMemberAsync(spaceId, userId);
        var message = await _spaces.GetEncryptedMessageAsync(spaceId, cipher.MessageId);
        var sender = await _repository.GetUserByIdAsync(userId);
        if (member is null || !member.CanPost || message is null || sender is null ||
            !string.Equals(message.SenderId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!SpaceCrypto.VerifyMessage(spaceId, cipher.MessageId, userId, cipher, sender.SigningPublicKey))
        {
            return null;
        }

        message.Nonce = cipher.Nonce;
        message.Tag = cipher.Tag;
        message.Ciphertext = cipher.Ciphertext;
        message.Signature = cipher.Signature;
        message.EditedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!await _spaces.UpdateEncryptedMessageAsync(message))
        {
            return null;
        }

        await Clients.Group($"space:{spaceId}").SendAsync("SpaceMessageUpdated", message);
        return message;
    }

    public async Task<bool> DeleteSpaceMessage(string spaceId, string messageId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var member = await FindMemberAsync(spaceId, userId);
        var message = await _spaces.GetEncryptedMessageAsync(spaceId, messageId);
        if (member is null || message is null)
        {
            return false;
        }

        var canModerate = member.Role is SpaceRoles.Owner or SpaceRoles.Admin;
        if (!canModerate && !string.Equals(message.SenderId, userId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!await _spaces.DeleteEncryptedMessageAsync(spaceId, messageId))
        {
            return false;
        }

        await Clients.Group($"space:{spaceId}").SendAsync("SpaceMessageDeleted", spaceId, messageId);
        return true;
    }

    /// <summary>Удаляет личное сообщение для всех участников чата.</summary>
    public async Task<bool> DeletePrivateMessage(string messageId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        // Получателя узнаём ДО удаления: после DELETE строка сообщения уже не найдётся,
        // и уведомить собеседника о удалении не получится.
        var message = await _repository.GetPrivateMessageAsync(messageId);
        if (message is null)
        {
            return false;
        }

        var (senderId, recipientId) = message.Value;
        if (!string.Equals(senderId, userId, StringComparison.Ordinal))
        {
            // Удалять сообщение может только его отправитель.
            return false;
        }

        if (!await _repository.DeletePrivateMessageAsync(messageId, userId))
        {
            return false;
        }

        await Clients.User(recipientId).SendAsync("PrivateMessageDeleted", messageId);
        return true;
    }

    /// <summary>
    /// Просмотрено один раз: получатель сообщает, что открыл одноразовое
    /// сообщение. Сервер удаляет его и уведомляет обе стороны. Удалить может
    /// только участник переписки (отправитель или получатель).
    /// </summary>
    public async Task<bool> ConsumeViewOnce(string messageId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        var message = await _repository.GetPrivateMessageAsync(messageId);
        if (message is null)
        {
            return false;
        }

        var (senderId, recipientId) = message.Value;
        if (!string.Equals(senderId, userId, StringComparison.Ordinal) &&
            !string.Equals(recipientId, userId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!await _repository.DeletePrivateMessageByParticipantAsync(messageId, userId))
        {
            return false;
        }

        await Clients.User(senderId).SendAsync("PrivateMessageDeleted", messageId);
        await Clients.User(recipientId).SendAsync("PrivateMessageDeleted", messageId);
        return true;
    }

    /// <summary>Удаляет всю личную переписку с пользователем для обеих сторон.</summary>
    public async Task<bool> DeletePrivateConversation(string username)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        var otherUser = await _repository.GetUserByUsernameAsync(username);
        if (otherUser is null)
        {
            return false;
        }

        // Удаляем всю переписку между ними на сервере.
        var deletedCount = await _repository.DeletePrivateConversationAsync(userId, otherUser.Id);

        // Уведомляем получателя, что переписка удалена.
        await Clients.User(otherUser.Id).SendAsync("PrivateConversationDeleted", userId);

        return deletedCount > 0;
    }

    /// <summary>Очищает всю личную переписку с пользователем для обеих сторон (чат остаётся в списке).</summary>
    public async Task<bool> ClearPrivateConversation(string username)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        var otherUser = await _repository.GetUserByUsernameAsync(username);
        if (otherUser is null)
        {
            return false;
        }

        var deletedCount = await _repository.DeletePrivateConversationAsync(userId, otherUser.Id);

        // Уведомляем получателя, что история очищена.
        await Clients.User(otherUser.Id).SendAsync("PrivateConversationCleared", userId);

        return deletedCount > 0;
    }

    public async Task<SpaceMessageDto?> SetSpaceReaction(string spaceId, string messageId, string emoji, bool on)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(emoji) || emoji.Length > 16)
        {
            return null;
        }

        if (await FindMemberAsync(spaceId, userId) is null)
        {
            return null;
        }

        await _spaces.SetReactionAsync(messageId, userId, emoji, on);
        var message = await _spaces.GetEncryptedMessageAsync(spaceId, messageId);
        if (message is not null)
        {
            await Clients.Group($"space:{spaceId}").SendAsync("SpaceMessageUpdated", message);
        }

        return message;
    }

    public async Task<bool> SetSpacePinned(string spaceId, string messageId, bool pinned)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var member = await FindMemberAsync(spaceId, userId);
        if (member is null || member.Role is not (SpaceRoles.Owner or SpaceRoles.Admin))
        {
            return false;
        }

        await _spaces.SetPinnedAsync(spaceId, messageId, pinned);
        var message = await _spaces.GetEncryptedMessageAsync(spaceId, messageId);
        if (message is null)
        {
            return false;
        }

        await Clients.Group($"space:{spaceId}").SendAsync("SpaceMessageUpdated", message);
        return true;
    }

    private async Task<SpaceMemberDto?> FindMemberAsync(string spaceId, string userId)
    {
        var members = await _spaces.GetMembersAsync(spaceId);
        return members.FirstOrDefault(item => string.Equals(item.UserId, userId, StringComparison.Ordinal));
    }

    /// <summary>Отметить посты канала как просмотренные текущим участником.</summary>
    public async Task RecordSpaceMessageViews(string spaceId, IEnumerable<string> messageIds)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId)) return;
        if (await FindMemberAsync(spaceId, userId) is null) return;
        var ids = messageIds.Take(200).ToList();
        await _spaces.RecordViewsAsync(ids, userId);
        await Clients.Group($"space:{spaceId}")
            .SendAsync("SpaceMessagesViewed", spaceId, userId, ids);
    }

    /// <summary>Список логинов, просмотревших пост канала.</summary>
    public async Task<List<string>> GetSpaceMessageViewers(string spaceId, string messageId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(messageId))
        {
            return new List<string>();
        }
        if (await FindMemberAsync(spaceId, userId) is null) return new List<string>();
        var message = await _spaces.GetEncryptedMessageAsync(spaceId, messageId);
        if (message is null) return new List<string>();
        return (await _spaces.GetViewersAsync(messageId)).ToList();
    }

    public async Task<List<SpaceMessageDto>> GetSpaceMessages(string spaceId, string? topicId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrWhiteSpace(spaceId) || string.IsNullOrEmpty(userId))
        {
            return new List<SpaceMessageDto>();
        }

        // Шифротексты чужих пространств недоступны: сервер их не читает,
        // но логин отправителя и метаданные видны — пускаем только участников.
        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || string.IsNullOrEmpty(space.MyRole))
        {
            return new List<SpaceMessageDto>();
        }

        // Корень форума — только сообщения вне тем: иначе лента перемешана с топиками.
        var withoutTopicOnly = topicId is null && space.Type == SpaceTypes.Forum;
        return await _spaces.ListEncryptedMessagesAsync(spaceId, topicId, 1000, withoutTopicOnly);
    }

    public async Task<List<TopicDto>> GetTopics(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return new List<TopicDto>();
        }

        if (await _spaces.GetAsync(spaceId, userId) is null)
        {
            return new List<TopicDto>();
        }

        return await _spaces.ListTopicsAsync(spaceId);
    }

    public async Task<TopicDto?> CreateTopic(CreateTopicRequest request)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(request.Title))
        {
            return null;
        }

        var space = await _spaces.GetAsync(request.SpaceId ?? string.Empty, userId);
        if (space is null || !space.CanPost)
        {
            return null;
        }

        var creator = await _repository.GetUserByIdAsync(userId);
        if (creator is null)
        {
            return null;
        }

        var topic = await _spaces.CreateTopicAsync(
            request.SpaceId!,
            request.Title,
            userId,
            creator.Username,
            request.Text);

        await Clients.Group($"space:{request.SpaceId}").SendAsync("TopicCreated", topic);
        return topic;
    }

    public async Task<bool> DeleteSpace(string spaceId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        return await _spaces.DeleteAsync(spaceId, userId);
    }

    public async Task<SpaceDto?> UpdateSpace(UpdateSpaceRequest request)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(request.Name))
        {
            return null;
        }

        var space = await _spaces.GetAsync(request.SpaceId ?? string.Empty, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return null;
        }

        await _spaces.UpdateAsync(
            space.Id,
            request.Name.Trim(),
            request.Description?.Trim() ?? string.Empty,
            request.IsPublic,
            request.SignMessages);

        return await _spaces.GetAsync(space.Id, userId);
    }

    public async Task<bool> UpdateSpaceMember(string spaceId, string userId, string? role, bool? canPost)
    {
        var requesterId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, requesterId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return false;
        }

        var members = await _spaces.GetMembersAsync(spaceId);
        var member = members.FirstOrDefault(item => item.UserId == userId);
        if (member is null)
        {
            return false;
        }

        // Владелец неприкосновенен: сменить его роль может только он сам (передача владения),
        // понизить себя — значит запереть администрирование.
        if (member.Role == SpaceRoles.Owner ||
            string.Equals(userId, requesterId, StringComparison.Ordinal))
        {
            return false;
        }

        if (role is not null &&
            role is not (SpaceRoles.Admin or SpaceRoles.Member or SpaceRoles.Guest))
        {
            return false;
        }

        var newRole = role ?? member.Role;
        var newCanPost = canPost ?? member.CanPost;
        return await _spaces.UpdateMemberAsync(spaceId, userId, newRole, newCanPost);
    }

    public async Task<bool> RemoveSpaceMember(string spaceId, string userId)
    {
        var requesterId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrWhiteSpace(spaceId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, requesterId);
        if (space is null)
        {
            return false;
        }

        var isSelf = string.Equals(requesterId, userId, StringComparison.Ordinal);
        if (!isSelf && !IsAdmin(space.MyRole))
        {
            return false;
        }

        await Groups.RemoveFromGroupAsync($"space:{spaceId}", userId);
        return await _spaces.RemoveMemberAsync(spaceId, userId);
    }

    public async Task<bool> DeleteTopic(string spaceId, string topicId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) || string.IsNullOrWhiteSpace(topicId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null)
        {
            return false;
        }

        var isAdmin = space.MyRole is SpaceRoles.Owner or SpaceRoles.Admin;
        if (!isAdmin)
        {
            return false;
        }

        return await _spaces.DeleteTopicAsync(topicId);
    }

    /// <summary>Переименовывает тему форума (владелец или администратор).</summary>
    public async Task<TopicDto?> RenameTopic(string spaceId, string topicId, string title)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(topicId) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return null;
        }

        var updated = await _spaces.RenameTopicAsync(spaceId, topicId, title.Trim());
        if (!updated)
        {
            return null;
        }

        var topic = (await _spaces.ListTopicsAsync(spaceId))
            .FirstOrDefault(item => item.Id == topicId);
        if (topic is not null)
        {
            await Clients.Group($"space:{spaceId}").SendAsync("TopicUpdated", topic);
        }

        return topic;
    }

    /// <summary>Закрывает или открывает тему форума (владелец или администратор).</summary>
    public async Task<TopicDto?> SetTopicClosed(string spaceId, string topicId, bool isClosed)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(topicId))
        {
            return null;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return null;
        }

        var updated = await _spaces.SetTopicClosedAsync(spaceId, topicId, isClosed);
        if (!updated)
        {
            return null;
        }

        var topic = (await _spaces.ListTopicsAsync(spaceId))
            .FirstOrDefault(item => item.Id == topicId);
        if (topic is not null)
        {
            await Clients.Group($"space:{spaceId}").SendAsync("TopicUpdated", topic);
        }

        return topic;
    }

    /// <summary>Закрепляет или открепляет тему форума/канал сообщества.</summary>
    public async Task<TopicDto?> SetTopicPinned(string spaceId, string topicId, bool isPinned)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(topicId))
        {
            return null;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return null;
        }

        if (!await _spaces.SetTopicPinnedAsync(spaceId, topicId, isPinned))
        {
            return null;
        }

        var topic = (await _spaces.ListTopicsAsync(spaceId))
            .FirstOrDefault(item => item.Id == topicId);
        if (topic is not null)
        {
            await Clients.Group($"space:{spaceId}").SendAsync("TopicUpdated", topic);
        }

        return topic;
    }

    /// <summary>Меняет порядок темы в списке форума (владелец или администратор).</summary>
    public async Task<TopicDto?> SetTopicPosition(string spaceId, string topicId, int position)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(spaceId) ||
            string.IsNullOrWhiteSpace(topicId))
        {
            return null;
        }

        var space = await _spaces.GetAsync(spaceId, userId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return null;
        }

        var updated = await _spaces.SetTopicPositionAsync(spaceId, topicId, Math.Max(0, position));
        if (!updated)
        {
            return null;
        }

        var topic = (await _spaces.ListTopicsAsync(spaceId))
            .FirstOrDefault(item => item.Id == topicId);
        if (topic is not null)
        {
            await Clients.Group($"space:{spaceId}").SendAsync("TopicUpdated", topic);
        }

        return topic;
    }

    private static bool IsAdmin(string role) =>
        role is SpaceRoles.Owner or SpaceRoles.Admin;

    /// <summary>Забанить пользователя на сервере (только админ через клиент, пока без отдельной админки).</summary>
    public async Task<bool> BanUser(string userId, string reason)
    {
        var requesterId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var requester = await _repository.GetUserByIdAsync(requesterId);
        if (requester is null || !requester.IsAdmin)
        {
            return false;
        }

        return await _repository.BanUserAsync(userId, requesterId, reason);
    }

    /// <summary>Разбанить пользователя на сервере.</summary>
    public async Task<bool> UnbanUser(string userId)
    {
        var requesterId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var requester = await _repository.GetUserByIdAsync(requesterId);
        if (requester is null || !requester.IsAdmin)
        {
            return false;
        }

        return await _repository.UnbanUserAsync(userId);
    }

    /// <summary>Замутить участника пространства на указанное время (мс от эпохи). 0 — снять мут.</summary>
    public async Task<bool> MuteSpaceMember(string spaceId, string userId, long mutedUntilUnixMs)
    {
        var requesterId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrWhiteSpace(spaceId) || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, requesterId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return false;
        }

        return await _spaces.MuteSpaceMemberAsync(spaceId, userId, mutedUntilUnixMs);
    }

    /// <summary>Снять мут с участника пространства.</summary>
    public async Task<bool> UnmuteSpaceMember(string spaceId, string userId)
    {
        var requesterId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrWhiteSpace(spaceId) || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var space = await _spaces.GetAsync(spaceId, requesterId);
        if (space is null || !IsAdmin(space.MyRole))
        {
            return false;
        }

        return await _spaces.UnmuteSpaceMemberAsync(spaceId, userId);
    }

    private static SendEnvelopeResult Fail(string error) =>
        new() { Accepted = false, Error = error };
}
