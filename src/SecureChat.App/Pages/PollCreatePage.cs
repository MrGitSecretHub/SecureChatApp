using System.Text.Json;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>Создание опроса: вопрос и до четырёх вариантов ответа.</summary>
public sealed class PollCreatePage : ContentPage
{
    private readonly Entry _question = new() { Placeholder = "Question" };
    private readonly List<Entry> _options = new();

    public event EventHandler<PollData>? PollCreated;

    public PollCreatePage()
    {
        Title = "New poll";

        var layout = new VerticalStackLayout { Spacing = 12, Padding = 16 };
        layout.Add(new Label { Text = "Question", FontAttributes = FontAttributes.Bold });
        _question.Placeholder = "Ask a question";
        layout.Add(_question);

        layout.Add(new Label { Text = "Options", FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) });
        for (var i = 0; i < 4; i++)
        {
            var entry = new Entry { Placeholder = $"Option {i + 1}{(i < 2 ? " *" : string.Empty)}" };
            _options.Add(entry);
            layout.Add(entry);
        }

        var create = new Button { Text = "Create poll", HorizontalOptions = LayoutOptions.Fill };
        create.Clicked += OnCreateClicked;
        layout.Add(create);

        Content = new ScrollView { Content = layout };
    }

    private async void OnCreateClicked(object? sender, EventArgs e)
    {
        var question = _question.Text?.Trim() ?? string.Empty;
        var options = _options.Select(o => o.Text?.Trim() ?? string.Empty)
            .Where(o => !string.IsNullOrEmpty(o)).ToList();
        if (string.IsNullOrEmpty(question) || options.Count < 2)
        {
            await DisplayAlert("Poll", "Enter a question and at least two options.", "OK");
            return;
        }

        PollCreated?.Invoke(this, new PollData
        {
            Id = Guid.NewGuid().ToString("N"),
            Question = question,
            Options = options
        });
        await Navigation.PopAsync();
    }
}
