using SecureChat.Core.Net;

namespace SecureChat.Core.Tests;

public class ConnectionErrorTests
{
    [Theory]
    [InlineData("Нет подключения к серверу.")]
    [InlineData("Нет подключения к серверу по WebSocket. Проверьте сеть и адрес сервера.")]
    [InlineData("WebSocket недоступен, переключаюсь на TCP: сбой")]
    [InlineData("Сервер недоступен")]
    [InlineData("TCP-подключение прервано: соединение закрыто")]
    [InlineData("Connection refused")]
    public void RecognizesConnectionProblems(string message)
    {
        Assert.True(ConnectionErrors.IsConnectionProblem(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Не удалось загрузить аватар: файл повреждён")]
    [InlineData("Название не может быть пустым.")]
    [InlineData("Нет права на запись аудио. Разрешите доступ к микрофону в настройках.")]
    public void IgnoresOtherErrors(string? message)
    {
        Assert.False(ConnectionErrors.IsConnectionProblem(message));
    }

    [Fact]
    public void OfflineHint_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(ConnectionErrors.OfflineHint));
    }
}
