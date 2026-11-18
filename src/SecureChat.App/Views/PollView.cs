using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SecureChat.App.Views;

/// <summary>Вариант ответа в опросе.</summary>
public sealed class PollOptionVm : INotifyPropertyChanged
{
    private int _votes;
    private bool _selected;

    public PollOptionVm(string text, Action<int> onVote, int index)
    {
        Text = text;
        VoteCommand = new Command(() => onVote(index));
    }

    public string Text { get; }
    public ICommand VoteCommand { get; }

    public int Votes
    {
        get => _votes;
        set { if (_votes != value) { _votes = value; OnPropertyChanged(); OnPropertyChanged(nameof(PercentText)); } }
    }

    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; OnPropertyChanged(); } }
    }

    public double Percent => TotalVotes == 0 ? 0 : Math.Round(Votes * 100.0 / TotalVotes, 1);
    public string PercentText => $"{Percent:0}%";
    public int TotalVotes { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Refresh() { OnPropertyChanged(nameof(Votes)); OnPropertyChanged(nameof(PercentText)); OnPropertyChanged(nameof(IsSelected)); }
}

/// <summary>Модель опроса для отображения в пузыре.</summary>
public sealed class PollVm
{
    public PollVm(string id, string question, IEnumerable<string> options, Action<int> onVote)
    {
        Id = id;
        Question = question;
        var list = options.ToList();
        Options = new ObservableCollection<PollOptionVm>(
            list.Select((text, i) => new PollOptionVm(text, onVote, i)));
    }

    public string Id { get; }
    public string Question { get; }
    public ObservableCollection<PollOptionVm> Options { get; }
    public int TotalVotes => Options.Sum(o => o.Votes);

    public void ApplyVote(int optionIndex, bool mine)
    {
        if (optionIndex < 0 || optionIndex >= Options.Count) return;
        Options[optionIndex].Votes++;
        if (mine) Options[optionIndex].IsSelected = true;
        foreach (var o in Options) o.TotalVotes = TotalVotes;
        foreach (var o in Options) o.Refresh();
    }
}

/// <summary>Пузырь опроса: вопрос и варианты с процентами, тап — голос.</summary>
public sealed class PollView : ContentView
{
    public static readonly BindableProperty PollProperty =
        BindableProperty.Create(nameof(Poll), typeof(PollVm), typeof(PollView), null,
            propertyChanged: (b, _, _) => ((PollView)b).Build());

    public PollVm? Poll
    {
        get => (PollVm?)GetValue(PollProperty);
        set => SetValue(PollProperty, value);
    }

    private void Build()
    {
        Content = null;
        var poll = Poll;
        if (poll is null) return;

        var root = new VerticalStackLayout { Spacing = 6 };
        root.Add(new Label
        {
            Text = poll.Question,
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White
        });

        foreach (var option in poll.Options)
        {
            var bar = new ProgressBar
            {
                ProgressColor = (Color?)Application.Current?.Resources["Accent"] ?? Colors.CornflowerBlue,
                BackgroundColor = (Color?)Application.Current?.Resources["SurfaceAlt"] ?? Colors.Gray,
                Progress = 0
            };
            bar.SetBinding(ProgressBar.ProgressProperty, new Binding(nameof(PollOptionVm.Percent), converter: new PercentToDoubleConverter()));

            var row = new Grid { RowDefinitions = new RowDefinitionCollection { new(), new() }, Padding = 8, RowSpacing = 2 };
            row.Add(new Label
            {
                Text = option.Text,
                FontSize = 13,
                TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White,
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);
            var percentLabel = new Label
            {
                FontSize = 11,
                TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray,
                HorizontalOptions = LayoutOptions.End
            };
            percentLabel.SetBinding(Label.TextProperty, new Binding(nameof(PollOptionVm.PercentText)));
            row.Add(percentLabel, 1, 0);
            row.Add(bar, 0, 1);
            Grid.SetColumnSpan(bar, 2);

            var tap = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Stroke = (Color?)Application.Current?.Resources["BorderColor"] ?? Colors.Gray,
                StrokeThickness = 1,
                BackgroundColor = Colors.Transparent,
                Padding = 0,
                Content = row,
                GestureRecognizers = { new TapGestureRecognizer { Command = option.VoteCommand } }
            };
            root.Add(tap);
        }

        var votesLabel = new Label
        {
            FontSize = 11,
            TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray,
            Text = $"{poll.TotalVotes} votes"
        };
        root.Add(votesLabel);

        Content = root;
    }
}
