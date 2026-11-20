using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecureChat.Core.Contracts;
using SecureChat.Server.Data;

namespace SecureChat.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/stories")]
public sealed class StoriesController : ControllerBase
{
    private readonly StoryRepository _stories;
    private readonly ChatRepository _chat;

    public StoriesController(StoryRepository stories, ChatRepository chat)
    {
        _stories = stories;
        _chat = chat;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        return Ok(await _stories.GetForUserAsync(userId, cancellationToken));
    }

    [HttpPost("{id}/view")]
    public async Task<IActionResult> MarkViewed(string id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        await _stories.MarkViewedAsync(id, userId, cancellationToken);
        return Ok(new { status = "ok" });
    }

    [HttpGet("{id}/viewers")]
    public async Task<IActionResult> Viewers(string id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        return Ok(await _stories.GetViewersAsync(id, cancellationToken));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        return await _stories.DeleteOwnAsync(id, userId, cancellationToken)
            ? Ok(new { status = "ok" })
            : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Publish([FromBody] StoryPublishRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(username)) return Unauthorized();
        if (request.Envelopes.Count == 0 || request.Envelopes.Count > 500) return BadRequest("No recipients.");
        if (request.EnvelopeTotalBytes() > 32 * 1024 * 1024) return BadRequest("Too large.");

        // Не даём добавить получателей, с которыми нет relationship — сервер всё равно
        // отдаст конверт только по explicit recipient id, но проверка снижает спам.
        await _stories.PublishAsync(userId, username, request, cancellationToken);
        return Ok(new { status = "ok" });
    }
}

internal static class StoryRequestExtensions
{
    public static long EnvelopeTotalBytes(this StoryPublishRequest request) =>
        request.Envelopes.Sum(e => (long)(e.EnvelopeJson?.Length ?? 0) * 2);
}
