using SecureChat.Core.Net;

namespace SecureChat.App.Services;

/// <summary>
/// Показ ошибок на страницах. Ошибки связи не показываются модальным окном:
/// при обрыве связи такие окна вылезают одно за другим и только раздражают.
/// Вместо них в интерфейсе уже есть постоянный индикатор офлайн-режима.
/// </summary>
public static class PageErrors
{
    /// <summary>
    /// Показывает ошибку. Для проблем с подключением — тихо, без окна.
    /// </summary>
    public static async Task ShowErrorAsync(this Page page, Exception exception, string title = "Ошибка")
    {
        if (ConnectionErrors.IsConnectionProblem(exception.Message))
        {
            return;
        }

        await page.DisplayAlert(title, exception.Message, "OK");
    }
}
