using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>モデル一覧の読み取り（設計 §47-2）。</summary>
public sealed class AgentModelCatalogTests
{
    [Fact]
    public void 一覧を出せるのは_Antigravity_だけ()
    {
        // **取れないものを、それらしく埋めない**（§7）。Claude と Codex は自由入力のまま。
        Assert.True(AgentModelCatalog.CanList(AgentKind.AntigravityCli));
        Assert.False(AgentModelCatalog.CanList(AgentKind.ClaudeCode));
        Assert.False(AgentModelCatalog.CanList(AgentKind.CodexCli));
    }

    [Fact]
    public void 実物の出力から_id_と表示名を読む()
    {
        // 2026-09-13 に実機で取った出力（先頭に進捗の行が混じる）。
        var output = string.Join("\n",
        [
            "Fetching available models...",
            "gemini-3.8-flash-high\tGemini 3.8 Flash (High)",
            "gemini-3.1-pro-low\tGemini 3.1 Pro (Low)",
            "claude-opus-4-6-thinking\tClaude Opus 4.6 (Thinking)",
        ]);

        var choices = AgentModelCatalog.Parse(output);

        Assert.Equal(3, choices.Count);
        Assert.Equal("gemini-3.8-flash-high", choices[0].Id);
        Assert.Equal("Gemini 3.8 Flash (High)", choices[0].Label);
    }

    [Fact]
    public void 形に合わない行は拾わない()
    {
        // **知らない行を id として扱わない。** 進捗やエラーが混じっても壊れない。
        Assert.Empty(AgentModelCatalog.Parse("Fetching available models...\nerror: something\n\n"));
    }
}
