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
        Assert.Equal(DriveMode.Tui, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode);
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

/// <summary>設計 §13-8 訂正 —— 送信キーは環境の設定で変わる。不明なら送らない。</summary>
public sealed class SubmitKeyTests
{
    [Fact]
    public void 判定できないときは_0D_にフォールバックしない()
    {
        var key = SubmitKey.Unknown("keybindings.json が読めない");

        Assert.False(key.IsResolved);
        Assert.Null(key.Bytes);
    }

    [Fact]
    public void 空のバイト列は送信キーにならない()
    {
        // IsResolved が true なのに送るものが無い、という状態を作れないようにする（§14-4）。
        Assert.Throws<ArgumentException>(() => SubmitKey.Resolved([], "x"));
    }

    [Fact]
    public void 解決済みのバイト列は外から書き換えられない()
    {
        var key = SubmitKey.Resolved(SubmitKey.AltEnter, "~/.claude/keybindings.json: meta+enter");

        var taken = key.Bytes!;
        taken[0] = 0x41;

        Assert.Equal(new byte[] { 0x1B, 0x0D }, key.Bytes);
    }

    [Fact]
    public void meta_enter_はESCとCRになる()
    {
        var key = SubmitKey.Resolved(SubmitKey.AltEnter, "~/.claude/keybindings.json: meta+enter");

        Assert.True(key.IsResolved);
        Assert.Equal(new byte[] { 0x1B, 0x0D }, key.Bytes);
    }
}
