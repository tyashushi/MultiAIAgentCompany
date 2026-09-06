using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// <b>デモ用。人間が §15 の3層を目で確かめるためだけにある。</b>
/// </summary>
/// <remarks>
/// 実セッションが繋がったら**この型ごと消す**。
/// 自動では動かさない —— 勝手に状態が変わると「本当に動いている」と誤解させるので、
/// 人間がボタンを押したときだけ1段進む。
/// </remarks>
public sealed class DemoDriver(ShellComposer composer, TimeProvider clock)
{
    private int _step;

    public string NextLabel => $"デモ: 状態を進める（{_step % Steps.Length + 1}/{Steps.Length}）";

    public void Step()
    {
        var scenario = Steps[_step % Steps.Length];
        _step++;

        foreach (var (departmentId, apply) in composer.DepartmentIds.Zip(scenario))
        {
            apply(composer.TrackerOf(departmentId), Evidence(departmentId));
        }
    }

    private Evidence Evidence(string departmentId) => new(
        EvidenceSource.StructuredEvent, clock.GetUtcNow(), "demo-session", "demo-turn",
        new AgentRef(departmentId, AgentKind.ClaudeCode), null, "demo", "デモの観測");

    private static readonly Action<DepartmentStatusTracker, Evidence>[][] Steps =
    [
        // 1: 全部が動き出す
        [Working, Working, Working, Working, Working],

        // 2: 人間の出番が3種そろう —— (a) 承認まち / (b) 相談中 / 報告まち
        [Working, Approval, Consultation, Reported, Working],

        // 3: 1つが倒れ、1つは休む。休んでいる・分からない・倒れたが別物であること（§15-2）
        [Resting, Approval, Consultation, Reported, Down],
    ];

    private static void Working(DepartmentStatusTracker t, Evidence e) => t.OnObserved(e);

    private static void Resting(DepartmentStatusTracker t, Evidence e) =>
        t.OnTurnFinished(new OutcomeVerdict(true, "デモ: turn が終わった"));

    private static void Approval(DepartmentStatusTracker t, Evidence e) =>
        t.OnApprovalRequested(new ApprovalRequest(
            "demo-a", ApprovalKind.Runtime, AgentKind.CodexCli, "s", "t",
            "RunCommand", "デモの承認要求", [new ApprovalDecision("accept", "許可")], []));

    private static void Consultation(DepartmentStatusTracker t, Evidence e)
    {
        t.OnApprovalRequested(new ApprovalRequest(
            "demo-b", ApprovalKind.Consultation, AgentKind.AntigravityCli, "s", "t",
            "設計の確認", "デモの相談", [], []));
        t.OnWorkStateChanged(CoreTaskStatus.AwaitingAnswer, DocumentEvidence(t));
    }

    private static void Reported(DepartmentStatusTracker t, Evidence e)
    {
        t.OnObserved(e);
        t.OnWorkStateChanged(CoreTaskStatus.Reported, DocumentEvidence(t));
    }

    private static void Down(DepartmentStatusTracker t, Evidence e) => t.OnExited(1);

    private static Evidence DocumentEvidence(DepartmentStatusTracker t) => new(
        EvidenceSource.Document, DateTimeOffset.UtcNow, null, null,
        new AgentRef("demo", AgentKind.ClaudeCode), null, "demo", "デモ: .company/ の状態");
}
