using SecureChat.App.Services;
using SecureChat.Core.Contracts;

namespace SecureChat.App.Pages;

/// <summary>Создание опроса: вопрос и до четырёх вариантов ответа.</summary>
public sealed class PollCreatePage : ContentPage
{
    private readonly Entry _question = new();
    private readonly List<Entry> _options = new();

    public event EventHandler<PollData>? PollCreated;

    public PollCreatePage()
    {
        Title = Loc.T("poll_create_title", "New poll");

        var layout = new VerticalStackLayout { Spacing = 12, Padding = 16 };
        layout.Add(new Label { Text = Loc.T("poll_question", "Question"), FontAttributes = FontAttributes.Bold });
        _question.Placeholder = Loc.T("poll_question_hint", "Ask a question");
        layout.Add(_question);

        layout.Add(new Label
        {
            Text = Loc.T("poll_options", "Options"),
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 8, 0, 0)
        });
        for (var i = 0; i < 4; i++)
        {
            var entry = new Entry
            {
                Placeholder = string.Format(
                    Loc.T("poll_option_hint", "Option {0}"), i + 1) +
                    (i < 2 ? " *" : string.Empty)
            };
            _options.Add(entry);
            layout.Add(entry);
        }

        var create = new Button
        {
            Text = Loc.T("poll_create", "Create poll"),
            HorizontalOptions = LayoutOptions.Fill
        };
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
            await DisplayAlert(
                Loc.T("poll_create_title", "New poll"),
                Loc.T("poll_need_two", "Enter a question and at least two options."),
                "OK");
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
