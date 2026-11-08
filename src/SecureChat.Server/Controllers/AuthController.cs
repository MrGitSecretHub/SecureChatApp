using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;
using SecureChat.Server.Data;
using SecureChat.Server.Services;

namespace SecureChat.Server.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController : ControllerBase
{
    private static readonly Regex UsernamePattern =
        new("^[A-Za-z0-9_]{3,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ChatRepository _repository;
    private readonly PasswordHasher _hasher;
    private readonly JwtTokenService _tokens;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        ChatRepository repository,
        PasswordHasher hasher,
        JwtTokenService tokens,
        ILogger<AuthController> logger)
    {
        _repository = repository;
        _hasher = hasher;
        _tokens = tokens;
        _logger = logger;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim() ?? string.Empty;

        if (!UsernamePattern.IsMatch(username))
        {
            return BadRequest(new ApiError
            {
                Error = "Имя пользователя: 3–32 символа, только латиница, цифры и «_»."
            });
        }

        var email = string.IsNullOrWhiteSpace(request.Email)
            ? null
            : request.Email.Trim();

        if (email is not null)
        {
            if (!IsValidEmail(email))
            {
                return BadRequest(new ApiError { Error = "Некорректный адрес электронной почты." });
            }

            if (await _repository.GetUserByEmailAsync(email, cancellationToken) is not null)
            {
                return Conflict(new ApiError { Error = "Эта почта уже зарегистрирована." });
            }
        }

        if (!IsValidAuthSecret(request.AuthSecret))
        {
            return BadRequest(new ApiError { Error = "Некорректный auth-secret." });
        }

        if (!PublicKeyValidator.IsValidEncryptionKey(request.EncryptionPublicKey))
        {
            return BadRequest(new ApiError { Error = "Некорректный ключ шифрования ECDH." });
        }

        if (!PublicKeyValidator.IsValidSigningKey(request.SigningPublicKey))
        {
            return BadRequest(new ApiError { Error = "Некорректный ключ подписи ECDSA." });
        }

        if (await _repository.GetUserByUsernameAsync(username, cancellationToken) is not null)
        {
            return Conflict(new ApiError { Error = "Это имя уже занято." });
        }

        var (hash, salt) = _hasher.Hash(request.AuthSecret);

        var user = new UserRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            Email = email,
            AuthHash = hash,
            AuthSalt = salt,
            EncryptionPublicKey = request.EncryptionPublicKey,
            SigningPublicKey = request.SigningPublicKey,
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (!await _repository.CreateUserAsync(user, cancellationToken))
        {
            return Conflict(new ApiError { Error = "Это имя уже занято." });
        }

        _logger.LogInformation("Зарегистрирован пользователь {Username} ({Email})", username, email);

        return Ok(new AuthResponse
        {
            Token = _tokens.CreateToken(user),
            User = ToProfile(user)
        });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var login = request.Username?.Trim() ?? string.Empty;

        UserRecord? user;
        if (login.Contains('@') && IsValidEmail(login))
        {
            user = await _repository.GetUserByEmailAsync(login, cancellationToken);
        }
        else
        {
            user = await _repository.GetUserByUsernameAsync(login, cancellationToken);
        }

        if (user is null || !IsValidAuthSecret(request.AuthSecret))
        {
            _hasher.VerifyDummy(request.AuthSecret ?? string.Empty);
            return Unauthorized(new ApiError { Error = "Неверное имя пользователя/почта или пароль." });
        }

        if (!_hasher.Verify(request.AuthSecret, user.AuthHash, user.AuthSalt))
        {
            return Unauthorized(new ApiError { Error = "Неверное имя пользователя/почта или пароль." });
        }

        return Ok(new AuthResponse
        {
            Token = _tokens.CreateToken(user),
            User = ToProfile(user)
        });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
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
            return NotFound();
        }

        if (!IsValidAuthSecret(request.CurrentAuthSecret) ||
            !IsValidAuthSecret(request.NewAuthSecret))
        {
            return BadRequest(new ApiError { Error = "Некорректный auth-secret." });
        }

        if (!_hasher.Verify(request.CurrentAuthSecret, user.AuthHash, user.AuthSalt))
        {
            return Unauthorized(new ApiError { Error = "Текущий пароль неверен." });
        }

        if (string.Equals(
                request.CurrentAuthSecret,
                request.NewAuthSecret,
                StringComparison.Ordinal))
        {
            return BadRequest(new ApiError
            {
                Error = "Новый пароль должен отличаться от текущего."
            });
        }

        var (hash, salt) = _hasher.Hash(request.NewAuthSecret);
        if (!await _repository.UpdateUserPasswordAsync(user.Id, hash, salt, cancellationToken))
        {
            return NotFound();
        }

        _logger.LogInformation("Пользователь {Username} сменил пароль", user.Username);
        return NoContent();
    }

    /// <summary>Удаляет аккаунт и все связанные данные без возможности восстановления.</summary>
    [HttpPost("delete-account")]
    [Authorize]
    public async Task<IActionResult> DeleteAccount(
        [FromBody] DeleteAccountRequest request,
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
            return NotFound();
        }

        if (!IsValidAuthSecret(request.AuthSecret) ||
            !_hasher.Verify(request.AuthSecret, user.AuthHash, user.AuthSalt))
        {
            return Unauthorized(new ApiError { Error = "Неверный пароль." });
        }

        if (!await _repository.DeleteUserAsync(user.Id, cancellationToken))
        {
            return NotFound();
        }

        _logger.LogInformation("Аккаунт {Username} удалён", user.Username);
        return NoContent();
    }

    private static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value;
        }
        catch
        {
            return false;
        }
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var user = await _repository.GetUserByIdAsync(userId, cancellationToken);
        return user is null ? NotFound() : Ok(ToProfile(user));
    }

    public static UserProfileDto ToProfile(UserRecord user, PresenceTracker? presence = null)
    {
        var (status, color) = presence?.GetStatus(user) ?? ("Онлайн", "#3BA55D");

        return new UserProfileDto
        {
            Id = user.Id,
            Username = user.Username,
            EncryptionPublicKey = user.EncryptionPublicKey,
            SigningPublicKey = user.SigningPublicKey,
            Fingerprint = SecureChat.Core.Crypto.Fingerprint.Compute(user.SigningPublicKey),
            CreatedAt = user.CreatedAt,
            Nickname = user.Nickname,
            Bio = user.Bio,
            AvatarBase64 = string.IsNullOrEmpty(user.AvatarBase64) ? null : user.AvatarBase64,
            StatusMessage = user.StatusMessage,
            OnlineStatus = status,
            OnlineStatusColor = color,
            LastSeenUnixMs = user.LastSeenUnixMs
        };
    }

    private static bool IsValidAuthSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
