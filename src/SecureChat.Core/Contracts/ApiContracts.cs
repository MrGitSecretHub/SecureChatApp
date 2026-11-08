namespace SecureChat.Core.Contracts;

public sealed class RegisterRequest
{
    public required string Username { get; set; }
    public string? Email { get; set; }
    public required string AuthSecret { get; set; }
    public required string EncryptionPublicKey { get; set; }
    public required string SigningPublicKey { get; set; }
}

public sealed class LoginRequest
{
    public required string Username { get; set; }
    public required string AuthSecret { get; set; }
}

/// <summary>Смена пароля аккаунта: текущий и новый auth-secret.</summary>
public sealed class ChangePasswordRequest
{
    public required string CurrentAuthSecret { get; set; }
    public required string NewAuthSecret { get; set; }
}

/// <summary>Удаление аккаунта: подтверждение паролем.</summary>
public sealed class DeleteAccountRequest
{
    public required string AuthSecret { get; set; }
}

public sealed class AuthResponse
{
    public required string Token { get; set; }
    public required UserProfileDto User { get; set; }
}

public sealed class UserProfileDto
{
    public required string Id { get; set; }
    public required string Username { get; set; }
    public required string EncryptionPublicKey { get; set; }
    public required string SigningPublicKey { get; set; }
    public required string Fingerprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? OnlineStatus { get; set; } = "Онлайн";
    public string? OnlineStatusColor { get; set; } = "#3BA55D";
    public string? AvatarBase64 { get; set; }

    /// <summary>Отображаемое имя (публичный профиль).</summary>
    public string? Nickname { get; set; }

    /// <summary>Описание «о себе» (публичный профиль).</summary>
    public string? Bio { get; set; }

    /// <summary>Личный статус: Онлайн / Неактивен / Занят.</summary>
    public string? StatusMessage { get; set; } = "Онлайн";

    /// <summary>Когда пользователь был в сети (Unix, мс).</summary>
    public long LastSeenUnixMs { get; set; }
}

/// <summary>Запрос обновления публичного профиля.</summary>
public sealed class UpdateProfileRequest
{
    public string? Nickname { get; set; }
    public string? Bio { get; set; }
    public string? AvatarBase64 { get; set; }
    public string? StatusMessage { get; set; }
}

public sealed class ApiError
{
    public required string Error { get; set; }
}
