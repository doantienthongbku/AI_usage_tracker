using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AIUsageTracker;

/// The detail panel: one block per AI with "5 hours" and "Week" columns, styled after reference.jpg.
public partial class Flyout : Window
{
    static readonly Brush Grey = Hex("#9AA3B2"), White = Hex("#F2F2F2"), Amber = Hex("#F5A524"), Red = Hex("#F04438");
    static readonly string[] MarkPaths = [Marks.Claude, Marks.OpenAI, Marks.Gemini];
    static readonly string[] MarkColors = ["#D97757", "#F2F2F2", "#4796E3"];
    DateTime hiddenAt;

    public Flyout()
    {
        InitializeComponent();
        Deactivated += (_, _) => { Hide(); hiddenAt = DateTime.Now; };
        SizeChanged += (_, _) => Place();
        KeyDown += async (_, e) =>
        {
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (e.Key == Key.Escape) Hide();
            else if (ctrl && e.Key == Key.R) await App.Refresh();
            else if (ctrl && e.Key == Key.Q) App.Quit();
        };
    }

    /// Tray click. A click that just closed the panel (by taking focus away) must not reopen it.
    public void Toggle()
    {
        if (IsVisible) Hide();
        else if ((DateTime.Now - hiddenAt).TotalMilliseconds > 300) Open();
    }

    public void Open()
    {
        Render();
        Place();
        Show();
        Activate();
        // Fade the content in. Window.Opacity would need AllowsTransparency, which turns off the blur.
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    /// Bottom-right, just above the taskbar, like Windows' own tray flyouts.
    void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - (ActualHeight > 0 ? ActualHeight : 400) - 12;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
    record struct AccentPolicy(int State, int Flags, int Color, int Animation);
    struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

    /// The glass tint: navy #1C2433 at 60 %, written as 0xAABBGGRR.
    const int Tint = unchecked((int)0x9933241C);

    /// Frosted glass like the Opus 5.5 panel: Windows blurs what is behind the panel under our navy tint
    /// (acrylic accent, the tunable route), and rounds the corners, which also adds its shadow and edge.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, edge = 0x00685A4E;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));    // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE: round
        DwmSetWindowAttribute(hwnd, 34, ref edge, sizeof(int));    // DWMWA_BORDER_COLOR: soft slate edge

        var accent = new AccentPolicy(4, 0, Tint, 0);   // ACCENT_ENABLE_ACRYLICBLURBEHIND
        var data = new CompositionData { Attribute = 19, Size = Marshal.SizeOf<AccentPolicy>() };   // WCA_ACCENT_POLICY
        data.Data = Marshal.AllocHGlobal(data.Size);
        Marshal.StructureToPtr(accent, data.Data, false);
        var glass = SetWindowCompositionAttribute(hwnd, ref data);
        Marshal.FreeHGlobal(data.Data);

        if (glass) HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        else Root.Background = Hex("#F21C2230");
    }

    public void Render()
    {
        RefreshText.Text = App.Busy ? "Refreshing…" : "Refresh Now";
        LoginSwitch.IsChecked = App.StartsAtLogin;
        CardsPanel.Children.Clear();
        CardsPanel.Children.Add(Line(Text("Limits", White, bold: true, size: 14), Text("5 hours", Grey), Text("Week", Grey)));
        for (var i = 0; i < App.Cards.Length; i++)
        {
            CardsPanel.Children.Add(new Border { Height = 1, Background = Hex("#1AFFFFFF"), Margin = new Thickness(0, 8, 0, 8) });
            CardsPanel.Children.Add(Block(App.Cards[i], i));
        }
    }

    static StackPanel Block(Card c, int i)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(MarkPaths[i]), Fill = Hex(MarkColors[i]), Stretch = Stretch.Uniform,
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        name.Children.Add(Text(c.Name, White, bold: true, size: 13.5));

        // When the last fetch failed, the numbers are the previous ones: dim them.
        var numbers = new StackPanel { Opacity = c.Error == null ? 1 : 0.55 };
        numbers.Children.Add(Line(name, Used(c.Five), Used(c.Week)));
        numbers.Children.Add(Line(Text(c.Plan, Grey), Bar(c.Five), Bar(c.Week)));
        numbers.Children.Add(Line(Text(Updated(c), Grey), Text(Resets(c.Five), Grey), Text(Resets(c.Week), Grey)));
        foreach (var row in c.Extra) numbers.Children.Add(Line(Text(row.Label, Grey), Mini(row.Five), Mini(row.Week)));
        if (c.Note != null) numbers.Children.Add(Wrapped(Text("⟳  " + c.Note, Grey)));

        var block = new StackPanel();
        block.Children.Add(numbers);
        if (c.Error != null) block.Children.Add(Wrapped(Text(c.Error, Amber)));
        return block;
    }

    /// One row of the 3-column table: label | 5 hours | Week.
    static Grid Line(UIElement label, UIElement five, UIElement week)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition());
        UIElement[] cells = [label, five, week];
        for (var col = 0; col < 3; col++)
        {
            Grid.SetColumn(cells[col], col);
            g.Children.Add(cells[col]);
        }
        return g;
    }

    static UIElement Used(Limit? l)
    {
        if (l == null || l.Passed) return Text(l == null ? "—" : "reset", Grey);
        var t = new TextBlock();
        t.Inlines.Add(new Run($"{Usage.Pct(l.Used)}%") { FontWeight = FontWeights.SemiBold, FontSize = 13.5, Foreground = Level(l.Used) });
        t.Inlines.Add(new Run(" used") { Foreground = Grey });
        return t;
    }

    static UIElement Bar(Limit? l) =>
        l == null || l.Passed ? new Border() : new ProgressBar { Value = l.Used, Foreground = Level(l.Used) };

    /// A compact "4% ▬▬" cell for the extra rows.
    static UIElement Mini(Limit? l)
    {
        if (l == null || l.Passed) return new Border();
        var cell = new DockPanel();
        var pct = Text($"{Usage.Pct(l.Used)}%", Level(l.Used));
        pct.Width = 38;
        cell.Children.Add(pct);
        cell.Children.Add(new ProgressBar { Value = l.Used, Foreground = Level(l.Used), VerticalAlignment = VerticalAlignment.Center });
        return cell;
    }

    static string Resets(Limit? l) => l is { ResetsAt: { } t, Passed: false } ? "resets " + Usage.When(t) : "";

    static string Updated(Card c)
    {
        if (c.Updated is not { } u) return c.Error == null ? "loading…" : "";
        var min = (int)(DateTimeOffset.Now - u).TotalMinutes;
        return "updated " + (min < 1 ? "just now" : min < 60 ? $"{min} min ago" : $"{min / 60} h ago");
    }

    static Brush Level(double used) => Usage.Level(used) switch { 2 => Red, 1 => Amber, _ => White };

    static TextBlock Text(string s, Brush color, bool bold = false, double size = 12.5) => new()
    {
        Text = s, Foreground = color, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock Wrapped(TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 3, 0, 0);
        return t;
    }

    static Brush Hex(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    async void Refresh_Click(object sender, RoutedEventArgs e) => await App.Refresh();
    /// Also fires when Render() syncs the switch from the registry, so write only on a real change.
    void Login_Changed(object sender, RoutedEventArgs e)
    {
        var on = LoginSwitch.IsChecked == true;
        if (App.StartsAtLogin != on) App.StartsAtLogin = on;
    }
    void Quit_Click(object sender, RoutedEventArgs e) => App.Quit();
}
