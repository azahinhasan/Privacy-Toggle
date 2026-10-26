using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PrivacyToggle.Views;

/// <summary>
/// One "Right now" card (Microphone, Camera or Network): a header with a status pill, the apps using the
/// resource now (at most <see cref="MaxRows"/> until expanded in place), an idle line when there are none,
/// and a "Today" footer with an action button. Live cards take the resource colour for their border and
/// hard shadow; idle cards get a muted border and no shadow.
/// </summary>
public sealed class ResourceCard : Border
{
    public const int MaxRows = 3;

    private readonly string _colorKey, _onTextKey, _name;
    private readonly TextBlock _pillText = new() { FontSize = 11, FontWeight = FontWeights.Black };
    private readonly Border _pill;
    private readonly StackPanel _sub = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _more;
    private readonly TextBlock _idle = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 2), FontSize = 13 };
    private readonly TextBlock _total = new() { FontSize = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _apps = new() { FontSize = 12, Margin = new Thickness(6, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _action;
    private List<FrameworkElement> _allRows = new();

    /// <summary>"Show N more" was used: all rows are shown. Reset when the page is left.</summary>
    public bool Expanded { get; private set; }

    public event Action? ActionClicked;

    public ResourceCard(string name, FrameworkElement glyph, string colorKey, string onTextKey)
    {
        (_name, _colorKey, _onTextKey) = (name, colorKey, onTextKey);
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(2);
        Padding = new Thickness(16, 14, 16, 14);
        Margin = new Thickness(0, 0, 12, 14); // right/bottom: room for the hard shadow and the gap
        VerticalAlignment = VerticalAlignment.Top; // an expanded card doesn't stretch its neighbours
        SetResourceReference(BackgroundProperty, "CardBg");

        var panel = new StackPanel();

        // header: glyph + name in the resource colour, status pill on the right
        var header = new DockPanel();
        _pill = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), Child = _pillText, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_pill, Dock.Right);
        header.Children.Add(_pill);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(glyph);
        var titleText = new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        title.Children.Add(titleText);
        header.Children.Add(title);
        panel.Children.Add(header);

        panel.Children.Add(_sub);
        panel.Children.Add(_rows);

        _more = new Button { Style = (Style)Application.Current.FindResource("DashedButton"), Margin = new Thickness(0, 6, 0, 2) };
        _more.SetResourceReference(Control.ForegroundProperty, colorKey);
        _more.Click += (_, _) =>
        {
            Expanded = !Expanded;
            ShowRows();
        };
        panel.Children.Add(_more);

        _idle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        panel.Children.Add(_idle);

        var divider = new Border { Height = 1.5, Margin = new Thickness(0, 12, 0, 10) };
        divider.SetResourceReference(BackgroundProperty, "SectionDivider");
        panel.Children.Add(divider);

        var footer = new DockPanel(); // the button keeps its size; the totals take the rest and trim if they must
        _action = new Button { Style = (Style)Application.Current.FindResource("SubtleButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 2) };
        _action.Click += (_, _) => ActionClicked?.Invoke();
        DockPanel.SetDock(_action, Dock.Right);
        footer.Children.Add(_action);
        var today = new StackPanel();
        var todayLabel = new TextBlock { Text = "Today", FontSize = 11, FontWeight = FontWeights.SemiBold };
        todayLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        today.Children.Add(todayLabel);
        var totals = new DockPanel();
        Tabular(_total);
        DockPanel.SetDock(_total, Dock.Left);
        totals.Children.Add(_total);
        _apps.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        totals.Children.Add(_apps);
        today.Children.Add(totals);
        footer.Children.Add(today);
        panel.Children.Add(footer);

        Child = panel;
        AutomationProperties.SetName(this, name);
        SetLive(false);
        ShowRows(); // no rows yet: hides the "Show more" button
    }

    /// <summary>Live: border and hard shadow in the resource colour. Idle: muted border, no shadow.</summary>
    public void SetLive(bool live)
    {
        if (live)
        {
            SetResourceReference(BorderBrushProperty, _colorKey);
            var color = (TryFindResource(_colorKey) as SolidColorBrush)?.Color ?? Colors.Gray;
            Effect = new DropShadowEffect { BlurRadius = 0, ShadowDepth = 4.2, Direction = 315, Color = color, Opacity = 1 };
        }
        else
        {
            SetResourceReference(BorderBrushProperty, "IdleBorder");
            Effect = null;
        }
    }

    /// <summary>The status pill: coloured when live ("LIVE · 2 APPS"), grey otherwise ("OFF", "QUIET").</summary>
    public void SetPill(string text, bool live)
    {
        _pillText.Text = text;
        _pill.SetResourceReference(BackgroundProperty, live ? _colorKey : "PillOffBg");
        _pillText.SetResourceReference(TextBlock.ForegroundProperty, live ? _onTextKey : "PillOffText");
        AutomationProperties.SetName(_pill, $"{_name}: {text}");
    }

    /// <summary>Extra line under the header (the network card's total rates); null hides it.</summary>
    public void SetSubHeader(UIElement? content)
    {
        _sub.Children.Clear();
        if (content != null) _sub.Children.Add(content);
        _sub.Visibility = content == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The apps using the resource now, in order; only the first <see cref="MaxRows"/> unless expanded.</summary>
    public void SetRows(List<FrameworkElement> rows)
    {
        _allRows = rows;
        ShowRows();
    }

    private void ShowRows()
    {
        if (_allRows.Count <= MaxRows) Expanded = false;
        _rows.Children.Clear();
        foreach (var row in Expanded ? _allRows : _allRows.Take(MaxRows)) _rows.Children.Add(row);
        int hidden = _allRows.Count - MaxRows;
        _more.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        _more.Content = Expanded ? "Show less" : $"Show {hidden} more app{(hidden == 1 ? "" : "s")}";
        AutomationProperties.SetHelpText(_more, Expanded ? "expanded" : "collapsed");
    }

    public void CollapseRows()
    {
        Expanded = false;
        ShowRows();
    }

    /// <summary>The line shown when no app is using the resource; null hides it.</summary>
    public void SetIdle(string? text)
    {
        _idle.Text = text ?? "";
        _idle.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetFooter(string total, string apps, string action, bool actionEnabled)
    {
        _total.Text = total;
        _apps.Text = apps;
        _action.Content = action;
        _action.IsEnabled = actionEnabled;
    }

    /// <summary>Fixed-width digits, so timers and rates don't jitter as they change.</summary>
    public static T Tabular<T>(T text) where T : TextBlock
    {
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }
}
