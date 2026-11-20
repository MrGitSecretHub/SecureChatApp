using QRCoder;

namespace SecureChat.App.Pages;

/// <summary>QR-код отпечатка ключа: собеседник сканирует и сравнивает.</summary>
public sealed class FingerprintQrPage : ContentPage
{
    public FingerprintQrPage(string fingerprint)
    {
        Title = "QR ключа";
        var normalized = new string(fingerprint.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var payload = "securechat:fingerprint:" + normalized;

        Image qrImage;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(12);
            qrImage = new Image
            {
                Source = ImageSource.FromStream(() => new MemoryStream(png)),
                WidthRequest = 260,
                HeightRequest = 260,
                HorizontalOptions = LayoutOptions.Center
            };
        }
        catch
        {
            qrImage = new Image { IsVisible = false };
        }

        var copy = new Button { Text = "Копировать отпечаток" };
        copy.Clicked += async (_, _) =>
        {
            await Clipboard.Default.SetTextAsync(fingerprint);
            await DisplayAlert("Готово", "Отпечаток скопирован.", "OK");
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = 24,
                Children =
                {
                    new Label
                    {
                        Text = "Сканируйте QR в другом приложении и сверьте отпечаток с собеседником.",
                        FontSize = 14,
                        LineBreakMode = LineBreakMode.WordWrap
                    },
                    qrImage,
                    new Label
                    {
                        Text = fingerprint,
                        FontSize = 13,
                        LineBreakMode = LineBreakMode.CharacterWrap,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    copy
                }
            }
        };
    }
}
