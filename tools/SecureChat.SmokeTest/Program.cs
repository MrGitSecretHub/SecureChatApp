using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SecureChat.Core.Contracts;
using SecureChat.Core.Crypto;

var serverUrl = (args.FirstOrDefault() ?? "http://localhost:5080").TrimEnd('/');
var suffix = Guid.NewGuid().ToString("N")[..6];
var aliceName = $"alice_{suffix}";
var bobName = $"bob_{suffix}";
var password = "correct horse battery staple";

Console.WriteLine($"Сервер: {serverUrl}");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

async Task<AuthResponse> RegisterAsync(string username, IdentityKeys identity)
{
    var request = new RegisterRequest
    {
        Username = username,
        AuthSecret = AuthSecret.Derive(password, username),
        EncryptionPublicKey = identity.EncryptionPublicKey,
        SigningPublicKey = identity.SigningPublicKey
    };

    using var response = await http.PostAsJsonAsync($"{serverUrl}/api/auth/register", request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"Регистрация не удалась: {response.StatusCode} {body}");
    }

    return JsonSerializer.Deserialize<AuthResponse>(
        body,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}

var alice = IdentityKeys.Create();
var bob = IdentityKeys.Create();

var aliceAuth = await RegisterAsync(aliceName, alice);
var bobAuth = await RegisterAsync(bobName, bob);
Console.WriteLine($"Зарегистрированы: {aliceName}, {bobName}");

async Task<HubConnection> ConnectAsync(string token)
{
    var connection = new HubConnectionBuilder()
        .WithUrl($"{serverUrl}/hubs/chat", options =>
        {
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .WithAutomaticReconnect()
        .Build();

    await connection.StartAsync();
    return connection;
}

await using var aliceHub = await ConnectAsync(aliceAuth.Token);
await using var bobHub = await ConnectAsync(bobAuth.Token);
Console.WriteLine("Оба клиента подключены к SignalR.");

async Task<string> SendAliceMessageAsync(string text)
{
    var id = Guid.NewGuid().ToString("N");
    var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    using var signingKey = alice.CreateSigningKey();
    var envelope = MessageCrypto.Encrypt(
        id,
        aliceAuth.User.Id,
        aliceAuth.User.Username,
        bobAuth.User.Id,
        bobAuth.User.Username,
        sentAt,
        text,
        bob.EncryptionPublicKey,
        signingKey);

    var result = await aliceHub.InvokeAsync<SendEnvelopeResult>("SendEnvelope", envelope);
    if (!result.Accepted)
    {
        throw new InvalidOperationException($"Сервер отклонил сообщение: {result.Error}");
    }

    return id;
}

var received = new TaskCompletionSource<EncryptedEnvelope>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var delivered = new TaskCompletionSource<string[]>(
    TaskCreationOptions.RunContinuationsAsynchronously);

bobHub.On<EncryptedEnvelope>("ReceiveEnvelope", envelope =>
{
    received.TrySetResult(envelope);
});

aliceHub.On<string[]>("MessagesDelivered", ids =>
{
    delivered.TrySetResult(ids);
});

var messageId = Guid.NewGuid().ToString("N");
var sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
using (var aliceSigningKey = alice.CreateSigningKey())
{
    var envelope = MessageCrypto.Encrypt(
        messageId,
        aliceAuth.User.Id,
        aliceAuth.User.Username,
        bobAuth.User.Id,
        bobAuth.User.Username,
        sentAt,
        "Привет из smoke-теста! Это E2E-сообщение.",
        bob.EncryptionPublicKey,
        aliceSigningKey);

    var result = await aliceHub.InvokeAsync<SendEnvelopeResult>("SendEnvelope", envelope);
    if (!result.Accepted)
    {
        throw new InvalidOperationException($"Сервер отклонил сообщение: {result.Error}");
    }
}

var incoming = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
var plaintext = MessageCrypto.Decrypt(
    incoming,
    bob.EncryptionPrivateKey,
    alice.SigningPublicKey);

if (plaintext != "Привет из smoke-теста! Это E2E-сообщение.")
{
    throw new InvalidOperationException("Расшифрованный текст не совпадает.");
}

Console.WriteLine($"Получено и расшифровано: «{plaintext}»");

var ackCount = await bobHub.InvokeAsync<int>("AckMessages", new[] { incoming.Id });
if (ackCount != 1)
{
    throw new InvalidOperationException("Подтверждение доставки не учтено.");
}

var deliveredIds = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
if (!deliveredIds.Contains(incoming.Id))
{
    throw new InvalidOperationException("Отправитель не получил подтверждение доставки.");
}

Console.WriteLine("Подтверждение доставки получено отправителем.");

// --- Проверка удаления и очистки переписки (версия 1.1) -------------------

var messageDeleted = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var conversationDeleted = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var conversationCleared = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);

bobHub.On<string>("PrivateMessageDeleted", id => messageDeleted.TrySetResult(id));
bobHub.On<string>("PrivateConversationDeleted", userId => conversationDeleted.TrySetResult(userId));
bobHub.On<string>("PrivateConversationCleared", userId => conversationCleared.TrySetResult(userId));

// 1. Удаление сообщения для всех: собеседник должен получить уведомление.
var deletableId = await SendAliceMessageAsync("Сообщение, которое удалят у всех.");
var deletedOk = await aliceHub.InvokeAsync<bool>("DeletePrivateMessage", deletableId);
if (!deletedOk)
{
    throw new InvalidOperationException("Сервер не удалил сообщение для всех.");
}

var notifiedId = await messageDeleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
if (!string.Equals(notifiedId, deletableId, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Собеседник получил уведомление о другом сообщении.");
}

Console.WriteLine("Удаление сообщения для всех: собеседник уведомлён.");

// 2. Повторное удаление того же сообщения должно вернуть false.
if (await aliceHub.InvokeAsync<bool>("DeletePrivateMessage", deletableId))
{
    throw new InvalidOperationException("Повторное удаление сообщения вернуло true.");
}

// 3. Удалить чужое сообщение нельзя.
var foreignId = await SendAliceMessageAsync("Сообщение, которое попробует удалить собеседник.");
if (await bobHub.InvokeAsync<bool>("DeletePrivateMessage", foreignId))
{
    throw new InvalidOperationException("Собеседник смог удалить чужое сообщение.");
}

// 4. Очистка истории для всех: чат остаётся, сообщения исчезают.
await SendAliceMessageAsync("Сообщение перед очисткой истории.");
var clearedOk = await aliceHub.InvokeAsync<bool>("ClearPrivateConversation", bobName);
if (!clearedOk)
{
    throw new InvalidOperationException("Сервер не очистил историю переписки.");
}

var clearedBy = await conversationCleared.Task.WaitAsync(TimeSpan.FromSeconds(10));
if (!string.Equals(clearedBy, aliceAuth.User.Id, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Уведомление об очистке пришло от неверного пользователя.");
}

Console.WriteLine("Очистка истории для всех: собеседник уведомлён.");

// 5. Удаление чата для всех: переписка исчезает у обеих сторон.
await SendAliceMessageAsync("Сообщение перед удалением чата.");
var conversationDeletedOk = await aliceHub.InvokeAsync<bool>("DeletePrivateConversation", bobName);
if (!conversationDeletedOk)
{
    throw new InvalidOperationException("Сервер не удалил переписку.");
}

var deletedBy = await conversationDeleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
if (!string.Equals(deletedBy, aliceAuth.User.Id, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Уведомление об удалении чата пришло от неверного пользователя.");
}

Console.WriteLine("Удаление чата для всех: собеседник уведомлён.");

Console.WriteLine("SMOKE TEST PASSED");
return 0;
