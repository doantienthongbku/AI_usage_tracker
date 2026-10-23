using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AIUsageTracker;

/// %LOCALAPPDATA%\AIUsageTracker\config.json. An empty wslDistro runs the CLIs natively on Windows.
record Config(string WslDistro = "Ubuntu-20.04", int RefreshMinutes = 10);

/// Tray app: three number icons (Claude, ChatGPT, Gemini), a refresh timer, and the flyout.
static class App
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "AIUsageTracker";
    static readonly string[] Brands = ["#D97757", "#10A37F", "#4796E3"];

    public static Card[] Cards = [new("Claude", "Pro", null, null, []), new("ChatGPT", "Plus", null, null, []),
                                  new("Gemini", "Google AI Pro", null, null, [])];
    public static bool Busy { get; private set; }
    static readonly Forms.NotifyIcon[] Icons = new Forms.NotifyIcon[3];
    static Config config = new();
    static Flyout flyout = null!;

    [STAThread]
    static void Main()
    {
        // Single instance. Launching it again (e.g. from the Start Menu) opens the running copy's panel.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "AIUsageTracker.Show", out var first);
        if (!first)
        {
            AllowSetForegroundWindow(-1);
            show.Set();
            return;
        }
        config = LoadConfig();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        flyout = new Flyout();
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => app.Dispatcher.Invoke(flyout.Open), null, -1, false);
        for (var i = 0; i < Icons.Length; i++)
        {
            Icons[i] = new Forms.NotifyIcon { Visible = true };
            Icons[i].MouseUp += (_, _) => flyout.Toggle();
        }
        UpdateIcons();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, config.RefreshMinutes)) };
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        app.Dispatcher.InvokeAsync(Refresh);
        app.Run();
    }

    /// Asks all three CLIs at once. A failed fetch keeps the last numbers and shows why.
    public static async Task Refresh()
    {
        if (Busy) return;
        Busy = true;
        flyout.Render();
        var d = config.WslDistro;
        var fresh = await Task.WhenAll(Task.Run(() => Usage.FetchClaude(d)), Task.Run(() => Usage.FetchCodex(d)),
                                       Task.Run(() => Usage.FetchGemini(d)));
        for (var i = 0; i < Cards.Length; i++)
            Cards[i] = fresh[i].Error == null ? fresh[i] : Cards[i] with { Error = fresh[i].Error };
        Busy = false;
        UpdateIcons();
        flyout.Render();
    }

    static void UpdateIcons()
    {
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                                      "SystemUsesLightTheme", 0) is 1;
        var tip = string.Join(" · ", Cards.Select(c => $"{c.Name} {(c.Max is { } m ? Usage.Pct(m) + "%" : "–")}"));
        if (Cards.Max(c => c.Updated) is { } updated) tip += $"\nupdated {updated.ToLocalTime():HH:mm}";
        for (var i = 0; i < Icons.Length; i++)
        {
            var max = Cards[i].Max;
            var text = max is not { } m ? "–" : m >= 100 ? "!!" : Usage.Pct(m).ToString();
            var color = Usage.Level(max) switch { 2 => "#F04438", 1 => "#F5A524", _ => light ? "#1A1A1A" : "#FFFFFF" };
            var old = Icons[i].Icon;
            Icons[i].Icon = DrawIcon(text, color, Brands[i]);
            Icons[i].Text = tip;
            old?.Dispose();
        }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    /// The tray number: bold digits in the level color, with a brand-color underline to tell the icons apart.
    static Drawing.Icon DrawIcon(string text, string color, string brand)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        using (var font = new Drawing.Font("Segoe UI", text.Length > 2 ? 15 : 22, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel))
        using (var ink = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(color)))
        using (var line = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(brand)))
        using (var center = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center })
        {
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString(text, font, ink, new Drawing.RectangleF(-6, -2, 44, 31), center);
            g.FillRectangle(line, 3, 28, 26, 4);
        }
        var handle = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    /// "Open at Login": the same HKCU Run value the installer writes.
    public static bool StartsAtLogin
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunName) != null;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunName, false);
        }
    }

    public static void Quit()
    {
        foreach (var icon in Icons) icon.Dispose();   // removes them from the tray at once
        Application.Current.Shutdown();
    }

    static Config LoadConfig()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageTracker");
        var file = Path.Combine(dir, "config.json");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, JsonSerializer.Serialize(new Config(), json));
            }
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(file), json) ?? new();
        }
        catch { return new(); }
    }
}
