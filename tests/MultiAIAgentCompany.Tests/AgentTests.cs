using MultiAIAgentCompany.Core.Agents;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §2 / §5 —— 3つの CLI は対称ではない。決定の語彙をハードコードしない。</summary>
public sealed class AgentTests
{
    [Fact]
    public void Antigravityには_ランタイム承認の往復が無い()
    {
        // 実測 §13-3。ここを true にすると、握りつぶされた実行を成功として扱う設計に戻る。
        Assert.False(AgentCapabilities.For(AgentKind.AntigravityCli).SupportsRuntimeApprovalRoundTrip);

        // **承認の往復が無いことと、駆動モードは別の話。**
        // 2026-09-06 に既定を Tui から Structured へ変えた（§5 / §13-3 追記2）——
        // 握りつぶしは denied_actions で検出でき、1プロセス多ターンも実測で回るため。
        Assert.Equal(DriveMode.Structured, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode);
    }

    [Fact]
    public void ClaudeとCodexは承認の往復が閉じる()
    {
        Assert.True(AgentCapabilities.For(AgentKind.ClaudeCode).SupportsRuntimeApprovalRoundTrip);
        Assert.True(AgentCapabilities.For(AgentKind.CodexCli).SupportsRuntimeApprovalRoundTrip);
    }

    [Fact]
    public void 提示されていない決定は返さない()
    {
        // 実測 §13-2: Codex 0.144.6 は decline を提示しなかった（仕様書には載っていた）。
        var request = new ApprovalRequest(
            RequestId: "r1",
            Kind: ApprovalKind.Runtime,
            Agent: AgentKind.CodexCli,
            SessionId: null,
            TurnId: null,
            Title: "RunCommand",
            Details: "touch /tmp/x",
            AvailableDecisions:
            [
                new ApprovalDecision("accept", "許可"),
                new ApprovalDecision("acceptWithExecpolicyAmendment", "許可（ポリシー修正つき）", "{\"acceptWithExecpolicyAmendment\":{}}"),
                new ApprovalDecision("cancel", "取り消し"),
            ],
            SuggestedRules: []);

        Assert.True(request.Offers("accept"));
        Assert.False(request.Offers("decline"));
    }
}

// **SubmitKeyTests は消した**（設計 §22-4、2026-09-06）。
// v1 は TUI 経路を持たないので、送信キーを解決する相手がいない。
// 実測（`~/.claude/keybindings.json` で送信が `1B 0D` だった件）は §13-8 に残してある。
