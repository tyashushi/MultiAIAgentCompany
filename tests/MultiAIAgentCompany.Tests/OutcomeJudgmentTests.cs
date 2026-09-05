using MultiAIAgentCompany.Core.Status;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 設計 §13 末尾「成功判定は必ず3層すべてを見る」を固定する。
/// 実測で踏んだ罠（Antigravity の status:"SUCCESS"、Unity MCP の isError:false ＋ success:false）が
/// そのまま test になっている。
/// </summary>
public sealed class OutcomeJudgmentTests
{
    [Fact]
    public void 三層が揃って成功なら成功()
    {
        var verdict = new OutcomeSignals(ToolReportedError: false, PayloadSuccess: true).Judge();

        Assert.True(verdict.Succeeded);
    }

    [Fact]
    public void 中身がsuccess_falseなら_isErrorがfalseでも失敗()
    {
        // 実測: Unity MCP の find_gameobjects は正しい形の呼び出しでも
        // {"success": false, "message": "Missing required parameter..."} を返し、isError は false だった。
        var verdict = new OutcomeSignals(ToolReportedError: false, PayloadSuccess: false).Judge();

        Assert.False(verdict.Succeeded);
    }

    [Fact]
    public void denied_actionsがあれば成功と言わない()
    {
        // 実測: Antigravity は exit 0 / status:"SUCCESS" を返しつつ、仕事はしていなかった。
        var verdict = new OutcomeSignals(
            ToolReportedError: false,
            PayloadSuccess: true,
            DeniedActions: ["RunCommand"]).Judge();

        Assert.False(verdict.Succeeded);
        Assert.Contains("RunCommand", verdict.Reason);
    }

    [Fact]
    public void どの層も成功を確認していないなら成功にしない()
    {
        // status:"SUCCESS" だけを見て成功にしないための番人。
        var verdict = new OutcomeSignals().Judge();

        Assert.False(verdict.Succeeded);
    }

    [Fact]
    public void プロトコルの失敗は他を見るまでもなく失敗()
    {
        var verdict = new OutcomeSignals(
            ProtocolError: "worker quit with fatal",
            ToolReportedError: false,
            PayloadSuccess: true).Judge();

        Assert.False(verdict.Succeeded);
    }
}
