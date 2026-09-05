namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// AI たちが働く対象のフォルダ。アプリ起動後に人間が選ぶ（設計 §0）。
/// </summary>
/// <param name="Root">フォルダの絶対パス。</param>
/// <param name="IsolateWithWorktree">
/// worktree 隔離を使うか。設計 §8 —— <b>既定は false（1フォルダ共有・書き込み権1つ）</b>。
/// Unity MCP は起動中の Editor が開いているプロジェクト（＝ main のチェックアウト）を見るので、
/// worktree に切るとファイルと MCP の操作対象が一致しない。
/// Unity を含まないワークスペースでのみ true を選べる。v1 では実装しない（設定項目だけ）。
/// </param>
public sealed record WorkspaceRef(string Root, bool IsolateWithWorktree = false)
{
    public Coordination.CompanyPaths Company => new(Root);
}

/// <summary>
/// ワークスペースの trust 検査。設計 §7 の規則
/// 「初回 trust・ログイン・モデル選択・更新通知・quota エラーを承認待ちと誤認しない」に対応する。
/// </summary>
/// <remarks>
/// 実測: Antigravity / Gemini CLI は未 trust ディレクトリで headless 実行を拒否する。
/// これは <see cref="Status.ActivityState.AwaitingApproval"/> ではない。起動前に判る事実として扱う。
/// </remarks>
public interface IWorkspaceTrustProbe
{
    Agents.AgentKind Kind { get; }

    /// <summary>そのワークスペースが、その CLI にとって trust 済みか。判らなければ null。</summary>
    Task<bool?> IsTrustedAsync(WorkspaceRef workspace, CancellationToken ct);
}
