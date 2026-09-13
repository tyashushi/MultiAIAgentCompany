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
    [Theory]
    [InlineData(AgentKind.ClaudeCode, AgentPermissionMode.Auto, "--permission-mode", "auto")]
    [InlineData(AgentKind.ClaudeCode, AgentPermissionMode.Manual, "--permission-mode", "manual")]
    [InlineData(AgentKind.ClaudeCode, AgentPermissionMode.AcceptEdits, "--permission-mode", "acceptEdits")]
    [InlineData(AgentKind.ClaudeCode, AgentPermissionMode.Plan, "--permission-mode", "plan")]
    [InlineData(AgentKind.CodexCli, AgentPermissionMode.Auto, "--approve-for-me", null)]
    [InlineData(AgentKind.AntigravityCli, AgentPermissionMode.AcceptEdits, "--mode", "accept-edits")]
    [InlineData(AgentKind.AntigravityCli, AgentPermissionMode.Plan, "--mode", "plan")]
    public void 権限モードはプロンプトの前に渡す(
        AgentKind kind, AgentPermissionMode mode, string flag, string? value)
    {
        // **モデルと強さを併用しても、プロンプトより前**（設計 §51）。agy は -i より前。
        var expected = AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high").ToList();
        var promptIndex = expected.Count - (kind is AgentKind.AntigravityCli ? 2 : 1);
        expected.Insert(promptIndex, flag);
        if (value is not null)
        {
            expected.Insert(promptIndex + 1, value);
        }

        Assert.Equal(expected, AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high", mode));
    }

    [Theory]
    [InlineData(AgentKind.CodexCli, AgentPermissionMode.Manual)]
    [InlineData(AgentKind.CodexCli, AgentPermissionMode.AcceptEdits)]
    [InlineData(AgentKind.CodexCli, AgentPermissionMode.Plan)]
    [InlineData(AgentKind.AntigravityCli, AgentPermissionMode.Auto)]
    [InlineData(AgentKind.AntigravityCli, AgentPermissionMode.Manual)]
    public void CLIが持たない権限モードは渡さない(AgentKind kind, AgentPermissionMode mode)
    {
        Assert.Equal(
            AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high"),
            AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high", mode));
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.CodexCli)]
    [InlineData(AgentKind.AntigravityCli)]
    public void 権限モードがnullなら既存の引数を変えない(AgentKind kind)
    {
        Assert.Equal(
            AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high"),
            AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high", permissionMode: null));
    }

    [Fact]
    public void CLIごとの権限モードを表示順に返す()
    {
        Assert.Equal(
            [AgentPermissionMode.Auto, AgentPermissionMode.Manual, AgentPermissionMode.AcceptEdits, AgentPermissionMode.Plan],
            AgentPermissionModes.For(AgentKind.ClaudeCode));
        Assert.Equal([AgentPermissionMode.Auto], AgentPermissionModes.For(AgentKind.CodexCli));
        Assert.Equal([AgentPermissionMode.AcceptEdits, AgentPermissionMode.Plan], AgentPermissionModes.For(AgentKind.AntigravityCli));
    }

    [Theory]
    [InlineData(AgentPermissionMode.Auto, "自動")]
    [InlineData(AgentPermissionMode.Manual, "手動")]
    [InlineData(AgentPermissionMode.AcceptEdits, "編集を受け入れる")]
    [InlineData(AgentPermissionMode.Plan, "プラン")]
    public void 権限モードの表示名を返す(AgentPermissionMode mode, string label)
    {
        Assert.Equal(label, AgentPermissionModes.Label(mode));
    }

}
