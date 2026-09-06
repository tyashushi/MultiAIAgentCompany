using System.Collections.ObjectModel;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using MultiAIAgentCompany.Core.Workspace.Trust;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Agents;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 部門定義から画面を組み立てる。<b>状態は検出器（Core）が決め、ここは繋ぐだけ</b>（設計 §4）。
/// </summary>
public sealed class ShellComposer
{
    /// <summary>根拠がこれより古くなったら活動状態は Unknown へ落ちる（設計 §7）。</summary>
    private static readonly TimeSpan EvidenceMaxAge = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, DepartmentStatusTracker> _trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DepartmentDefinition> _definitions = new(StringComparer.Ordinal);

    private readonly TimeProvider _clock;

    public ShellComposer(IReadOnlyList<DepartmentDefinition> departments, TimeProvider clock)
    {
        _clock = clock;
        var tiles = new List<DepartmentTile>();
        foreach (var department in departments)
        {
            var tracker = new DepartmentStatusTracker(
                new AgentRef(department.Id, department.Agent), clock, EvidenceMaxAge);
            _trackers[department.Id] = tracker;
            _definitions[department.Id] = department;
            tiles.Add(new DepartmentTile(department.Id, department.DisplayName, department.Agent, department.Mode, tracker));
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

    public DepartmentDefinition DefinitionOf(string departmentId) => _definitions[departmentId];

    /// <summary>選ばれたワークスペース。まだ選ばれていなければ null。</summary>
    public WorkspaceRef? Workspace { get; private set; }

    public TaskStore? Tasks { get; private set; }

    public LeaseStore? Leases { get; private set; }

    public TaskDispatcher? Dispatcher { get; private set; }

    public CompanyScanner? Scanner { get; private set; }

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

        Workspace = workspace;
        var paths = workspace.Company;
        Tasks = new TaskStore(paths, _clock);
        Leases = new LeaseStore(paths, _clock);
        Dispatcher = new TaskDispatcher(paths, Tasks, Leases, _clock);
        Scanner = new CompanyScanner(paths, Tasks);
        Shell.WorkspaceLabel = root;
        Shell.Trust.Clear();
        foreach (var row in rows)
        {
            Shell.Trust.Add(new TrustRow(row.Agent, row.State));
        }
    }

    /// <summary>
    /// `.company/` を1周見て、仕事状態をタイルへ反映する（設計 §16-1）。
    /// </summary>
    /// <remarks>
    /// <b>走査が正本。</b> セッションのイベントから仕事状態を作らない（§7）。
    /// </remarks>
    public async Task<CompanyScanResult?> ScanAsync(CompanyScanKind kind, CancellationToken ct)
    {
        if (Scanner is null || Tasks is null)
        {
            return null;
        }

        var result = await Scanner.SyncAsync(kind, ct);
        await PushWorkStatesAsync(ct);

        if (kind is CompanyScanKind.Startup)
        {
            Shell.Recovery.Clear();

            // §16-3: 部門が分かるものはタイルにも出す。左ペインだけだと右を見ている人が拾えない。
            var acrossRestart = result.Dispatched.Select(task => task.DepartmentId).ToHashSet(StringComparer.Ordinal);
            foreach (var tile in Shell.Departments)
            {
                tile.DispatchedAcrossRestart = acrossRestart.Contains(tile.Id);
            }

            foreach (var task in result.Dispatched)
            {
                Shell.Recovery.Add(new RecoveryItem(RecoveryKind.MaybeSent, task.Slug,
                    $"{task.DepartmentId} へ送ったかもしれない。届いたか確かめる（自動で再送しない）")
                {
                    DepartmentId = task.DepartmentId,
                });
            }

            foreach (var broken in result.Unreadable)
            {
                // 部門に紐づけない —— state.json が読めないなら departmentId も信用できない。
                Shell.Recovery.Add(new RecoveryItem(RecoveryKind.Unreadable, broken.Slug, broken.Reason));
            }
        }

        return result;
    }

    /// <summary>
    /// 各部門の「いま人間の出番に近い仕事」をタイルへ入れる。
    /// </summary>
    /// <remarks>
    /// 1部門が複数の仕事を持ち得るので、<b>いちばん急ぐものを選ぶ</b>
    /// （§15-6 のボタンが1つであるのと同じ理由）。
    /// </remarks>
    private async Task PushWorkStatesAsync(CancellationToken ct)
    {
        if (Tasks is null)
        {
            return;
        }

        var chosen = new Dictionary<string, TaskState>(StringComparer.Ordinal);
        foreach (var slug in await Tasks.ListSlugsAsync(ct))
        {
            if (await Tasks.ReadAsync(slug, ct) is not TaskReadResult.Found found)
            {
                continue;
            }

            var state = found.State;
            if (!chosen.TryGetValue(state.DepartmentId, out var current) || Urgency(state.Status) > Urgency(current.Status))
            {
                chosen[state.DepartmentId] = state;
            }
        }

        // 走査に出てこなかった部門の仕事状態を残さない。
        // 残すと、別のワークスペースに切り替えたあとも古い報告まちを指し続ける。
        foreach (var (departmentId, tracker) in _trackers)
        {
            if (chosen.ContainsKey(departmentId))
            {
                continue;
            }

            tracker.OnWorkStateCleared();
            foreach (var tile in Shell.Departments.Where(t => t.Id == departmentId))
            {
                tile.CurrentTaskSlug = null;
            }
        }

        foreach (var (departmentId, state) in chosen)
        {
            if (!_trackers.TryGetValue(departmentId, out var tracker))
            {
                continue;
            }

            // 仕事状態は Document 根拠でしか動かない（§7 の信頼順）。
            tracker.OnWorkStateChanged(state.Status, new Evidence(
                EvidenceSource.Document, _clock.GetUtcNow(), null, null,
                new AgentRef(departmentId, DefinitionOf(departmentId).Agent), null, null,
                $"{state.Slug}: {state.Status}（試行 {state.AttemptId}）"));

            foreach (var tile in Shell.Departments.Where(t => t.Id == departmentId))
            {
                tile.CurrentTaskSlug = state.Slug;
            }
        }
    }

    private static int Urgency(CoreTaskStatus status) => status switch
    {
        CoreTaskStatus.AwaitingAnswer => 4,
        CoreTaskStatus.Reported => 3,
        CoreTaskStatus.Dispatched => 2,
        CoreTaskStatus.InProgress => 1,
        _ => 0,
    };

    public static ShellComposer CreateDefault(TimeProvider clock) =>
        new(Core.Workspace.DepartmentStore.CreateDefaultDepartments(), clock);
}
