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
/// <remarks>
/// <b><c>AutoApproveAllTools</c>（§30-4 の危険モード）は 2026-09-09 に廃止した</b>（§32-3）。
/// あれは「人間に聞く手段が無い CLI」への抜け道だったが、
/// <see cref="DriveMode.ExternalTerminal"/> という聞く手段ができたので要らない ——
/// <b>聞けるのに聞かない、を残さない。</b>
/// </remarks>
/// <param name="ReportDeadlineMinutes">報告を待つ分数。null は既定、0 以下は期限を見ない。</param>
/// <param name="ReasoningEffort">
/// CLI に渡す思考の強さ。null なら CLI の設定に任せる（設計 §46）。
/// </param>
/// <remarks>
/// <b><see cref="ReasoningEffort"/> を enum にしない。</b> 使える値が CLI ごとに違い、
/// **モデルによっても変わる**（Codex は `low` の上に `xhigh` / `max` / `ultra` を持つものがある）。
/// こちらで閉じた集合にすると、**CLI が増やした値を人間が指定できなくなる。**
/// </remarks>
/// <param name="PermissionMode">起動時の権限モード。<b>null は CLI の設定に任せる</b>（設計 §51-2）。</param>
public sealed record DepartmentDefinition(
    string Id, string DisplayName, string Responsibility, AgentKind Agent, DriveMode Mode,
    string? Model = null, bool ReadsOnly = false,
    int? ReportDeadlineMinutes = null, string? ReasoningEffort = null,
    AgentPermissionMode? PermissionMode = null)
{
    /// <summary>期限がファイルに書かれていないときに使う既定。</summary>
    public static readonly TimeSpan DefaultReportDeadline = TimeSpan.FromMinutes(30);

    /// <summary>この部門に対して報告を待つ期限。null なら期限を見ない。</summary>
    /// <remarks>計算値はファイルへ書かない（§30-6）。人間が触る鍵は ReportDeadlineMinutes だけ。</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? ReportDeadline => ReportDeadlineMinutes switch
    {
        null => DefaultReportDeadline,
        <= 0 => null,
        var minutes => TimeSpan.FromMinutes(minutes.Value),
    };

}

/// <remarks>
/// <b>record の等値比較は <see cref="Departments"/> を要素で見ない</b>
/// （C# の record はコレクションを参照で比べるので、配列と List は別物になる）。
/// 中身を比べたいときは <c>Departments</c> を明示的に突き合わせること。
/// </remarks>
/// <summary>
/// 秘書に使う CLI（設計 §46）。
/// </summary>
/// <remarks>
/// <b>秘書は構造化でしか務まらない。</b> 中央ペインで会話し、承認をアプリ内のボタンで
/// 受ける前提なので、<b>構造化の会話を持たない CLI は選べない</b>。
/// とくに headless の Antigravity は<b>ツール権限を人間に聞けず全部自動拒否する</b>（§30-1 の実測）——
/// 選ばせると、`.company/` を読むことすらできない秘書ができあがる。
/// <para>
/// <b>ファイルに無ければ Claude Code。</b> これまでの実装が固定でそうしていたので、
/// **古いフォルダを開いたときに挙動が変わらない**（§23 の姿勢）。
/// </para>
/// </remarks>
/// <param name="Agent">担当する CLI。</param>
/// <param name="Model">渡すモデル。null なら CLI の設定に任せる。</param>
/// <param name="ReasoningEffort">渡す思考の強さ。null なら CLI の設定に任せる。</param>
public sealed record SecretaryDefinition(
    AgentKind Agent = AgentKind.ClaudeCode, string? Model = null, string? ReasoningEffort = null);

/// <param name="Secretary">
/// 秘書の設定（設計 §46）。<b>古いファイルには無い</b>ので、null なら既定を使う。
/// </param>
public sealed record CompanyDefinition(
    long Revision, IReadOnlyList<DepartmentDefinition> Departments, SecretaryDefinition? Secretary = null)
{
    /// <summary>秘書の設定。<b>無ければ既定</b>（Claude Code、モデルと強さは CLI 任せ）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SecretaryDefinition SecretaryOrDefault => Secretary ?? new SecretaryDefinition();
}

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

    /// <summary>
    /// <c>departments.json</c> のうち、<b>アプリが読まなかったキー</b>を挙げる（設計 §32-10）。
    /// </summary>
    /// <remarks>
    /// <b>JSON は未知のキーを黙って捨てる</b>（既定の <c>UnmappedMemberHandling</c>）。
    /// だから廃止したキーが残っていても読めてしまい、**人間が書いたものが
    /// 黙って無視される** —— §30-6 で潰したはずの形が、**廃止した側から**戻ってくる。
    /// <para>
    /// <b>弾かない。</b> 余分なキーがあるだけで開けなくすると、
    /// 人間が書き置き代わりに1行足しただけでフォルダが死ぬ。**言うだけにする。**
    /// </para>
    /// <para>
    /// <b>定義の型に <c>JsonExtensionData</c> を持たせない。</b> record の等値比較に
    /// 辞書が入ると参照比較になり、**読むたびに「顔ぶれが変わった」ことになって
    /// 部門タイルが毎回作り直される**（§30-6 の「同じ顔ぶれなら作り直さない」が壊れる）。
    /// だからここでは<b>生の JSON をもう一度読む。</b>
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> FindUnreadKeysAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_paths.Departments))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(_paths.Departments, ct));
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("departments", out var departments)
                || departments.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            var unread = new List<string>();
            foreach (var department in departments.EnumerateArray())
            {
                if (department.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                var id = department.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                foreach (var property in department.EnumerateObject())
                {
                    if (!KnownKeys.Contains(property.Name))
                    {
                        unread.Add($"{id ?? "(id 不明)"}: {property.Name}");
                    }
                }
            }

            return unread;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // **読めないことは、ここでは扱わない。** それは ReadAsync が Unreadable として返す。
            return [];
        }
    }

    /// <summary>
    /// アプリが読むキー。<b><see cref="DepartmentDefinition"/> と一緒に直すこと。</b>
    /// </summary>
    /// <remarks>
    /// camelCase で持つ（<see cref="TaskStateJson.Options"/> がそう書く）。
    /// <b>ここを直し忘れると、読んでいるキーを「読んでいない」と言う</b>ので、
    /// テストで既定の部門を往復させて見張る。
    /// </remarks>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "id", "displayName", "responsibility", "agent", "mode",
        "model", "readsOnly", "reportDeadlineMinutes", "reasoningEffort", "permissionMode",
    };

    /// <param name="secretary">
    /// 秘書の設定（設計 §46-3）。null なら**いまの値を保つ** ——
    /// 部門だけを直す呼び出しで、秘書の設定を黙って既定へ戻さないため。
    /// </param>
    public async Task<DefinitionWriteResult> SaveAsync(
        CompanyDefinition expected, IReadOnlyList<DepartmentDefinition> departments,
        SecretaryDefinition? secretary, CancellationToken ct)
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

        var next = new CompanyDefinition(
            checked(currentRevision + 1), departments.ToArray(),
            secretary ?? (read as DefinitionReadResult.Found)?.Definition.Secretary);
        await WriteAtomicallyAsync(_paths.Departments, next, ct);
        return new DefinitionWriteResult.Written(next);
    }

    /// <summary>秘書を触らずに部門だけ保存する。</summary>
    public Task<DefinitionWriteResult> SaveAsync(
        CompanyDefinition expected, IReadOnlyList<DepartmentDefinition> departments, CancellationToken ct) =>
        SaveAsync(expected, departments, secretary: null, ct);

    /// <summary>監査部門の ID（設計 §59）。<b>秘書が計画の最後に足す工程を、この ID で探す。</b></summary>
    public const string AuditDepartmentId = "audit";

    /// <summary>設計部門の ID。<b>計画で実装の前に要る工程を、この ID で探す</b>（設計 §62-18）。</summary>
    public const string DesignDepartmentId = "design";

    /// <summary>実装部門の ID（設計 §62-18）。</summary>
    public const string ImplementationDepartmentId = "implementation";

    /// <summary>既定の部門（§29 / §56 / §59）。各 CLI の既定モードは能力定義から取る。</summary>
    public static IReadOnlyList<DepartmentDefinition> CreateDefaultDepartments() =>
    [
        new("design", "設計", "要件と設計判断を整理する。", AgentKind.ClaudeCode, AgentCapabilities.For(AgentKind.ClaudeCode).DefaultDriveMode, ReportDeadlineMinutes: 30),
        new("implementation", "実装", "承認された設計を実装する。", AgentKind.CodexCli, AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode, ReportDeadlineMinutes: 30),
        new("research", "調査", "技術的な選択肢と根拠を調査する。", AgentKind.AntigravityCli, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode, ReportDeadlineMinutes: 30),
        new("review", "レビュー", "変更をレビューし、懸念を報告する。", AgentKind.ClaudeCode, AgentCapabilities.For(AgentKind.ClaudeCode).DefaultDriveMode, ReportDeadlineMinutes: 30),
        new("testing", "テスト", "テストを実行し、結果を報告する。", AgentKind.CodexCli, AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode, ReportDeadlineMinutes: 30),

        // **設計レビューは2人**（設計 §29-2）。同じ文書を読ませるが、**問いを分ける** ——
        // 同じ問いを2人に投げると、費用は2倍で発見はほとんど増えない。
        // どちらも作業ツリーを書き換えないので `ReadsOnly`（§29-1）。
        new("design-review-consistency", "設計レビュー（整合）",
            "設計文書が、他の節と矛盾していないかを見る。",
            AgentKind.CodexCli, AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode,
            Model: null, ReadsOnly: true, ReportDeadlineMinutes: 30),
        new("design-review-outside", "設計レビュー（外から）",
            "その設計で作られたものを使う人が、何に困るかを見る。",
            AgentKind.AntigravityCli, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode,
            Model: null, ReadsOnly: true, ReportDeadlineMinutes: 30),

        // **画像は仕事の成果物にする**（設計 §56-3）。約束はこの部門の責務に収める。
        new("designer", "デザイナー",
            "画像生成で画像・イラスト・バナーを作る。仕事のフォルダ（instruction.md と同じ場所）の images/ に置く。"
            + "ファイル名に空白を入れず、拡張子は png / jpg / jpeg / webp / gif。"
            + "report.md にワークスペースからの相対パス .company/tasks/<slug>/images/<name>.png を書く。",
            AgentKind.CodexCli, AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode,
            ReadsOnly: true, ReportDeadlineMinutes: 30),

        // **commit / push の前の監査**（設計 §59）。git の操作は人間の仕事のまま —— 監査は見て報告するだけで、直さない。
        // 秘書が「作業ツリーを書き換える計画」の最後にレビュー工程として足すので、NG（`verdict: revise`）は見た工程へ自動で送り直される（§37-5）。
        new(AuditDepartmentId, "監査",
            "commit / push の前に、変更に個人情報・秘密情報、ライセンス違反、著作権侵害が無いかを確かめる。"
            + "見るのは、まだコミットしていない変更（git status と git diff HEAD、未追跡のファイルの中身）と、"
            + "まだ push していないコミット（git log -p @{u}..。上流が無ければ履歴全体）。"
            + "個人情報・秘密情報: 実名、メールアドレス、電話番号、住所、ユーザー名を含む絶対パス、API キー・トークン・パスワード、コミットの作者とメールアドレス。"
            + "ライセンス: 持ち込んだコードや追加した依存の条件が、このリポジトリのライセンスと両立するか。表示義務（著作権表示・ライセンス文）を満たしているか。"
            + "著作権: 他者の文章・画像・コードを許可なく転載していないか。"
            + "直さない。見つけたものごとに、場所（ファイルと行、またはコミット）・理由・直し方の案を report.md に書く。",
            AgentKind.AntigravityCli, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode,
            ReadsOnly: true, ReportDeadlineMinutes: 30),
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

            // **数値でも読めてしまう**（`JsonStringEnumConverter` は整数を許す）ので、Agent / Mode と同じく弾く（設計 §51、Codex の指摘）。
            if (department.PermissionMode is { } permissionMode && !Enum.IsDefined(permissionMode))
            {
                return $"未定義の PermissionMode です: {department.Id}";
            }

            // **Antigravity は思考の強さを設定として持てない**（設計 §47-2、実機で確かめた）。
            // あちらは強さが**モデル名に畳まれていて**（`gemini-3.8-flash-high`）、
            // `--effort` を併せて渡すと **`conflicts with --effort=…` で起動しない。**
            // **保存の時点で弾く** —— 通すと、開くたびに失敗する部門ができる。
            if (department.Agent is AgentKind.AntigravityCli
                && department.ReasoningEffort is { Length: > 0 })
            {
                return $"{department.Agent} では思考の強さを設定できません（モデル名に含まれます）: {department.Id}";
            }
            var capabilities = AgentCapabilities.For(department.Agent);

            // **ExternalTerminal はどの CLI でも成立する**（設計 §32-2）——
            // 3つとも対話起動でき、人間がその窓で承認できることを実機で確かめた。
            // 下の検査は構造化に固有の話なので、ここで抜ける。
            if (department.Mode is DriveMode.ExternalTerminal) continue;

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
