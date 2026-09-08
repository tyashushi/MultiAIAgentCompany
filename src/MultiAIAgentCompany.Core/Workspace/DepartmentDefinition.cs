using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;

namespace MultiAIAgentCompany.Core.Workspace;

/// <param name="Id">部門の識別子。<see cref="CompanyPaths.IsValidSlug"/> を通ること。</param>
/// <param name="DisplayName">画面に出す名前。</param>
/// <param name="Responsibility">担当業務の1行。</param>
/// <param name="Agent">担当する CLI。</param>
/// <param name="Mode">駆動モード。</param>
/// <param name="Model">CLI に渡すモデル。null なら CLI の設定に任せる。</param>
/// <param name="ReadsOnly">
/// 作業ツリーを<b>書き換えない</b>部門か（設計 §29-1）。
/// </param>
/// <remarks>
/// <b><see cref="ReadsOnly"/> は書き込み権を取るかどうかを決める</b>（§14-2）。
/// 読むだけの部門（設計レビューなど）が Write lease を取ると、**同時に1つしか動けない** ——
/// 2人のレビュアーに同じ文書を読ませるだけで直列化される。
/// 成果物は <c>report.md</c> で、調整文書への書き込みは lease の対象外（§14-2）。
/// <para>
/// <b>これは「書かない」という宣言であって、強制ではない。</b> CLI は実際には書ける ——
/// 守らせるのは指示書（§16-1 の publish 契約と同じ姿勢）。
/// だから<b>安全側の既定は false</b>（＝ lease を取る）。
/// </para>
/// </remarks>
/// <param name="AutoApproveAllTools">
/// この部門の CLI に<b>ツール権限を全部自動承認させる</b>か（設計 §30-4）。<b>既定は false。</b>
/// </param>
public sealed record DepartmentDefinition(
    string Id, string DisplayName, string Responsibility, AgentKind Agent, DriveMode Mode,
    string? Model = null, bool ReadsOnly = false, bool AutoApproveAllTools = false)
{
    /// <summary>
    /// 危険モードを<b>この部門で意味のあるものとして扱ってよいか</b>（設計 §30-4）。
    /// </summary>
    /// <remarks>
    /// <b>承認の往復を持つ CLI には出さない。</b> Claude Code には <c>can_use_tool</c> が
    /// あるので、そちらで人間に聞く（§3）。この抜け道が要るのは
    /// <b>人間に聞く手段が無い CLI だけ</b> —— 聞けるのに聞かない、を作らない。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DangerousModeApplies => !AgentCapabilities.For(Agent).SupportsRuntimeApprovalRoundTrip;

    /// <summary>実際に全自動承認で起動するか。<b>宣言と適用を分ける</b>（設計 §30-4）。</summary>
    /// <remarks>
    /// <b>ファイルへ書かない</b>（実機で発覚、2026-09-08）。計算値なので読み戻されない ——
    /// 人間が <c>departments.json</c> でこちらを true にすると、
    /// <b>設定したのに黙って無視される</b>。人間が触る鍵は <see cref="AutoApproveAllTools"/> だけ。
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RunsWithAllToolsApproved => AutoApproveAllTools && DangerousModeApplies;
}

/// <remarks>
/// <b>record の等値比較は <see cref="Departments"/> を要素で見ない</b>
/// （C# の record はコレクションを参照で比べるので、配列と List は別物になる）。
/// 中身を比べたいときは <c>Departments</c> を明示的に突き合わせること。
/// </remarks>
public sealed record CompanyDefinition(long Revision, IReadOnlyList<DepartmentDefinition> Departments);

/// <summary><c>.company/departments.json</c> の読み書き結果。</summary>
public abstract record DefinitionReadResult
{
    public sealed record Found(CompanyDefinition Definition) : DefinitionReadResult;
    public sealed record Missing : DefinitionReadResult;
    public sealed record Unreadable(string Reason) : DefinitionReadResult;
}

public abstract record DefinitionWriteResult
{
    public sealed record Written(CompanyDefinition Definition) : DefinitionWriteResult;
    public sealed record Rejected(string Reason) : DefinitionWriteResult;
}

/// <summary>ワークスペースに残る部門定義の唯一の保存口。</summary>
public sealed class DepartmentStore
{
    private const string FileName = "departments.json";
    private readonly CompanyPaths _paths;

    public DepartmentStore(CompanyPaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<DefinitionReadResult> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = _paths.Departments;
        if (!File.Exists(path)) return new DefinitionReadResult.Missing();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            var definition = await JsonSerializer.DeserializeAsync<CompanyDefinition>(stream, TaskStateJson.Options, ct);
            if (definition is null) return new DefinitionReadResult.Unreadable("departments.json が空です");
            var validation = Validate(definition.Departments);
            return validation is null
                ? new DefinitionReadResult.Found(definition)
                : new DefinitionReadResult.Unreadable(validation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new DefinitionReadResult.Unreadable($"departments.json を読めません: {exception.Message}");
        }
    }

    public async Task<DefinitionWriteResult> SaveAsync(
        CompanyDefinition expected, IReadOnlyList<DepartmentDefinition> departments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(departments);
        ct.ThrowIfCancellationRequested();

        var validation = Validate(departments);
        if (validation is not null) return new DefinitionWriteResult.Rejected(validation);

        var read = await ReadAsync(ct);
        if (read is DefinitionReadResult.Unreadable broken)
            return new DefinitionWriteResult.Rejected($"departments.json を検証できません: {broken.Reason}");
        var currentRevision = read switch
        {
            DefinitionReadResult.Missing => 0,
            DefinitionReadResult.Found found => found.Definition.Revision,
            _ => throw new InvalidOperationException("未知の読み取り結果です"),
        };
        if (currentRevision != expected.Revision)
            return new DefinitionWriteResult.Rejected($"Revision が一致しません（expected: {expected.Revision}, actual: {currentRevision}）");

        var next = new CompanyDefinition(checked(currentRevision + 1), departments.ToArray());
        await WriteAtomicallyAsync(_paths.Departments, next, ct);
        return new DefinitionWriteResult.Written(next);
    }

    /// <summary>v1 の既定5部門。各 CLI の既定モードは能力定義から取る。</summary>
    public static IReadOnlyList<DepartmentDefinition> CreateDefaultDepartments() =>
    [
        new("design", "設計", "要件と設計判断を整理する。", AgentKind.ClaudeCode, AgentCapabilities.For(AgentKind.ClaudeCode).DefaultDriveMode),
        new("implementation", "実装", "承認された設計を実装する。", AgentKind.CodexCli, AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode),
        new("research", "調査", "技術的な選択肢と根拠を調査する。", AgentKind.AntigravityCli, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode),
        new("review", "レビュー", "変更をレビューし、懸念を報告する。", AgentKind.ClaudeCode, AgentCapabilities.For(AgentKind.ClaudeCode).DefaultDriveMode),
        new("testing", "テスト", "テストを実行し、結果を報告する。", AgentKind.CodexCli, AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode),

        // **設計レビューは2人**（設計 §29-2）。同じ文書を読ませるが、**問いを分ける** ——
        // 同じ問いを2人に投げると、費用は2倍で発見はほとんど増えない。
        // どちらも作業ツリーを書き換えないので `ReadsOnly`（§29-1）。
        new("design-review-consistency", "設計レビュー（整合）",
            "設計文書が、他の節と矛盾していないかを見る。",
            AgentKind.CodexCli, AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode,
            Model: null, ReadsOnly: true),
        new("design-review-outside", "設計レビュー（外から）",
            "その設計で作られたものを使う人が、何に困るかを見る。",
            AgentKind.AntigravityCli, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode,
            Model: null, ReadsOnly: true),
    ];

    private static string? Validate(IReadOnlyList<DepartmentDefinition>? departments)
    {
        if (departments is null) return "Departments がありません";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var department in departments)
        {
            if (department is null) return "部門に null があります";
            if (!CompanyPaths.IsValidSlug(department.Id)) return $"部門 Id が不正です: {department.Id}";
            if (!ids.Add(department.Id)) return $"部門 Id が重複しています: {department.Id}";
            if (string.IsNullOrWhiteSpace(department.DisplayName)) return $"部門 DisplayName がありません: {department.Id}";
            if (string.IsNullOrWhiteSpace(department.Responsibility)) return $"部門 Responsibility がありません: {department.Id}";
            if (!Enum.IsDefined(department.Agent)) return $"未定義の Agent です: {department.Id}";
            if (!Enum.IsDefined(department.Mode)) return $"未定義の Mode です: {department.Id}";
            var capabilities = AgentCapabilities.For(department.Agent);
            if (department.Mode is DriveMode.Structured && !capabilities.SupportsStructuredConversation)
                return $"{department.Agent} は Structured をサポートしません: {department.Id}";
            // **承認の往復が無くても、握りつぶしを検出できるなら構造化でよい**（設計 §13-3 追記2）。
            // Antigravity は往復そのものが無い代わりに `result.denied_actions` を返す ——
            // `status:"SUCCESS"` が嘘をつくときの唯一の手がかりがそれで、§14-4 の3層判定はこれを見る。
            // ここを往復だけで判定していたので、**2026-09-06 に既定を Structured に変えたあと、
            // 既定の部門集合が保存も読み込みもできなくなっていた**（レビューで発覚）。
            if (department.Mode is DriveMode.Structured
                && !capabilities.SupportsRuntimeApprovalRoundTrip
                && !capabilities.ReportsDeniedActions)
                return $"{department.Agent} は Structured で承認の結果を確かめられません: {department.Id}";
        }
        return null;
    }

    private static async Task WriteAtomicallyAsync(string path, CompanyDefinition definition, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, definition, TaskStateJson.Options, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
