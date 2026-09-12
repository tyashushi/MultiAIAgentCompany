using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class SecretaryApprovalPolicyTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();

    private ApprovalRequest Request(string tool, string? path) => new(
        "req-1", ApprovalKind.Runtime, AgentKind.ClaudeCode, null, null,
        Title: tool, Details: string.Empty,
        AvailableDecisions: [new ApprovalDecision("allow", "許可"), new ApprovalDecision("deny", "拒否")],
        SuggestedRules: [],
        ToolName: tool,
        TargetPath: path);

    private ApprovalVerdict Decide(string tool, string? path) =>
        SecretaryApprovalPolicy.Decide(Request(tool, path), _workspace.Paths);

    [Fact]
    public void 作業フォルダの中を読むのは通す()
    {
        Assert.True(Decide("Read", Path.Combine(_workspace.Paths.WorkspaceRoot, "docs", "x.md")).AutoApprove);
        Assert.True(Decide("Grep", _workspace.Paths.WorkspaceRoot).AutoApprove);
    }

    [Fact]
    public void 作業フォルダの外を読むのは聞く()
    {
        Assert.False(Decide("Read", "/etc/passwd").AutoApprove);
        Assert.False(Decide("Read", Path.Combine(Path.GetTempPath(), "よそ.md")).AutoApprove);
    }

    [Fact]
    public void 秘書の持ち場に書くのは通す()
    {
        Assert.True(Decide("Write", Path.Combine(_workspace.Paths.SecretaryOutbox, "proposal.md")).AutoApprove);
        Assert.True(Decide("Edit", _workspace.Paths.SecretaryReadme).AutoApprove);
    }

    [Fact]
    public void 調整基盤の中に書くのは通す()
    {
        // **人間が §17-6 の「秘書は tasks/ を触らない」を改めた**（2026-09-12、§38）。
        Assert.True(Decide("Write", _workspace.Paths.Instruction("feature")).AutoApprove);
    }

    [Fact]
    public void 状態と権利のファイルは_秘書には書かせない()
    {
        // **約束ではなく不変条件。** §14-1（state.json を書くのはアプリだけ）と
        // §14-2（権利の正本）が、revision の楽観ロックごとここに乗っている。
        Assert.False(Decide("Write", _workspace.Paths.State("feature")).AutoApprove);
        Assert.False(Decide("Edit", _workspace.Paths.Lease).AutoApprove);

        // **大文字小文字を区別しないファイルシステムでは、同じファイルを指す**（レビューで発覚）。
        Assert.False(Decide("Write", Path.Combine(
            Path.GetDirectoryName(_workspace.Paths.State("feature"))!, "STATE.JSON")).AutoApprove);
        Assert.False(Decide("Edit", Path.Combine(
            _workspace.Paths.Root, "Lease.Json")).AutoApprove);
    }

    [Fact]
    public void 調整基盤の外に書くのは聞く()
    {
        Assert.False(Decide("Write", Path.Combine(_workspace.Paths.WorkspaceRoot, "src", "a.cs")).AutoApprove);
        Assert.False(Decide("Write", "/tmp/よそ.md").AutoApprove);
    }

    [Fact]
    public void 分からないものは聞く()
    {
        // ツール名を返してこない CLI や、名前が変わった版（§7）。
        Assert.False(Decide(tool: "", path: _workspace.Paths.SecretaryOutbox).AutoApprove);
        Assert.False(Decide("Write", path: null).AutoApprove);
        Assert.False(Decide("知らないツール", _workspace.Paths.SecretaryOutbox).AutoApprove);
    }

    [Fact]
    public void 上へ辿って外に出られない()
    {
        // **綴りではなく実体で比べる**（§26-1 と同じ姿勢）。
        var escape = Path.Combine(_workspace.Paths.SecretaryOutbox, "..", "..", "..", "よそ.md");

        Assert.False(Decide("Write", escape).AutoApprove);
    }

    [Fact]
    public void 似た名前の隣のフォルダに当たらない()
    {
        // 区切りまで含めて比べないと `/ws` が `/ws-other` に当たる。
        Assert.False(Decide("Read", _workspace.Paths.WorkspaceRoot + "-other/x.md").AutoApprove);
    }

    [Fact]
    public void 通したときも理由を返す()
    {
        // **黙って通さない**（§7）。画面に出すために理由が要る。
        var verdict = Decide("Write", Path.Combine(_workspace.Paths.SecretaryOutbox, "p.md"));

        Assert.True(verdict.AutoApprove);
        Assert.Contains("Write", verdict.Reason);
    }

    public void Dispose() => _workspace.Dispose();
}
