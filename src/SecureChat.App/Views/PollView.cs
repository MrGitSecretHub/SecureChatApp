using SecureChat.App.Services;
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
    private readonly List<string> _voters = new();

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

    public IReadOnlyList<string> Voters => _voters;

    public double Percent => TotalVotes == 0 ? 0 : Math.Round(Votes * 100.0 / TotalVotes, 1);
    public string PercentText => $"{Percent:0}%";
    public int TotalVotes { get; set; }

    public void SetVoters(IEnumerable<string> names)
    {
        _voters.Clear();
        _voters.AddRange(names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase));
        Votes = _voters.Count;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Votes));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>Модель опроса для отображения в пузыре.</summary>
public sealed class PollVm
{
    private readonly Dictionary<string, int> _votesByVoter = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Учесть голос. Имя исключает повторный голос того же участника.</summary>
    public void ApplyVote(int optionIndex, string? voter, bool mine)
    {
        if (optionIndex < 0 || optionIndex >= Options.Count) return;
        if (string.IsNullOrWhiteSpace(voter)) voter = "?";

        _votesByVoter[voter] = optionIndex;
        var names = Options.Select(_ => new List<string>()).ToList();
        foreach (var pair in _votesByVoter)
        {
            if (pair.Value >= 0 && pair.Value < names.Count) names[pair.Value].Add(pair.Key);
        }
        for (var i = 0; i < Options.Count; i++) Options[i].SetVoters(names[i]);

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

        var root = new VerticalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.Fill, MinimumWidthRequest = 240 };
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
                HorizontalOptions = LayoutOptions.Fill,
                Progress = 0
            };
            bar.SetBinding(ProgressBar.ProgressProperty, new Binding(nameof(PollOptionVm.Percent), converter: new PercentToDoubleConverter()));

            var inner = new Grid { RowDefinitions = new RowDefinitionCollection { new(), new() }, Padding = 8, RowSpacing = 2, HorizontalOptions = LayoutOptions.Fill };
            inner.Add(new Label
            {
                Text = option.Text,
                FontSize = 13,
                TextColor = (Color?)Application.Current?.Resources["TextPrimary"] ?? Colors.White,
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);
            inner.Add(bar, 0, 1);

            var voteArea = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Stroke = (Color?)Application.Current?.Resources["BorderColor"] ?? Colors.Gray,
                StrokeThickness = 1,
                BackgroundColor = Colors.Transparent,
                Padding = 0,
                HorizontalOptions = LayoutOptions.Fill,
                MinimumWidthRequest = 180,
                Content = inner,
                GestureRecognizers = { new TapGestureRecognizer { Command = option.VoteCommand } }
            };

            var percentLabel = new Label
            {
                TextColor = (Color?)Application.Current?.Resources["TextMuted"] ?? Colors.Gray,
                FontSize = 11,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.End,
                MinimumWidthRequest = 44,
                GestureRecognizers = { new TapGestureRecognizer { Command = new Command(() => ShowVoters(option)) } }
            };
            percentLabel.SetBinding(Label.TextProperty, new Binding(nameof(PollOptionVm.PercentText)));

            var outer = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(), new(GridLength.Auto) }, HorizontalOptions = LayoutOptions.Fill };
            outer.Add(voteArea, 0, 0);
            outer.Add(percentLabel, 1, 0);
            root.Add(outer);
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

    private async void ShowVoters(PollOptionVm option)
    {
        var page = Application.Current?.Windows.LastOrDefault()?.Page;
        if (page is null) return;
        var names = option.Voters.Count == 0
            ? new[] { Loc.T("poll_no_votes", "Пока нет голосов") }
            : option.Voters.ToArray();
        await page.DisplayActionSheet(option.Text, Loc.T("common_cancel", "Отмена"), null, names);
    }
}
