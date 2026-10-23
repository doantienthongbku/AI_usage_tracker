using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIUsageTracker;

/// One limit window: percent used (0–100) and when it resets.
public record Limit(double Used, DateTimeOffset? ResetsAt)
{
    /// After the reset time the old percentage is obsolete (not 0 %).
    public bool Passed => ResetsAt < DateTimeOffset.Now;
}

/// An extra line under a card, e.g. Claude's per-model week or Gemini 4 Argon.
public record Row(string Label, Limit? Five, Limit? Week);

/// Everything the tray and the flyout show for one AI.
public record Card(string Name, string Plan, Limit? Five, Limit? Week, List<Row> Extra,
                   string? Note = null, DateTimeOffset? Updated = null, string? Error = null)
{
    /// The window that stops you first. Windows that already reset don't count.
    public double? Max => new[] { Five, Week }.Where(l => l != null && !l.Passed).Max(l => (double?)l!.Used);
}

/// Where the numbers come from: each AI's own CLI (the limitbar approach), so no token is ever read here.
public static class Usage
{
    // ---------- display helpers ----------

    /// Rounded percent, but 99.6 % must never read as 100 %.
    public static int Pct(double used)
    {
        var r = (int)Math.Round(used);
        return r >= 100 && used < 100 ? 99 : r;
    }

    /// 0 = normal, 1 = amber (≥ 80 %), 2 = red (≥ 90 %), judged on the number the user sees.
    public static int Level(double? used) => used is not { } u ? 0 : Pct(u) >= 90 ? 2 : Pct(u) >= 80 ? 1 : 0;

    /// "23:09" today, "Thu 06:59" within a week, "5 Nov" later.
    public static string When(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var days = (local.Date - DateTime.Today).TotalDays;
        return local.ToString(days == 0 ? "HH:mm" : days is > 0 and < 7 ? "ddd HH:mm" : "d MMM", CultureInfo.InvariantCulture);
    }

    // ---------- fetch: run the CLI, parse its answer ----------

    public static Task<Card> FetchClaude(string distro) => Fetch("Claude", "claude", distro,
        ["-p", "/usage", "--output-format", "json", "--max-turns", "1", "--no-session-persistence",
         "--strict-mcp-config", "--settings", """{"disableAllHooks":true}"""],
        o => ParseClaude(o, DateTimeOffset.Now));

    // codex app-server speaks JSON-RPC on stdin/stdout and keeps running, so we stop at the answer to id 2.
    public static Task<Card> FetchCodex(string distro) => Fetch("ChatGPT", "codex", distro, ["app-server"],
        o => ParseCodex(o.Split('\n').FirstOrDefault(l => l.Contains("\"id\":2")) ?? o),
        input: """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"ai-usage-tracker","version":"1"}}}""" + "\n" +
               """{"method":"initialized"}""" + "\n" +
               """{"id":2,"method":"account/rateLimits/read"}""" + "\n",
        until: "\"id\":2");

    public static Task<Card> FetchGemini(string distro) => Fetch("Gemini", "agy", distro,
        ["-p", "/quota", "--output-format", "json", "--print-timeout", "30s"], ParseGemini);

    static async Task<Card> Fetch(string name, string cli, string distro, string[] args, Func<string, Card> parse,
                                  string? input = null, string? until = null)
    {
        var (output, errors) = ("", "");
        try
        {
            (output, errors) = await Run(distro, cli, args, input, until);
            return parse(output) with { Updated = DateTimeOffset.Now };
        }
        catch
        {
            return new Card(name, "", null, null, [], Error: Problem(cli, output + errors));
        }
    }

    /// Runs a CLI (inside WSL when a distro is set) and returns its stdout and stderr.
    /// With `until`, it stops at the first stdout line containing that text.
    internal static async Task<(string, string)> Run(string distro, string cli, string[] args, string? input, string? until)
    {
        var psi = new ProcessStartInfo(distro == "" ? cli : "wsl.exe")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        };
        if (distro != "")
            foreach (var a in new[] { "-d", distro, "--cd", "~", "-e", "sh", "-c", "PATH=\"$HOME/.local/bin:$PATH\" exec \"$@\"", "sh", cli })
                psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var errors = p.StandardError.ReadToEndAsync(timeout.Token);
        var output = new StringBuilder();
        try
        {
            if (input == null) p.StandardInput.Close();
            else
            {
                await p.StandardInput.WriteAsync(input);
                await p.StandardInput.FlushAsync();
            }
            while (await p.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                output.AppendLine(line);
                if (until != null && line.Contains(until)) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { p.StandardInput.Close(); if (!p.WaitForExit(3000)) p.Kill(true); } catch { }
        }
        var err = "";
        try { err = await errors.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        return (output.ToString(), err);
    }

    static readonly string[] SignedOut = ["not logged in", "not signed in", "please log in", "please sign in", "login required",
        "unauthenticated", "unauthorized", "no credentials", "not authenticated", "authentication required"];

    /// One fixed line explaining a failed fetch. The CLI's own text is only matched, never shown.
    public static string Problem(string cli, string output) =>
        SignedOut.Any(output.ToLowerInvariant().Contains)
            ? $"Not signed in — run `{cli}` and sign in"
            : "No data (timeout or output changed)";

    // ---------- parsers (pure, unit-tested) ----------

    static readonly Regex ClaudeLine = new(
        @"^Current (session|week \((.+?)\)):\s*(\d+(?:\.\d+)?)%\s*used(?:\s*·\s*resets\s+(.+?)\s*\(([^)]+)\))?",
        RegexOptions.Multiline);

    /// `claude -p /usage` JSON: the `result` text has lines like
    /// "Current session: 18% used · resets Oct 22, 11:09pm (Asia/Singapore)".
    public static Card ParseClaude(string output, DateTimeOffset now)
    {
        var text = (string?)JsonNode.Parse(output)?["result"] ?? "";
        Limit? five = null, week = null;
        var extra = new List<Row>();
        foreach (Match m in ClaudeLine.Matches(text))
        {
            var reset = m.Groups[4].Success ? ClaudeDate(m.Groups[4].Value, m.Groups[5].Value, now) : null;
            var limit = new Limit(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), reset);
            if (m.Groups[1].Value == "session") five = limit;
            else if (m.Groups[2].Value == "all models") week = limit;
            else extra.Add(new Row(m.Groups[2].Value, null, limit));
        }
        if (five == null && week == null) throw new FormatException("no plan limits");
        return new Card("Claude", "Pro", five, week, extra);
    }

    /// "Oct 22, 11:09pm", "Oct 24, 11pm", "Oct 21 at 9:10pm" or just "11pm" in an IANA zone. Takes the next date that fits.
    static DateTimeOffset? ClaudeDate(string s, string zone, DateTimeOffset now)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(zone); } catch { tz = TimeZoneInfo.Local; }
        s = s.Replace(" at ", ", ").ToUpperInvariant();
        var inv = CultureInfo.InvariantCulture;
        var local = TimeZoneInfo.ConvertTime(now, tz);
        foreach (var year in new[] { local.Year, local.Year + 1 })
            if (DateTime.TryParseExact($"{s} {year}", ["MMM d, h:mmtt yyyy", "MMM d, htt yyyy"], inv, DateTimeStyles.None, out var d))
            {
                var t = new DateTimeOffset(d, tz.GetUtcOffset(d));
                if (t > now.AddDays(-1)) return t.ToUniversalTime();
            }
        if (DateTime.TryParseExact(s, ["h:mmtt", "htt"], inv, DateTimeStyles.NoCurrentDateDefault, out var tod))
        {
            var t = new DateTimeOffset(local.Date + tod.TimeOfDay, local.Offset);
            return (t < now ? t.AddDays(1) : t).ToUniversalTime();
        }
        return null;
    }

    /// `codex app-server` answer to `account/rateLimits/read`.
    public static Card ParseCodex(string output)
    {
        var result = JsonNode.Parse(output)?["result"] ?? throw new FormatException("no result");
        var limits = result["rateLimits"] ?? throw new FormatException("no rate limits");
        Limit? five = null, week = null;
        foreach (var w in new[] { limits["primary"], limits["secondary"] })
        {
            if (w?["usedPercent"] is not { } used) continue;
            var limit = new Limit((double)used, w["resetsAt"] is { } at ? DateTimeOffset.FromUnixTimeSeconds((long)at) : null);
            if ((int?)w["windowDurationMins"] <= 300) five = limit; else week = limit;
        }
        if (five == null && week == null) throw new FormatException("no rate limits");

        var plan = (string?)limits["planType"] is { Length: > 0 } p ? char.ToUpperInvariant(p[0]) + p[1..] : "";
        var credits = (result["rateLimitResetCredits"]?["credits"] as JsonArray ?? new JsonArray())
            .Where(c => (string?)c?["status"] == "available" && c?["expiresAt"] != null)
            .Select(c => DateTimeOffset.FromUnixTimeSeconds((long)c!["expiresAt"]!))
            .Order().ToList();
        var note = credits.Count == 0 ? null
            : $"{credits.Count} free full reset{(credits.Count == 1 ? "" : "s")} · next expires {When(credits[0])}";
        return new Card("ChatGPT", plan, five, week, [], note);
    }

    /// `agy -p /quota` JSON. "Gemini Models" fills the columns. Other Gemini groups (e.g. Argon) become extra rows.
    public static Card ParseGemini(string output)
    {
        var groups = JsonNode.Parse(output)?["command"]?["data"]?["groups"] as JsonArray
                     ?? throw new FormatException("no quota");
        var rows = new List<Row>();
        foreach (var g in groups)
        {
            var name = (string?)g?["name"] ?? "";
            if (!name.Contains("Gemini", StringComparison.OrdinalIgnoreCase)) continue;
            Limit? five = null, week = null;
            foreach (var b in g!["buckets"] as JsonArray ?? new JsonArray())
            {
                if (b?["remaining_fraction"] is not { } left) continue;
                var reset = DateTimeOffset.TryParse((string?)b["reset_time"], CultureInfo.InvariantCulture, out var t) ? t : (DateTimeOffset?)null;
                var limit = new Limit((1 - (double)left) * 100, reset);
                if ((string?)b["window"] == "5h") five = limit; else week = limit;
            }
            rows.Add(new Row(name, five, week));
        }
        var main = rows.Find(r => r.Label == "Gemini Models") ?? rows.FirstOrDefault();
        if (main?.Five == null && main?.Week == null) throw new FormatException("no Gemini quota");
        rows.Remove(main!);
        return new Card("Gemini", "Google AI Pro", main!.Five, main.Week, rows);
    }
}
