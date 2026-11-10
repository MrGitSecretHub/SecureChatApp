namespace SecureChat.App.Views;

/// <summary>
/// Вкладка «Стикеры» в стиле Telegram: крупные эмодзи-глифы (как большие
/// эмодзи в Telegram). В SecureChat наборы стикеров локальные и E2E: тап
/// отправляет сообщение Kind=Sticker с этим глифом, получатель рисует его
/// крупно. Внешние CDN-паки не используются — трафик не светится.
/// </summary>
public partial class StickerPickerView : ContentView
{
    private static readonly (string Title, string[] Glyphs)[] Categories =
    {
        ("Лица", new[]
        {
            "😂", "😍", "🥳", "😎", "🤯", "😱", "🤔", "😅", "🥹", "😭",
            "😡", "🤗", "😴", "🤡", "🫠", "🙃", "😏", "🥺"
        }),
        ("Жесты", new[]
        {
            "👍", "👎", "🙏", "👏", "💪", "🤝", "🖖", "✌️",
            "🤞", "🫶", "👌", "🤌", "☝️", "🫡"
        }),
        ("Праздник и дела", new[]
        {
            "🔥", "💥", "✨", "🎉", "🎁", "🏆", "🚀", "💎",
            "🍕", "🍺", "☕", "⚽", "🎮", "🎧", "🍀", "🌸"
        }),
        ("Животные", new[]
        {
            "🐶", "🐱", "🦊", "🐻", "🐼", "🐸", "🐵", "🦄",
            "🐙", "🦋", "🐢", "🐝", "🐷", "🐮", "🐹", "🦉"
        }),
        ("Знаки", new[]
        {
            "❤️", "💚", "💜", "💯", "⭐", "⚡", "❓", "❗",
            "✅", "❌", "🚩", "🎯", "🔒", "🔑", "💀", "👻"
        })
    };

    public StickerPickerView()
    {
        InitializeComponent();
        BuildCategories();
    }

    /// <summary>Нажали стикер — отправляем крупный глиф.</summary>
    public event Action<string>? StickerPicked;

    private void BuildCategories()
    {
        foreach (var (title, glyphs) in Categories)
        {
            Container.Children.Add(new Label
            {
                Text = title,
                FontSize = 12,
                TextColor = (Color)Application.Current!.Resources["TextMuted"],
                Margin = new Thickness(4, 6, 4, 0)
            });

            for (var start = 0; start < glyphs.Length; start += 6)
            {
                var row = new HorizontalStackLayout { Spacing = 2 };

                foreach (var glyph in glyphs.Skip(start).Take(6))
                {
                    var tile = new Label
                    {
                        Text = glyph,
                        FontSize = 30,
                        Padding = new Thickness(10, 6),
                        VerticalOptions = LayoutOptions.Center
                    };

                    tile.GestureRecognizers.Add(new TapGestureRecognizer
                    {
                        Command = new Command<string>(picked => StickerPicked?.Invoke(picked)),
                        CommandParameter = glyph
                    });

                    row.Children.Add(tile);
                }

                Container.Children.Add(row);
            }
        }
    }
}
