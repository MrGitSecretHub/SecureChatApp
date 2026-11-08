namespace SecureChat.App.Views;

/// <summary>
/// Компактная эмодзи-панель над полем ввода: категории с сеткой
/// эмодзи, нажатие поднимает событие <see cref="EmojiPicked"/>.
/// </summary>
public partial class EmojiPickerView : ContentView
{
    private static readonly (string Title, string[] Emojis)[] Categories =
    {
        ("Часто используемые", new[]
        {
            "😀", "😁", "😂", "🤣", "😊", "😍", "😘", "😎", "🤔", "😅",
            "😢", "😭", "😡", "👍", "👎", "👌", "🙏", "👏", "💪", "🔥",
            "❤️", "💙", "💚", "⭐", "🎉", "🎁", "✅", "❌", "⚡", "💥"
        }),
        ("Люди", new[]
        {
            "🙂", "😉", "😇", "🥰", "🤩", "😋", "😜", "🤪", "🤗", "🤭",
            "🤫", "😐", "😴", "🤤", "😷", "🤒", "🥶", "🥵", "😱", "😨",
            "😰", "😬", "🙄", "😮", "😲", "🥳", "🥺", "😤", "🤠", "😈"
        }),
        ("Природа и животные", new[]
        {
            "🌞", "🌙", "🌈", "☁️", "❄️", "🌊", "🌪️", "🌸", "🌻", "🌹",
            "🍀", "🌵", "🌴", "🍎", "🍌", "🍓", "🍇", "🍉", "🐶", "🐱",
            "🦊", "🐻", "🐼", "🐨", "🦁", "🐮", "🐷", "🐸", "🦄", "🦋"
        }),
        ("Еда и напитки", new[]
        {
            "🍕", "🍔", "🍟", "🌮", "🍣", "🍜", "🍚", "🍲", "🥪", "🥗",
            "🍰", "🍪", "🍫", "🍬", "☕", "🍺", "🥂", "🥤", "🥛", "🍳",
            "🥐", "🍝", "🦞", "🥝"
        }),
        ("Активность и отдых", new[]
        {
            "⚽", "🏀", "🎾", "🏈", "🎮", "🎲", "🎯", "🏆", "🥇", "🥈",
            "🥉", "🏃", "🚴", "🧘", "🎿", "🏂", "🏋️", "🤸", "🎪", "🎬",
            "🎨", "🎸", "🎤", "🎧", "🎭"
        }),
        ("Предметы", new[]
        {
            "⌚", "📱", "💻", "⌨️", "🖥️", "🖨️", "📷", "🎥", "📞", "🔋",
            "💡", "🔧", "🔨", "🧪", "🧬", "💊", "🧭", "🗺️", "📎", "✂️",
            "📌", "📝", "📚", "🔑", "🔒"
        }),
        ("Символы", new[]
        {
            "🧡", "💛", "💜", "🖤", "🤍", "💯", "♻️", "🚩", "⚠️", "☢️",
            "➕", "➖", "✖️", "➗", "❓", "❗", "💬", "🏁", "🚩", "💠"
        })
    };

    public EmojiPickerView()
    {
        InitializeComponent();
        BuildCategories();
    }

    /// <summary>Пользователь нажал эмодзи — вставить в поле ввода.</summary>
    public event Action<string>? EmojiPicked;

    private void BuildCategories()
    {
        foreach (var (title, emojis) in Categories)
        {
            Container.Children.Add(new Label
            {
                Text = title,
                FontSize = 12,
                TextColor = (Color)Application.Current!.Resources["TextMuted"],
                Margin = new Thickness(4, 6, 4, 0)
            });

            var list = emojis.Distinct().ToList();

            // Сетка строками по 8 эмодзи — без FlexLayout для надёжности.
            for (var start = 0; start < list.Count; start += 8)
            {
                var row = new HorizontalStackLayout { Spacing = 2 };

                for (var i = start; i < Math.Min(start + 8, list.Count); i++)
                {
                    var picked = list[i];
                    var label = new Label
                    {
                        Text = picked,
                        FontSize = 24,
                        Padding = new Thickness(6, 4),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    };

                    label.GestureRecognizers.Add(new TapGestureRecognizer
                    {
                        Command = new Command(() => EmojiPicked?.Invoke(picked))
                    });

                    row.Children.Add(label);
                }

                Container.Children.Add(row);
            }
        }
    }
}
