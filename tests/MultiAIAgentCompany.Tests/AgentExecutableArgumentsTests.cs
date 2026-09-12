using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 外部ターミナルへ渡す引数（設計 §46）。
/// </summary>
/// <remarks>
/// <b>旗の形は CLI ごとに違う。</b> 実機で確かめた事実をここで固定する ——
/// Claude と Antigravity は <c>--effort</c>、**Codex には旗が無く**
/// <c>-c model_reasoning_effort=…</c> で渡す。
/// <para>
/// <b>指定が無いものは渡さない。</b> 空文字を渡すと、CLI 側の設定を
/// **空で上書きする**ことになりかねない（§7: 分からないものを埋めない）。
/// </para>
/// </remarks>
public sealed class AgentExecutableArgumentsTests
{
    [Fact]
    public void 指定が無ければプロンプトだけ渡す()
    {
        Assert.Equal(["読んで"], AgentExecutable.InteractiveArguments(AgentKind.ClaudeCode, "読んで"));
        Assert.Equal(["読んで"], AgentExecutable.InteractiveArguments(AgentKind.CodexCli, "読んで"));
        Assert.Equal(["-i", "読んで"], AgentExecutable.InteractiveArguments(AgentKind.AntigravityCli, "読んで"));
    }

    [Fact]
    public void 空白だけの指定は渡さない()
    {
        Assert.Equal(
            ["読んで"],
            AgentExecutable.InteractiveArguments(AgentKind.ClaudeCode, "読んで", "  ", "\t"));
    }

    [Fact]
    public void Claude_はモデルと強さを旗で渡す()
    {
        Assert.Equal(
            ["--model", "claude-opus-5", "--effort", "high", "読んで"],
            AgentExecutable.InteractiveArguments(AgentKind.ClaudeCode, "読んで", "claude-opus-5", "high"));
    }

    [Fact]
    public void Codex_は強さを設定の上書きで渡す()
    {
        // **`--effort` は無い。** 旗のつもりで渡すと、CLI が引数を解釈できない。
        Assert.Equal(
            ["-m", "gpt-5.6-terra", "-c", "model_reasoning_effort=\"high\"", "読んで"],
            AgentExecutable.InteractiveArguments(AgentKind.CodexCli, "読んで", "gpt-5.6-terra", "high"));
    }

    [Fact]
    public void Antigravity_はプロンプトの旗が違う()
    {
        // `-i`（--prompt-interactive）はプロンプトの直前に置く。
        Assert.Equal(
            ["--model", "gemini-3-pro", "--effort", "medium", "-i", "読んで"],
            AgentExecutable.InteractiveArguments(AgentKind.AntigravityCli, "読んで", "gemini-3-pro", "medium"));
    }
}
