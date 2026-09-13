using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Desktop;

/// <summary>権限モードの候補。<b>同じモードは等しい</b>ので選択を保てる（設計 §51-3）。</summary>
public sealed record PermissionModeChoice(AgentPermissionMode? Mode)
{
    public string Label => Mode is { } mode ? AgentPermissionModes.Label(mode) : "CLI の設定に任せる";
}

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
        PermissionMode = definition.PermissionMode;
        UpdatePermissionModeChoices();
        Model = definition.Model ?? string.Empty;
        ReasoningEffort = definition.ReasoningEffort ?? string.Empty;
        ReadsOnly = definition.ReadsOnly;
        ReportDeadlineMinutes = definition.ReportDeadlineMinutes?.ToString() ?? string.Empty;
        IsNew = isNew;
        UpdateEffortChoices();
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
            UpdatePermissionModeChoices();
            Raise();
            Raise(nameof(ModelHint));
            Raise(nameof(SupportsEffort));
        }
    }

    public DriveMode Mode { get => field; set { field = value; Raise(); Raise(nameof(IsStructured)); } }

    /// <summary>空欄は「CLI の設定に任せる」（設計 §46）。</summary>
    public string Model { get => field; set { field = value; Raise(); } }

    /// <summary><b>null は空欄として持つ。</b> ComboBox は選択が外れると null を書き戻してくる。</summary>
    public string ReasoningEffort { get => field; set { field = value ?? string.Empty; Raise(); } } = string.Empty;

    /// <summary>起動時の権限モード。<b>null は CLI 任せ</b>（設計 §51-2）。</summary>
    public AgentPermissionMode? PermissionMode
    {
        get => field;
        set
        {
            field = value;
            Raise();
            Raise(nameof(SelectedPermissionMode));
        }
    }

    public ObservableCollection<PermissionModeChoice> PermissionModeChoices { get; } = [new(null)];

    public PermissionModeChoice? SelectedPermissionMode
    {
        get => PermissionModeChoices.FirstOrDefault(choice => choice.Mode == PermissionMode);
        set => PermissionMode = value?.Mode;
    }

    /// <summary>候補を<b>その場で足し引きして選択を保つ</b>（設計 §51-3、§48）。</summary>
    private void UpdatePermissionModeChoices()
    {
        var modes = AgentPermissionModes.For(Agent);
        if (PermissionMode is { } mode && !modes.Contains(mode))
        {
            PermissionMode = null;
        }

        List<PermissionModeChoice> target = [new(null), .. modes.Select(mode => new PermissionModeChoice(mode))];
        for (var i = PermissionModeChoices.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(PermissionModeChoices[i]))
            {
                PermissionModeChoices.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < PermissionModeChoices.Count && PermissionModeChoices[i] == target[i])
            {
                continue;
            }

            var existing = PermissionModeChoices.IndexOf(target[i]);
            if (existing >= 0)
            {
                PermissionModeChoices.Move(existing, i);
            }
            else
            {
                PermissionModeChoices.Insert(i, target[i]);
            }
        }
    }

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

                // **選んだモデルが持たない強さは、残さない**（§48）。
                if (value.Efforts is { Count: > 0 } efforts
                    && ReasoningEffort is { Length: > 0 } current
                    && !efforts.Contains(current, StringComparer.Ordinal))
                {
                    ReasoningEffort = string.Empty;
                }
            }

            Raise();
            UpdateEffortChoices();
        }
    }

    /// <summary>候補を入れ直す（CLI を変えたときに呼ばれる）。</summary>
    public void SetChoices(IReadOnlyList<AgentModelChoice> choices, IReadOnlyList<string> cliEfforts)
    {
        ModelChoices.Clear();
        foreach (var choice in choices)
        {
            ModelChoices.Add(choice);
        }

        _cliEfforts = cliEfforts;
        SelectedModel = ModelChoices.FirstOrDefault(
            choice => string.Equals(choice.Id, Model, StringComparison.Ordinal));
        Raise(nameof(HasModelChoices));
        UpdateEffortChoices();
    }

    public string Title => $"{DisplayName}（{Id}）";

    /// <summary>
    /// その部門で選べる思考の強さ（設計 §48）。
    /// </summary>
    /// <remarks>
    /// <b>モデルごとに違う。</b> Codex は `gpt-5.5` が `xhigh` まで、
    /// `gpt-5.6-terra` は `ultra` まで —— **一律に出すと、設定できるのに起動しない
    /// 組み合わせを作れてしまう**（§47-2 で agy で踏んだのと同じ形）。
    /// <para>
    /// モデルが強さを持たないときは、**CLI に聞いた候補**に落とす。
    /// それも無ければ空（＝ CLI の設定に任せる、しか選べない）。
    /// </para>
    /// </remarks>
    public ObservableCollection<string> EffortChoices { get; } = [];

    /// <summary>
    /// 候補を<b>作り直さずに、その場で足し引きする</b>。
    /// </summary>
    /// <remarks>
    /// <b>開いた直後に強さが空欄になっていた</b>（実機で分かった）。候補は CLI に聞いてから
    /// 非同期で入るので、開いた直後は `[""]` しか無い。保存済みの `medium` が候補に無いと
    /// ComboBox は選択を外し、**後から候補を差し替えても、同じ値を通知し直しても選び直さない**
    /// （値が変わっていないので、ComboBox 側に変更が届かない）。部門を選び直すと出たのは、
    /// 一度別の値を経由するから。
    /// <para>
    /// だから <b>保存済みの値は、候補に無くても最初から入れておく</b>。そのうえで一覧を
    /// 丸ごと差し替えず、要らない項目を抜いて足りない項目を差し込む ——
    /// 選んでいる項目に触らないので、選択が外れない。
    /// </para>
    /// </remarks>
    private void UpdateEffortChoices()
    {
        List<string> target = SelectedModel?.Efforts is { Count: > 0 } fromModel
            ? ["", .. fromModel]
            : ["", .. _cliEfforts];
        if (ReasoningEffort.Length > 0 && !target.Contains(ReasoningEffort, StringComparer.Ordinal))
        {
            target.Add(ReasoningEffort);
        }

        for (var i = EffortChoices.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(EffortChoices[i], StringComparer.Ordinal))
            {
                EffortChoices.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < EffortChoices.Count && string.Equals(EffortChoices[i], target[i], StringComparison.Ordinal))
            {
                continue;
            }

            var existing = EffortChoices.IndexOf(target[i]);
            if (existing >= 0)
            {
                EffortChoices.Move(existing, i);
            }
            else
            {
                EffortChoices.Insert(i, target[i]);
            }
        }
    }

    private IReadOnlyList<string> _cliEfforts = [];

    /// <summary>モデル欄の下に出す例。<b>一覧は機械で取れない</b>ので、例として出す。</summary>
    public string ModelHint => Agent switch
    {
        AgentKind.ClaudeCode =>
            "空欄なら CLI の設定に任せる。別名も使える（opus / sonnet / fable）。"
            + "一覧は Claude Code からは取れないので、ここは打ち込みです",
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
        SupportsEffort ? Blank(ReasoningEffort) : null,

        // **保存時にも持たない値を落とす**（設計 §51-3）。
        PermissionMode is { } mode && AgentPermissionModes.For(Agent).Contains(mode) ? mode : null);

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
