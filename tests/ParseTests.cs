using AIUsageTracker;
using Xunit;

// Fixtures are real CLI answers captured on 2026-10-22, with account and credit IDs removed.
public class ParseTests
{
    const string ClaudeOut = """
        {"type":"result","subtype":"success","is_error":false,"num_turns":0,"total_cost_usd":0,
         "result":"You are currently using your subscription to power your Claude Code usage\n\nCurrent session: 18% used · resets Oct 22, 11:09pm (Asia/Singapore)\nCurrent week (all models): 61% used · resets Oct 23, 6:59am (Asia/Singapore)\nCurrent week (Opus): 4% used · resets Oct 23, 6:59am (Asia/Singapore)\n\nWhat's contributing to your limits usage?\nLast 24h · 209 requests · 3 sessions\n  85%  Start fresh when you switch tasks · run /clear"}
        """;

    const string CodexOut = """
        {"id":2,"result":{"ordinaryUsageAllowed":true,"rateLimits":{"limitId":"codex","limitName":null,
         "primary":{"usedPercent":81,"windowDurationMins":300,"resetsAt":1792672417},
         "secondary":{"usedPercent":13,"windowDurationMins":10080,"resetsAt":1793259217},
         "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"planType":"plus"},
         "rateLimitResetCredits":{"availableCount":2,"credits":[
           {"id":"x","status":"available","expiresAt":1794007669},
           {"id":"y","status":"available","expiresAt":1793291830},
           {"id":"z","status":"used","expiresAt":1793000000}]}}}
        """;

    const string GeminiOut = """
        {"name":"usage","command":{"name":"usage","data":{"groups":[
          {"name":"Gemini Models","buckets":[
            {"id":"gemini-weekly","window":"weekly","remaining_fraction":0.22,"reset_time":"2026-10-29T12:07:32Z"},
            {"id":"gemini-5h","window":"5h","remaining_fraction":1,"reset_time":"2026-10-22T17:07:32Z"}]},
          {"name":"Claude and GPT models","buckets":[
            {"id":"3p-weekly","window":"weekly","remaining_fraction":1,"reset_time":"2026-10-29T12:07:32Z"}]},
          {"name":"Gemini 4 Argon","buckets":[
            {"id":"gemini-argon-wtu-weekly","window":"weekly","remaining_fraction":1,"reset_time":"2026-10-29T12:07:32Z"}]}]}}}
        """;

    // `agy -p /credits`, captured on 2026-10-24.
    const string GeminiCreditsOut = """
        {"conversation_id":"","status":"SUCCESS","response":"Remaining credits\t0\nUpgrade\thttps://antigravity.google/g1-upgrade\n",
         "command":{"name":"credits","data":{"remaining_credits":0,"upgrade_uri":"https://antigravity.google/g1-upgrade"}}}
        """;

    static readonly DateTimeOffset Now =new(2026, 10, 22, 20, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Claude_reads_session_week_and_model_weeks()
    {
        var c = Usage.ParseClaude(ClaudeOut, Now);
        Assert.Equal(18, c.Five!.Used);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 15, 9, 0, TimeSpan.Zero), c.Five.ResetsAt);
        Assert.Equal(61, c.Week!.Used);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 22, 59, 0, TimeSpan.Zero), c.Week.ResetsAt);
        var opus = Assert.Single(c.Extra);
        Assert.Equal("Opus", opus.Label);
        Assert.Equal(4, opus.Week!.Used);
    }

    [Fact]
    public void Claude_reset_date_rolls_into_next_year()
    {
        var json = """{"result":"Current session: 5% used · resets Jan 1, 1am (Asia/Singapore)"}""";
        var c = Usage.ParseClaude(json, new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero), c.Five!.ResetsAt);
    }

    [Fact]
    public void Codex_reads_windows_plan_and_reset_credits()
    {
        var c = Usage.ParseCodex(CodexOut);
        Assert.Equal(81, c.Five!.Used);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792672417), c.Five.ResetsAt);
        Assert.Equal(13, c.Week!.Used);
        Assert.Equal("Plus", c.Plan);
        Assert.Equal("⟳  2 free full resets · expire " + Usage.When(DateTimeOffset.FromUnixTimeSeconds(1793291830)) + ", " +
                     Usage.When(DateTimeOffset.FromUnixTimeSeconds(1794007669)), c.Note);
    }

    [Fact]
    public void Codex_shows_paid_credits_and_counts_resets_that_never_expire()
    {
        var c = Usage.ParseCodex(CodexOut.Replace("\"balance\":\"0\"", "\"balance\":\"25\"")
            .Replace("\"status\":\"used\",\"expiresAt\":1793000000", "\"status\":\"available\",\"expiresAt\":null"));
        var lines = c.Note!.Split('\n');
        Assert.StartsWith("⟳  3 free full resets · expire ", lines[0]);
        Assert.Equal("◎  25 credits left", lines[1]);
    }

    [Fact]
    public void Codex_has_no_note_without_credits() =>
        Assert.Null(Usage.ParseCodex(CodexOut.Replace("\"status\":\"available\"", "\"status\":\"used\"")).Note);

    [Fact]
    public void Gemini_credits_show_only_above_zero()
    {
        Assert.Null(Usage.GeminiCredits(GeminiCreditsOut));
        Assert.Equal("◎  250 AI credits left",
                     Usage.GeminiCredits(GeminiCreditsOut.Replace("\"remaining_credits\":0", "\"remaining_credits\":250")));
    }

    [Fact]
    public void Gemini_reads_main_group_and_other_gemini_groups_only()
    {
        var c = Usage.ParseGemini(GeminiOut);
        Assert.Equal(0, c.Five!.Used);
        Assert.Equal(78, c.Week!.Used, 3);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T12:07:32Z"), c.Week.ResetsAt);
        var argon = Assert.Single(c.Extra);
        Assert.Equal("Gemini 4 Argon", argon.Label);
        Assert.Null(argon.Five);
        Assert.Equal(0, argon.Week!.Used);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"result":"You are using an API key"}""")]
    [InlineData("""{"id":2,"result":{}}""")]
    [InlineData("""{"command":{"data":{"groups":[]}}}""")]
    public void Unusable_output_throws(string output)
    {
        Assert.ThrowsAny<Exception>(() => Usage.ParseClaude(output, Now));
        Assert.ThrowsAny<Exception>(() => Usage.ParseCodex(output));
        Assert.ThrowsAny<Exception>(() => Usage.ParseGemini(output));
    }

    [Fact]
    public void Problem_tells_signed_out_from_other_failures()
    {
        Assert.StartsWith("Not signed in", Usage.Problem("codex", "Error: Not logged in. Run codex login"));
        Assert.StartsWith("No data", Usage.Problem("agy", "segfault"));
    }

    [Theory]
    [InlineData(79.4, 0)]
    [InlineData(79.6, 1)]
    [InlineData(80, 1)]
    [InlineData(89, 1)]
    [InlineData(90, 2)]
    [InlineData(100, 2)]
    public void Level_turns_amber_at_80_and_red_at_90(double used, int level) =>
        Assert.Equal(level, Usage.Level(used));

    [Fact]
    public void Percent_never_rounds_up_to_100()
    {
        Assert.Equal(99, Usage.Pct(99.6));
        Assert.Equal(100, Usage.Pct(100));
    }

    [Fact]
    public void Shown_picks_the_chosen_window()
    {
        var c = new Card("X", "", new Limit(30, null), new Limit(70, null), []);
        Assert.Equal(30, c.Shown("5h"));
        Assert.Equal(70, c.Shown("week"));
        Assert.Equal(70, c.Shown("max"));
        Assert.Null((c with { Week = null }).Shown("week"));
    }

    [Fact]
    public void Shown_ignores_windows_that_already_reset()
    {
        var past = DateTimeOffset.Now.AddMinutes(-1);
        var c = new Card("X", "", new Limit(95, past), new Limit(40, null), []);
        Assert.Equal(40, c.Shown("max"));
        Assert.Null(c.Shown("5h"));
    }

    [Theory]
    [InlineData(18, 82)]
    [InlineData(99.6, 1)]
    [InlineData(100, 0)]
    [InlineData(120, 0)]
    public void Left_is_100_minus_shown_percent(double used, int left) =>
        Assert.Equal(left, Usage.Left(used));
}

public class RunTests
{
    [Fact]
    public async Task Run_returns_output_of_a_cli_that_needs_no_input()
    {
        var (output, _) = await Usage.Run("", "dotnet", ["--version"], null, null);
        Assert.Matches(@"^\d+\.\d+", output);
    }
}
