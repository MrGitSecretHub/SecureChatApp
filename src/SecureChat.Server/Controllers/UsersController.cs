using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecureChat.Core.Contracts;
using SecureChat.Server.Data;

namespace SecureChat.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    private readonly ChatRepository _repository;
    private readonly PresenceTracker _presence;

    public UsersController(ChatRepository repository, PresenceTracker presence)
    {
        _repository = repository;
        _presence = presence;
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var user = await _repository.GetUserByIdAsync(userId, cancellationToken);
        return user is null
            ? NotFound()
            : Ok(AuthController.ToProfile(user, _presence));
    }

    [HttpGet("{username}")]
    public async Task<IActionResult> GetByUsername(
        string username,
        CancellationToken cancellationToken)
    {
        var user = await _repository.GetUserByUsernameAsync(username, cancellationToken);
        return user is null
            ? NotFound(new ApiError { Error = "Пользователь не найден." })
            : Ok(AuthController.ToProfile(user, _presence));
    }

    /// <summary>Обновляет публичный профиль текущего пользователя.</summary>
    [HttpPut("me/profile")]
    public async Task<IActionResult> UpdateProfile(
        UpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var user = await _repository.GetUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return NotFound(new ApiError { Error = "Пользователь не найден." });
        }

        var nickname = (request.Nickname ?? string.Empty).Trim();
        if (nickname.Length > 64)
        {
            return BadRequest(new ApiError { Error = "Имя не длиннее 64 символов." });
        }

        var bio = (request.Bio ?? string.Empty).Trim();
        if (bio.Length > 500)
        {
            return BadRequest(new ApiError { Error = "Описание не длиннее 500 символов." });
        }

        var avatar = request.AvatarBase64 ?? string.Empty;
        if (avatar.Length > 512 * 1024)
        {
            return BadRequest(new ApiError { Error = "Аватар слишком большой (максимум 512 КБ)." });
        }

        var status = request.StatusMessage ?? user.StatusMessage;
        if (status is not ("Онлайн" or "Неактивен" or "Занят"))
        {
            status = "Онлайн";
        }

        await _repository.UpdateProfileAsync(
            userId,
            nickname,
            bio,
            avatar,
            status,
            cancellationToken);

        var updated = await _repository.GetUserByIdAsync(userId, cancellationToken);
        return updated is null
            ? NotFound()
            : Ok(AuthController.ToProfile(updated, _presence));
    }
}
