using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 設計 §13 末尾「成功判定は必ず3層すべてを見る」と、§14-4 の直しを固定する。
/// 実測で踏んだ罠（Antigravity の status:"SUCCESS"、Unity MCP の isError:false ＋ success:false）が
/// そのまま test になっている。
/// </summary>
public sealed class OutcomeJudgmentTests
{
    private static readonly OutcomeRequirement Unity = OutcomeRequirement.UnityMcp;

    [Fact]
    public void 必須の層が全部_Ok_なら成功()
    {
        var verdict = new OutcomeSignals(
            LayerObservation.Ok, LayerObservation.Ok, LayerObservation.Ok).Judge(Unity);

        Assert.True(verdict.Succeeded);
    }

    [Fact]
    public void 層を1つだけ確認しても成功にしない()
    {
        // 再レビューが挙げた反例（§14-4）。初版はこれが true を返していた。
        Assert.False(new OutcomeSignals(Tool: LayerObservation.Ok).Judge(Unity).Succeeded);
        Assert.False(new OutcomeSignals(Payload: LayerObservation.Ok).Judge(Unity).Succeeded);
    }

    [Fact]
    public void 必須の層に適用外は使えない()
    {
        var verdict = new OutcomeSignals(
            LayerObservation.Ok, LayerObservation.NotApplicable, LayerObservation.Ok).Judge(Unity);

        Assert.False(verdict.Succeeded);
    }

    [Fact]
    public void 中身がsuccess_falseなら_isErrorがOkでも失敗()
    {
        // 実測: Unity MCP の find_gameobjects は正しい形の呼び出しでも
        // {"success": false, ...} を返し、isError は false だった。
        var verdict = new OutcomeSignals(
            LayerObservation.Ok, LayerObservation.Ok, LayerObservation.Failed).Judge(Unity);

        Assert.False(verdict.Succeeded);
    }

    [Fact]
    public void denied_actionsがあれば全層_Ok_でも成功と言わない()
    {
        // 実測: Antigravity は exit 0 / status:"SUCCESS" を返しつつ、仕事はしていなかった。
        var verdict = new OutcomeSignals(
            LayerObservation.Ok, LayerObservation.Ok, LayerObservation.Ok,
            DeniedActions: ["RunCommand"]).Judge(OutcomeRequirement.For(AgentKind.AntigravityCli));

        Assert.False(verdict.Succeeded);
        Assert.Contains("RunCommand", verdict.Reason);
    }

    [Fact]
    public void 何も観測していないなら成功にしない()
    {
        Assert.False(new OutcomeSignals().Judge(Unity).Succeeded);
    }

    [Fact]
    public void プロトコルの失敗は他が_Ok_でも失敗()
    {
        var verdict = new OutcomeSignals(
            LayerObservation.Failed, LayerObservation.Ok, LayerObservation.Ok).Judge(Unity);

        Assert.False(verdict.Succeeded);
    }

    [Fact]
    public void 必須の層が1つも無い要求は作れない()
    {
        // これを許すと「何も見ずに成功」が返る要求を実装者が書けてしまう。
        Assert.Throws<ArgumentException>(() => new OutcomeRequirement(false, false, false));
    }
}
