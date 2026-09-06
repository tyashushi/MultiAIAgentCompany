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

    private Action<DepartmentStatusTracker, Evidence>[][] Steps =>
    [
        // 1: 全部が動き出す
        [Working, Working, Working, Working, Working],

        // 2: 人間の出番が3種そろう —— (a) 承認まち / (b) 相談中 / 報告まち。
        //    承認は Codex と Claude の両方を出す。**提示される決定が違う**のが見えるように（§5）。
        [Working, Approval, Consultation, ClaudeApproval, Working],

        // 3: 1つが倒れ、1つは休む。休んでいる・分からない・倒れたが別物であること（§15-2）
        [Resting, Skip, Consultation, Skip, Down],
    ];

    private static void Skip(DepartmentStatusTracker t, Evidence e)
    {
        // 前の段の状態をそのまま残す（承認まちは人間が答えるまで消えない。§7）。
    }

    private static void Working(DepartmentStatusTracker t, Evidence e) => t.OnObserved(e);

    private static void Resting(DepartmentStatusTracker t, Evidence e) =>
        t.OnTurnFinished(new OutcomeVerdict(true, "デモ: turn が終わった"));

    private void Approval(DepartmentStatusTracker t, Evidence e)
    {
        // **決定は CLI が提示したものだけ**（設計 §5）。Codex は accept /
        // acceptWithExecpolicyAmendment / cancel の3つで、decline は提示しない（実測 §13-2）。
        var request = new ApprovalRequest(
            "demo-a", ApprovalKind.Runtime, AgentKind.CodexCli, "demo-thread", "demo-turn",
            "RunCommand",
            "Do you want to allow creating this file outside the writable workspace?",
            [
                new ApprovalDecision("accept", "許可"),
                new ApprovalDecision("acceptWithExecpolicyAmendment", "許可（ポリシー修正つき）"),
                new ApprovalDecision("cancel", "取り消し"),
            ],
            []);

        t.OnApprovalRequested(request);
        composer.Approvals.Add(new PendingApproval("実装", request, t,
            (decision, reason, ct) => Task.CompletedTask));
    }

    private void ClaudeApproval(DepartmentStatusTracker t, Evidence e)
    {
        // Claude は allow / deny の2つ。permission_suggestions から
        // 「今回だけ / 常に許可」の材料が付く（実測 §13-1）。**安全な要約だけ**（§10）。
        var request = new ApprovalRequest(
            "demo-claude", ApprovalKind.Runtime, AgentKind.ClaudeCode, "demo-session", "demo-turn",
            "Bash", "echo HELLO > hello.txt",
            [new ApprovalDecision("allow", "許可"), new ApprovalDecision("deny", "拒否")],
            ["Bash のルール1件を常に許可（localSettings）", "ディレクトリ1件を作業対象に追加（session）"]);

        t.OnApprovalRequested(request);
        composer.Approvals.Add(new PendingApproval("レビュー", request, t,
            (decision, reason, ct) => Task.CompletedTask));
    }

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
