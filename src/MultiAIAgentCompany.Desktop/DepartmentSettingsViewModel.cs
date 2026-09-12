using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 設定画面で編集している1部門（設計 §47）。
/// </summary>
/// <remarks>
/// <b>編集中の値をここに持つ。</b> `DepartmentDefinition` は record なので、
/// 画面の途中経過（まだ保存していない状態）を入れる場所が要る。
/// <para>
/// <b>Id は新規のときだけ編集できる。</b> `state.json` は `departmentId` しか持たないので、
/// **変えるのは「別の部門にする」こと**であり、過去の仕事が迷子になる。
/// </para>
/// </remarks>
public sealed class DepartmentEdit : INotifyPropertyChanged
{
    public DepartmentEdit(DepartmentDefinition definition, bool isNew)
    {
        Id = definition.Id;
        DisplayName = definition.DisplayName;
        Responsibility = definition.Responsibility;
        Agent = definition.Agent;
        Mode = definition.Mode;
        Model = definition.Model ?? string.Empty;
        ReasoningEffort = definition.ReasoningEffort ?? string.Empty;
        ReadsOnly = definition.ReadsOnly;
        ReportDeadlineMinutes = definition.ReportDeadlineMinutes?.ToString() ?? string.Empty;
        IsNew = isNew;
    }

    public string Id { get => field; set { field = value; Raise(); Raise(nameof(Title)); } }

    public string DisplayName { get => field; set { field = value; Raise(); Raise(nameof(Title)); } }

    public string Responsibility { get => field; set { field = value; Raise(); } }

    public AgentKind Agent
    {
        get => field;
        set
        {
            field = value;
            Raise();
            Raise(nameof(ModelHint));
            Raise(nameof(SupportsEffort));
        }
    }

    public DriveMode Mode { get => field; set { field = value; Raise(); Raise(nameof(IsStructured)); } }

    /// <summary>空欄は「CLI の設定に任せる」（設計 §46）。</summary>
    public string Model { get => field; set { field = value; Raise(); } }

    public string ReasoningEffort { get => field; set { field = value; Raise(); } }

    public bool ReadsOnly { get => field; set { field = value; Raise(); } }

    /// <summary>空欄なら既定（30分）。文字列で持つのは、空欄と 0 を分けるため。</summary>
    public string ReportDeadlineMinutes { get => field; set { field = value; Raise(); } }

    /// <summary>新規作成中か。<b>Id を編集できるのはこのときだけ。</b></summary>
    public bool IsNew { get; }

    /// <summary>
    /// 駆動モードが構造化のままか（設計 §46）。
    /// </summary>
    /// <remarks>
    /// <b>画面では選ばせない</b>が、**手で書いたファイルには入り得る** ——
    /// そのときだけ警告と「外部ターミナルに直す」を出す（Codex の指摘）。
    /// </remarks>
    public bool IsStructured => Mode is DriveMode.Structured;

    /// <summary>
    /// 思考の強さを設定として持てるか（設計 §47-2、実機で分かった）。
    /// </summary>
    /// <remarks>
    /// <b>Antigravity は持てない。</b> あちらは<b>強さがモデル名に畳まれている</b>
    /// （<c>Gemini 3.8 Flash (High)</c>）ので、`--effort` を付けると
    /// **「そのモデルは --effort に対応していない」と弾かれて起動しない。**
    /// <para>
    /// <c>--effort</c> という旗は存在する。**旗があることと、使えることは別**だった。
    /// </para>
    /// </remarks>
    public bool SupportsEffort => Agent is not AgentKind.AntigravityCli;

    /// <summary>
    /// モデルの候補（設計 §47-2）。<b>CLI から取れたときだけ入る。</b>
    /// </summary>
    public ObservableCollection<AgentModelChoice> ModelChoices { get; } = [];

    /// <summary>候補を選べるか。<b>取れない CLI では自由入力のまま。</b></summary>
    public bool HasModelChoices => ModelChoices.Count > 0;

    /// <summary>一覧から選んだモデル。<b>入れるのは id</b>（CLI に渡すのはこちら）。</summary>
    public AgentModelChoice? SelectedModel
    {
        get => field;
        set
        {
            field = value;
            if (value is not null)
            {
                Model = value.Id;
            }

            Raise();
        }
    }

    /// <summary>候補を入れ直す（CLI を変えたときに呼ばれる）。</summary>
    public void SetModelChoices(IReadOnlyList<AgentModelChoice> choices)
    {
        ModelChoices.Clear();
        foreach (var choice in choices)
        {
            ModelChoices.Add(choice);
        }

        SelectedModel = ModelChoices.FirstOrDefault(
            choice => string.Equals(choice.Id, Model, StringComparison.Ordinal));
        Raise(nameof(HasModelChoices));
    }

    public string Title => $"{DisplayName}（{Id}）";

    /// <summary>
    /// その CLI で使える思考の強さ（設計 §46）。
    /// </summary>
    /// <remarks>
    /// <b>これが全部ではない。</b> 使える値は CLI とモデルで変わる
    /// （Codex には `xhigh` / `max` / `ultra` を持つモデルがある）——
    /// **候補は出すが、打ち込めるようにしておく。**
    /// </remarks>
    public IReadOnlyList<string> EffortChoices { get; } = ["", "low", "medium", "high", "xhigh", "max"];

    /// <summary>モデル欄の下に出す例。<b>一覧は機械で取れない</b>ので、例として出す。</summary>
    public string ModelHint => Agent switch
    {
        AgentKind.ClaudeCode => "空欄なら CLI の設定に任せる（例: claude-opus-5 / claude-sonnet-5）",
        AgentKind.CodexCli => "空欄なら CLI の設定に任せる（例: gpt-5.6-terra / gpt-5.6-sol）",
        AgentKind.AntigravityCli => "強さはモデル名に含まれます（例: Gemini 3.8 Flash (High)）",
        _ => "空欄なら CLI の設定に任せる",
    };

    /// <summary>編集中の値を定義に戻す。<b>空欄は null</b>（「指定しない」の意味）。</summary>
    public DepartmentDefinition ToDefinition() => new(
        Id.Trim(), DisplayName.Trim(), Responsibility.Trim(), Agent, Mode,
        Blank(Model), ReadsOnly,
        int.TryParse(ReportDeadlineMinutes.Trim(), out var minutes) ? minutes : null,

        // **持てない CLI では捨てる**（設計 §47-2、実機で踏んだ）。
        // 欄を隠しただけでは、**ファイルに残った値がそのまま渡り続ける** ——
        // Antigravity は `--model gemini-3.8-flash-high --effort low` を
        // **`conflicts with --effort=low` で弾く**ので、モデルを変えた瞬間に起動しなくなる。
        SupportsEffort ? Blank(ReasoningEffort) : null);

    private static string? Blank(string value) => value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


/// <summary>
/// 設定画面そのもの（設計 §47）。
/// </summary>
/// <remarks>
/// <b>保存するまでディスクに触らない。</b> 編集は全部ここに溜めて、
/// 「保存」で `DepartmentStore.SaveAsync` に一度で渡す ——
/// **検証（`Validate`）と楽観ロック（`Revision`）は Core が持っている**ので、
/// 画面はその結果を出すだけにする（§33-6 の「書く前に検証する」）。
/// </remarks>
public sealed class DepartmentSettingsViewModel : INotifyPropertyChanged
{
    public ObservableCollection<DepartmentEdit> Departments { get; } = [];

    public DepartmentEdit? Selected
    {
        get => field;
        set { field = value; Raise(); Raise(nameof(HasSelection)); }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>秘書の設定（設計 §46-3）。</summary>
    public AgentKind SecretaryAgent { get => field; set { field = value; Raise(); } } = AgentKind.ClaudeCode;

    public string SecretaryModel { get => field; set { field = value; Raise(); } } = string.Empty;

    public string SecretaryEffort { get => field; set { field = value; Raise(); } } = string.Empty;

    /// <summary>
    /// 秘書に選べる CLI（設計 §46-3）。
    /// </summary>
    /// <remarks>
    /// <b>Antigravity は入れない。</b> headless ではツール権限を人間に聞けず全部自動拒否するので
    /// （§30-1 の実測）、`.company/` を読むことすらできない秘書ができあがる。
    /// </remarks>
    public IReadOnlyList<AgentKind> SecretaryChoices { get; } = [AgentKind.ClaudeCode, AgentKind.CodexCli];

    public IReadOnlyList<AgentKind> AgentChoices { get; } =
        [AgentKind.ClaudeCode, AgentKind.CodexCli, AgentKind.AntigravityCli];

    /// <summary>保存や削除の結果。<b>黙って失敗しない</b>（§28-1）。</summary>
    public string Message { get => field; set { field = value; Raise(); Raise(nameof(HasMessage)); } } = string.Empty;

    public bool HasMessage => Message.Length > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
