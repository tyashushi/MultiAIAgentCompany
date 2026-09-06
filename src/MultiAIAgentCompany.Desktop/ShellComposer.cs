using System.Collections.ObjectModel;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using MultiAIAgentCompany.Core.Workspace.Trust;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 部門定義から画面を組み立てる。<b>状態は検出器（Core）が決め、ここは繋ぐだけ</b>（設計 §4）。
/// </summary>
public sealed class ShellComposer
{
    /// <summary>根拠がこれより古くなったら活動状態は Unknown へ落ちる（設計 §7）。</summary>
    private static readonly TimeSpan EvidenceMaxAge = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, DepartmentStatusTracker> _trackers = new(StringComparer.Ordinal);

    public ShellComposer(IReadOnlyList<DepartmentDefinition> departments, TimeProvider clock)
    {
        var tiles = new List<DepartmentTile>();
        foreach (var department in departments)
        {
            var tracker = new DepartmentStatusTracker(
                new AgentRef(department.Id, department.Agent), clock, EvidenceMaxAge);
            _trackers[department.Id] = tracker;
            tiles.Add(new DepartmentTile(department.DisplayName, department.Agent, department.Mode, tracker));
        }

        Approvals = new ApprovalQueue();
        Shell = new ShellViewModel
        {
            Approvals = Approvals,
            WorkLog = ["まだ何も動かしていない"],
            SecretaryTranscript = ["秘書はまだ起動していない"],
            Departments = tiles,
        };
    }

    public ShellViewModel Shell { get; }

    /// <summary>(a) ランタイム承認の待ち行列。セッションの ApprovalRequested をここへ流す。</summary>
    public ApprovalQueue Approvals { get; }

    /// <summary>部門の検出器。セッションのイベントをここへ流し込む。</summary>
    public DepartmentStatusTracker TrackerOf(string departmentId) => _trackers[departmentId];

    public IReadOnlyCollection<string> DepartmentIds => _trackers.Keys;

    /// <summary>
    /// フォルダが選ばれたときに、各 CLI の trust を読み直す（設計 §13-9）。
    /// <b>書き込みはしない。</b>
    /// </summary>
    public async Task SelectWorkspaceAsync(string root, CancellationToken ct)
    {
        var workspace = new WorkspaceRef(root);
        var rows = await WorkspaceTrustReport.BuildAsync(workspace,
            [new ClaudeCodeTrustProbe(), new CodexCliTrustProbe(), new AntigravityTrustProbe()],
            ct);

        Shell.WorkspaceLabel = root;
        Shell.Trust.Clear();
        foreach (var row in rows)
        {
            Shell.Trust.Add(new TrustRow(row.Agent, row.State));
        }
    }

    public static ShellComposer CreateDefault(TimeProvider clock) =>
        new(Core.Workspace.DepartmentStore.CreateDefaultDepartments(), clock);
}
