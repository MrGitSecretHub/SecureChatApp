using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;
using SecureChat.Server.Controllers;
using SecureChat.Server.Data;
using SecureChat.Server.Services;

namespace SecureChat.Server.Tests;

/// <summary>
/// Интеграционные тесты аккаунта: контроллер + настоящие SQLite-репозитории
/// во временном каталоге.
/// </summary>
public sealed class AccountEndpointsTests : IAsyncLifetime
{
    private const string OldPassword = "OldPass123!";
    private const string NewPassword = "NewPass456!";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "securechat-tests-" + Guid.NewGuid().ToString("N"));

    private ChatRepository _chats = null!;
    private SpaceRepository _spaces = null!;
    private PasswordHasher _hasher = null!;
    private JwtTokenService _tokens = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Chat"] = $"Data Source={Path.Combine(_directory, "test.db")}"
            })
            .Build();

        var environment = new TestHostEnvironment { ContentRootPath = _directory };

        _chats = new ChatRepository(
            configuration,
            environment,
            NullLogger<ChatRepository>.Instance);
        _spaces = new SpaceRepository(
            configuration,
            environment,
            NullLogger<SpaceRepository>.Instance);

        await _chats.InitializeAsync();
        await _spaces.InitializeAsync();

        _hasher = new PasswordHasher();
        _tokens = new JwtTokenService(new JwtSettings
        {
            Issuer = "SecureChat.Tests",
            Audience = "SecureChat.Tests",
            KeyBytes = Encoding.UTF8.GetBytes(new string('k', 64))
        });
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог может быть занят SQLite — ОС почистит его позже.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ChangePassword_ReplacesHash_AndOldSecretStopsWorking()
    {
        var (user, controller, oldSecret, newSecret) = await CreateUserAsync("alice");

        var result = await controller.ChangePassword(
            new ChangePasswordRequest
            {
                CurrentAuthSecret = oldSecret,
                NewAuthSecret = newSecret
            },
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        var updated = await _chats.GetUserByIdAsync(user.Id);
        Assert.NotNull(updated);
        Assert.True(_hasher.Verify(newSecret, updated!.AuthHash, updated.AuthSalt));
        Assert.False(_hasher.Verify(oldSecret, updated.AuthHash, updated.AuthSalt));
    }

    [Fact]
    public async Task ChangePassword_RejectsWrongCurrentSecret()
    {
        var (user, controller, _, newSecret) = await CreateUserAsync("alice");

        var result = await controller.ChangePassword(
            new ChangePasswordRequest
            {
                CurrentAuthSecret = AuthSecret.Derive("WrongPass789!", user.Username),
                NewAuthSecret = newSecret
            },
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);

        var unchanged = await _chats.GetUserByIdAsync(user.Id);
        Assert.NotNull(unchanged);
        Assert.True(_hasher.Verify(
            AuthSecret.Derive(OldPassword, user.Username),
            unchanged!.AuthHash,
            unchanged.AuthSalt));
    }

    [Fact]
    public async Task ChangePassword_RejectsSamePassword()
    {
        var (_, controller, oldSecret, _) = await CreateUserAsync("alice");

        var result = await controller.ChangePassword(
            new ChangePasswordRequest
            {
                CurrentAuthSecret = oldSecret,
                NewAuthSecret = oldSecret
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DeleteAccount_RejectsWrongPassword_AndKeepsUser()
    {
        var (user, controller, _, _) = await CreateUserAsync("bob");

        var result = await controller.DeleteAccount(
            new DeleteAccountRequest
            {
                AuthSecret = AuthSecret.Derive("WrongPass789!", user.Username)
            },
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.NotNull(await _chats.GetUserByIdAsync(user.Id));
    }

    [Fact]
    public async Task DeleteAccount_RemovesUserAndOwnedSpace()
    {
        var (user, controller, oldSecret, _) = await CreateUserAsync("bob");

        var space = await _spaces.CreateAsync(
            user.Id,
            user.Username,
            new CreateSpaceRequest
            {
                Name = "Тестовая группа",
                Type = SpaceTypes.Group
            });

        var result = await controller.DeleteAccount(
            new DeleteAccountRequest { AuthSecret = oldSecret },
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await _chats.GetUserByIdAsync(user.Id));
        Assert.Null(await _spaces.GetAsync(space.Id, user.Id));
    }

    private async Task<(UserRecord User, AuthController Controller, string OldSecret, string NewSecret)>
        CreateUserAsync(string username)
    {
        var oldSecret = AuthSecret.Derive(OldPassword, username);
        var newSecret = AuthSecret.Derive(NewPassword, username);
        var (hash, salt) = _hasher.Hash(oldSecret);

        var user = new UserRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            AuthHash = hash,
            AuthSalt = salt,
            EncryptionPublicKey = "test-encryption-key",
            SigningPublicKey = "test-signing-key",
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.True(await _chats.CreateUserAsync(user));

        var controller = new AuthController(
            _chats,
            _hasher,
            _tokens,
            NullLogger<AuthController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, user.Id) },
                        "Test"))
                }
            }
        };

        return (user, controller, oldSecret, newSecret);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SecureChat.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
