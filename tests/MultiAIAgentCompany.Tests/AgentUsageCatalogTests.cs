using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>残量を CLI の出力から読む（設計 §50）。</summary>
/// <remarks><b>2026-09-13 の実機出力をそのまま固定する。</b> 境界値の検証だけ別の入力を使う（§39 / §50）。</remarks>
public sealed class AgentUsageCatalogTests
{
    private const string ClaudeOutput =
        """
        {"type": "result", "subtype": "success", "is_error": false, "num_turns": 0, "result": "You are currently using your subscription to power your Claude Code usage\n\nCurrent session: 70% used · resets Sep 13 at 3:30pm (Asia/Tokyo)\nCurrent week (all models): 30% used · resets Sep 19 at 4am (Asia/Tokyo)\n\nWhat's contributing to your limits usage?\nApproximate, based on local sessions on this machine — does not include other devices or claude.ai. Behaviors are independent characteristics, not a breakdown.\n\nLast 24h · 792 requests · 20 sessions\n  95% of your usage came from sessions active for 8+ hours\n  95% of your usage was at >150k context\n\nLast 7d · 2897 requests · 91 sessions\n  97% of your usage came from sessions active for 8+ hours\n  94% of your usage was at >150k context"}
        """;

    private const string CodexOutput =
        """
        {"id":2,"result":{"rateLimits":{"limitId":"codex","limitName":null,"primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1789294479},"secondary":{"usedPercent":24,"windowDurationMins":10080,"resetsAt":1789806137},"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"individualLimit":null,"spendControlReached":false,"planType":"plus","rateLimitReachedType":null}}}
        """;

    private const string AntigravityOutput =
        """
        {"conversation_id": "", "status": "SUCCESS", "response": "Gemini Models\tWeekly Limit Remaining\t97%\t2026-09-18T17:17:46Z\nGemini Models\tFive Hour Limit Remaining\t96%\t2026-09-13T10:15:03Z\nClaude and GPT models\tWeekly Limit Remaining\t100%\t2026-09-20T05:26:14Z\nClaude and GPT models\tFive Hour Limit Remaining\t100%\t2026-09-13T10:26:14Z\n", "duration_seconds": 0, "num_turns": 0, "usage": {"input_tokens": 0, "output_tokens": 0, "thinking_tokens": 0, "cache_read_tokens": 0, "total_tokens": 0}, "command": {"name": "usage", "data": {"description": "Within each group, models share a weekly limit and a 5-hour limit.", "groups": [{"name": "Gemini Models", "description": "Models within this group: Gemini Flash, Gemini Pro", "buckets": [{"id": "gemini-weekly", "name": "Weekly Limit Remaining", "window": "weekly", "remaining_fraction": 0.9704716205596924, "reset_time": "2026-09-18T17:17:46Z"}, {"id": "gemini-5h", "name": "Five Hour Limit Remaining", "window": "5h", "remaining_fraction": 0.962273895740509, "reset_time": "2026-09-13T10:15:03Z"}]}, {"name": "Claude and GPT models", "description": "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS", "buckets": [{"id": "3p-weekly", "name": "Weekly Limit Remaining", "window": "weekly", "remaining_fraction": 1, "reset_time": "2026-09-20T05:26:14Z"}, {"id": "3p-5h", "name": "Five Hour Limit Remaining", "window": "5h", "remaining_fraction": 1, "reset_time": "2026-09-13T10:26:14Z"}]}]}}}
        """;

    [Fact]
    public void Claude_は2枠を読み利用傾向の行は採らない()
    {
        var windows = AgentUsageCatalog.ParseClaude(ClaudeOutput);
        Assert.Equal(2, windows.Count);
        Assert.Equal("5時間枠", windows[0].Name);
        Assert.Equal(30, windows[0].RemainingPercent);
        Assert.Equal("Sep 13 at 3:30pm (Asia/Tokyo)", windows[0].ResetText);
        Assert.Equal("週枠（全モデル）", windows[1].Name);
        Assert.Equal(70, windows[1].RemainingPercent);
        Assert.All(windows, window => { Assert.Null(window.Group); Assert.Null(window.ResetsAt); });
    }

    [Fact]
    public void Codex_は2枠とUnix秒を読む()
    {
        var windows = AgentUsageCatalog.ParseCodex(CodexOutput);
        Assert.Equal(2, windows.Count);
        Assert.Equal("5時間枠", windows[0].Name);
        Assert.Equal(90, windows[0].RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789294479), windows[0].ResetsAt);
        Assert.Equal("週枠", windows[1].Name);
        Assert.Equal(76, windows[1].RemainingPercent);
        Assert.All(windows, window => { Assert.Null(window.Group); Assert.Null(window.ResetText); });
    }

    [Fact]
    public void Antigravity_はモデル群ごとに4枠を読む()
    {
        var windows = AgentUsageCatalog.ParseAntigravity(AntigravityOutput);
        Assert.Equal(4, windows.Count);
        Assert.Equal(new[] { "Gemini Models", "Claude and GPT models" }, windows.Select(w => w.Group).Distinct());
        var fiveHour = Assert.Single(windows, w => w.Group == "Gemini Models" && w.Name == "5時間枠");
        Assert.Equal(96.2, fiveHour.RemainingPercent, 1);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 10, 15, 3, TimeSpan.Zero), fiveHour.ResetsAt);
        Assert.Equal(2, windows.Count(w => w.Name == "週枠"));
        Assert.All(windows, window => Assert.Null(window.ResetText));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"result\":[]}")]
    [InlineData("{\"result\":{\"rateLimits\":{\"primary\":5,\"secondary\":[]}}}")]
    [InlineData("{\"status\":\"SUCCESS\",\"command\":{\"data\":{\"groups\":[null,5,{\"name\":\"group\",\"buckets\":[null,42,{}]}]}}}")]
    public void JSONでない入力も形が違うJSONも空で返す(string output)
    {
        Assert.Empty(AgentUsageCatalog.ParseClaude(output));
        Assert.Empty(AgentUsageCatalog.ParseCodex(output));
        Assert.Empty(AgentUsageCatalog.ParseAntigravity(output));
    }

    [Fact]
    public void CLIが失敗を返したら枠を採らない()
    {
        Assert.Empty(AgentUsageCatalog.ParseClaude(ClaudeOutput.Replace("\"is_error\": false", "\"is_error\": true")));
        Assert.Empty(AgentUsageCatalog.ParseAntigravity(AntigravityOutput.Replace("SUCCESS", "ERROR")));
        Assert.Empty(AgentUsageCatalog.ParseCodex("""{"id":2,"error":{"message":"unauthorized"}}"""));
    }

    [Fact]
    public void 残りを0から100に丸め込む()
    {
        Assert.Equal(0, AgentUsageCatalog.ParseClaude(ClaudeOutput.Replace("70% used", "120% used"))[0].RemainingPercent);
        Assert.Equal(0, AgentUsageCatalog.ParseCodex(CodexOutput.Replace("\"usedPercent\":10", "\"usedPercent\":120"))[0].RemainingPercent);
        Assert.Equal(100, AgentUsageCatalog.ParseCodex(CodexOutput.Replace("\"usedPercent\":10", "\"usedPercent\":-20"))[0].RemainingPercent);
        Assert.Equal(0, AgentUsageCatalog.ParseAntigravity(AntigravityOutput.Replace("0.9704716205596924", "-0.2"))[0].RemainingPercent);
        Assert.Equal(100, AgentUsageCatalog.ParseAntigravity(AntigravityOutput.Replace("0.9704716205596924", "1.2"))[0].RemainingPercent);
    }

    [Fact]
    public void Claude_はモデル別の週枠と未知の名前を残す()
    {
        var windows = AgentUsageCatalog.ParseClaude(
            """{"result":"Current week (Sonnet): 12.5% used\nExtra usage: 20% used"}""");
        Assert.Equal("週枠（Sonnet）", windows[0].Name);
        Assert.Equal(87.5, windows[0].RemainingPercent);
        Assert.Null(windows[0].ResetText);
        Assert.Equal("Extra usage", windows[1].Name);
    }

    [Fact]
    public void Codex_はnull枠を飛ばし未知の時間幅も表示する()
    {
        var window = Assert.Single(AgentUsageCatalog.ParseCodex(
            """{"result":{"rateLimits":{"primary":null,"secondary":{"usedPercent":12.5,"windowDurationMins":60}}}}"""));
        Assert.Equal("60分枠", window.Name);
        Assert.Equal(87.5, window.RemainingPercent);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void Antigravity_は未知の枠ならbucketの名前を使う()
    {
        var windows = AgentUsageCatalog.ParseAntigravity(AntigravityOutput.Replace("\"window\": \"weekly\"", "\"window\": \"unknown\""));
        Assert.Equal("Weekly Limit Remaining", windows[0].Name);
    }

    [Fact]
    public void 数値やリセット時刻の型が違っても例外を投げない()
    {
        Assert.Empty(AgentUsageCatalog.ParseCodex(
            """{"result":{"rateLimits":{"primary":{"usedPercent":"10","windowDurationMins":300}}}}"""));
        Assert.Empty(AgentUsageCatalog.ParseCodex(CodexOutput.Replace("\"usedPercent\":10", "\"usedPercent\":1e999").Replace("\"usedPercent\":24", "\"usedPercent\":null")));
        Assert.Null(AgentUsageCatalog.ParseCodex(CodexOutput.Replace("1789294479", "9223372036854775807"))[0].ResetsAt);
        Assert.Null(AgentUsageCatalog.ParseAntigravity(AntigravityOutput.Replace("2026-09-18T17:17:46Z", "invalid"))[0].ResetsAt);
    }

    [Fact]
    public async Task 呼び出し側のキャンセルは起動前にそのまま返す()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentUsageCatalog.ReadAsync(AgentKind.ClaudeCode, cancellation.Token));
    }
}
