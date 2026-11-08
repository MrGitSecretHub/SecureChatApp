using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecureChat.Core.Contracts;
using SecureChat.Server.Data;

namespace SecureChat.Server.Controllers;

/// <summary>
/// Приём и выдача зашифрованных вложений. Сервер хранит только шифротекст:
/// ключ и имя файла передаются внутри E2E-конверта или содержимого сообщения.
/// Вложение пространства доступно участникам этого пространства.
/// </summary>
[ApiController]
[Authorize]
[Route("api/media")]
public sealed class MediaController : ControllerBase
{
    private const int MaxRequestBytes = MediaLimits.MaxBytes + 64 * 1024;
    private const string SpacePrefix = "space:";

    private readonly ChatRepository _repository;
    private readonly SpaceRepository _spaces;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        ChatRepository repository,
        SpaceRepository spaces,
        ILogger<MediaController> logger)
    {
        _repository = repository;
        _spaces = spaces;
        _logger = logger;
    }

    [HttpPost("{mediaId}")]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Upload(
        string mediaId,
        [FromQuery] string? recipientId,
        [FromQuery] string? spaceId,
        CancellationToken cancellationToken)
    {
        var senderId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(senderId))
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(mediaId, out _))
        {
            return BadRequest(new ApiError { Error = "Некорректный идентификатор вложения." });
        }

        string storedRecipient;
        if (!string.IsNullOrWhiteSpace(spaceId))
        {
            var members = await _spaces.GetMemberIdsAsync(spaceId, cancellationToken);
            if (!members.Contains(senderId))
            {
                return NotFound(new ApiError { Error = "Пространство не найдено." });
            }

            storedRecipient = SpacePrefix + spaceId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(recipientId) ||
                string.Equals(recipientId, senderId, StringComparison.Ordinal))
            {
                return BadRequest(new ApiError { Error = "Некорректный получатель вложения." });
            }

            var recipient = await _repository.GetUserByIdAsync(recipientId, cancellationToken);
            if (recipient is null)
            {
                return NotFound(new ApiError { Error = "Получатель не найден." });
            }

            storedRecipient = recipientId;
        }

        if (Request.ContentLength is > MaxRequestBytes)
        {
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new ApiError { Error = "Вложение слишком большое." });
        }

        var ciphertext = await ReadLimitedAsync(
            Request.Body,
            MediaLimits.MaxBytes,
            cancellationToken);
        if (ciphertext is null || ciphertext.Length == 0)
        {
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new ApiError { Error = "Вложение слишком большое или пустое." });
        }

        var media = new MediaRecord
        {
            Id = mediaId,
            SenderId = senderId,
            RecipientId = storedRecipient,
            Size = ciphertext.Length,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var finalPath = _repository.GetMediaPath(mediaId);
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await System.IO.File.WriteAllBytesAsync(tempPath, ciphertext, cancellationToken);

            if (!await _repository.CreateMediaAsync(media, cancellationToken))
            {
                return Conflict(new ApiError
                {
                    Error = "Вложение с таким идентификатором уже существует."
                });
            }

            System.IO.File.Move(tempPath, finalPath);
            _logger.LogInformation(
                "Вложение {MediaId} сохранено: {Size} байт, получатель {Recipient}",
                mediaId,
                ciphertext.Length,
                storedRecipient);

            return Ok(new { id = mediaId, size = ciphertext.Length });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    [HttpGet("{mediaId}")]
    public async Task<IActionResult> Download(
        string mediaId,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(mediaId, out _))
        {
            return NotFound();
        }

        var media = await _repository.GetMediaAsync(mediaId, cancellationToken);
        if (media is null || !await CanReadAsync(media, userId, cancellationToken))
        {
            // Не раскрываем существование чужого вложения.
            return NotFound();
        }

        var path = _repository.GetMediaPath(mediaId);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, "application/octet-stream", enableRangeProcessing: true);
    }

    private async Task<bool> CanReadAsync(MediaRecord media, string userId, CancellationToken cancellationToken)
    {
        if (media.RecipientId.StartsWith(SpacePrefix, StringComparison.Ordinal))
        {
            var spaceId = media.RecipientId[SpacePrefix.Length..];
            var members = await _spaces.GetMemberIdsAsync(spaceId, cancellationToken);
            return members.Contains(userId);
        }

        return string.Equals(media.SenderId, userId, StringComparison.Ordinal) ||
               string.Equals(media.RecipientId, userId, StringComparison.Ordinal);
    }

    private static async Task<byte[]?> ReadLimitedAsync(
        Stream body,
        int limit,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
