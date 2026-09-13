using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// モデルと思考の強さの候補を、CLI から読む（設計 §48）。
/// </summary>
/// <remarks>
/// <b>実機の出力から固定する。</b> 2026-09-13 に3つの CLI を叩いて取った形を使う ——
/// こちらで作った例で固定すると、**本物と別のものを読めるようにしてしまう**（§39 と同じ姿勢）。
/// </remarks>
public sealed class AgentModelCatalogTests
{
    [Fact]
    public void モデルの一覧を出せるのは_Codex_と_Antigravity()
    {
        // **Claude は出せない**（カタログが実行ファイルの中。叩いて確かめた）。
        Assert.True(AgentModelCatalog.CanListModels(AgentKind.CodexCli));
        Assert.True(AgentModelCatalog.CanListModels(AgentKind.AntigravityCli));
        Assert.False(AgentModelCatalog.CanListModels(AgentKind.ClaudeCode));
    }

    [Fact]
    public void Codex_はモデルごとの強さまで返す()
    {
        var choices = AgentModelCatalog.ParseCodex(
            """
            {"models":[
              {"slug":"gpt-5.6-terra","display_name":"GPT-5.6-Terra","visibility":"list",
               "default_reasoning_level":"medium",
               "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},
                                             {"effort":"xhigh"},{"effort":"max"},{"effort":"ultra"}]},
              {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list",
               "supported_reasoning_levels":[{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"xhigh"}]}
            ]}
            """);

        Assert.Equal(2, choices.Count);
        Assert.Equal("gpt-5.6-terra", choices[0].Id);
        Assert.Equal("GPT-5.6-Terra", choices[0].Label);

        // **モデルによって使える強さが違う。** 一律の候補を出すと、
        // 「設定できるが起動しない組み合わせ」を作れてしまう（§47-2）。
        Assert.Contains("ultra", choices[0].Efforts);
        Assert.DoesNotContain("ultra", choices[1].Efforts);
    }

    [Fact]
    public void Codex_の隠されたモデルは出さない()
    {
        // CLI 自身が人間に見せないと決めたものを、こちらの画面で選ばせない。
        var choices = AgentModelCatalog.ParseCodex(
            """
            {"models":[
              {"slug":"codex-auto-review","display_name":"Codex Auto Review","visibility":"hide"},
              {"slug":"gpt-5.5","display_name":"GPT-5.5","visibility":"list"}
            ]}
            """);

        Assert.Equal("gpt-5.5", Assert.Single(choices).Id);
    }

    [Fact]
    public void Codex_の読めない出力は無いものとして扱う()
    {
        // 版が変われば形も変わる。**壊れるより、候補が出ない方がよい。**
        Assert.Empty(AgentModelCatalog.ParseCodex("not json"));
        Assert.Empty(AgentModelCatalog.ParseCodex("""{"other":1}"""));
    }

    [Fact]
    public void Antigravity_は_id_と表示名を読む()
    {
        var output = string.Join("\n",
        [
            "Fetching available models...",
            "gemini-3.8-flash-high\tGemini 3.8 Flash (High)",
            "claude-opus-4-6-thinking\tClaude Opus 4.6 (Thinking)",
        ]);

        var choices = AgentModelCatalog.ParseAntigravity(output);

        Assert.Equal(2, choices.Count);
        Assert.Equal("gemini-3.8-flash-high", choices[0].Id);
        Assert.Equal("Gemini 3.8 Flash (High)", choices[0].Label);

        // **強さは返さない。** あちらはモデル名に畳まれている（§47-2）。
        Assert.Empty(choices[0].Efforts);
    }

    [Fact]
    public void Antigravity_の形に合わない行は拾わない()
    {
        Assert.Empty(AgentModelCatalog.ParseAntigravity("Fetching available models...\nerror: something\n\n"));
    }

    [Fact]
    public void Claude_は警告から強さの候補を読む()
    {
        // 実機の出力（`claude --effort bogus --help`）。**会話を1つも使わない。**
        var output =
            "Warning: Unknown --effort value 'bogus' — ignoring it and using the default effort. "
            + "Valid values: low, medium, high, xhigh, max.\n";

        Assert.Equal(["low", "medium", "high", "xhigh", "max"], AgentModelCatalog.ParseValidValues(output));
    }

    [Fact]
    public void 候補が書かれていなければ空で返す()
    {
        Assert.Empty(AgentModelCatalog.ParseValidValues("何も書かれていない出力"));
        Assert.Empty(AgentModelCatalog.ParseValidValues(string.Empty));
    }
}
