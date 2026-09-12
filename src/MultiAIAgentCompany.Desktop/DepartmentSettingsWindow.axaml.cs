using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 部門の設定（設計 §47）。
/// </summary>
/// <remarks>
/// <b>保存するまでディスクに触らない。</b> 「保存」で <see cref="DepartmentStore.SaveAsync"/> に
/// 一度で渡す —— <b>検証と楽観ロックは Core が持っている</b>ので、画面はその結果を出すだけ。
/// <para>
/// <b>読み直しは人間が決める。</b> 外部で `departments.json` が編集されていたら
/// `Revision` が合わずに弾かれる。**勝手に上書きしない**（Codex の指摘）。
/// </para>
/// </remarks>
public partial class DepartmentSettingsWindow : Window
{
    private readonly DepartmentSettingsViewModel _model = new();
    private readonly DepartmentStore? _store;
    private readonly Func<string, Task<DepartmentRemovalDecision>>? _canRemove;
    private readonly Func<string, Task>? _stopSession;
    private CompanyDefinition _loaded = new(0, []);

    /// <summary>CLI ごとのモデル一覧。<b>1回だけ聞く</b>（ネットワークへ出るため）。</summary>
    private readonly Dictionary<AgentKind, IReadOnlyList<AgentModelChoice>> _catalog = [];

    /// <summary>XAML プレビュー用。</summary>
    public DepartmentSettingsWindow()
    {
        DataContext = _model;
        InitializeComponent();
    }

    /// <param name="store">読み書きする先。</param>
    /// <param name="canRemove">その部門を消してよいか（<see cref="DepartmentRemoval"/> に繋ぐ）。</param>
    /// <param name="stopSession">その部門のターミナルを終わらせる。</param>
    public DepartmentSettingsWindow(
        DepartmentStore store,
        Func<string, Task<DepartmentRemovalDecision>> canRemove,
        Func<string, Task> stopSession) : this()
    {
        _store = store;
        _canRemove = canRemove;
        _stopSession = stopSession;
    }

    /// <summary>ディスクから読み込む。<b>開くたびに読む</b> —— 外で変わっているかもしれない。</summary>
    public async Task LoadAsync()
    {
        if (_store is null)
        {
            return;
        }

        switch (await _store.ReadAsync(CancellationToken.None))
        {
            case DefinitionReadResult.Found found:
                Apply(found.Definition);
                break;

            case DefinitionReadResult.Unreadable broken:
                // **既定に落とさない**（§30-6）。読めないものを空として開くと、
                // 保存した瞬間に人間の設定を消す。
                _model.Message = $"departments.json を読めません: {broken.Reason}（このまま保存しないこと）";
                break;

            default:
                _model.Message = "departments.json がありません（フォルダを開き直すと作られます）";
                break;
        }
    }

    private void Apply(CompanyDefinition definition)
    {
        _loaded = definition;
        _model.Departments.Clear();
        foreach (var department in definition.Departments)
        {
            _model.Departments.Add(new DepartmentEdit(department, isNew: false));
        }

        _ = LoadModelChoicesAsync();

        var secretary = definition.SecretaryOrDefault;
        _model.SecretaryAgent = secretary.Agent;
        _model.SecretaryModel = secretary.Model ?? string.Empty;
        _model.SecretaryEffort = secretary.ReasoningEffort ?? string.Empty;
        _model.Selected = _model.Departments.FirstOrDefault();
    }

    /// <summary>
    /// モデルの候補を CLI から取る（設計 §47-2）。
    /// </summary>
    /// <remarks>
    /// <b>取れたものだけ入れる。</b> 取れない CLI（Claude / Codex）は自由入力のまま ——
    /// **それらしい一覧をこちらで作らない**（§7）。
    /// <para>
    /// <b>1つの CLI につき1回だけ聞く。</b> `agy models` はネットワークへ出るので、
    /// 部門を選ぶたびに走らせない。
    /// </para>
    /// </remarks>
    private async Task LoadModelChoicesAsync()
    {
        foreach (var kind in _model.AgentChoices.Where(AgentModelCatalog.CanList))
        {
            if (!_catalog.TryGetValue(kind, out var choices))
            {
                choices = await AgentModelCatalog.ListAsync(kind, CancellationToken.None);
                _catalog[kind] = choices;
            }

            foreach (var edit in _model.Departments.Where(edit => edit.Agent == kind))
            {
                edit.SetModelChoices(choices);
            }
        }
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        // **責務は空のまま作る。** 仮文を入れて保存できるようにすると、嘘が残る（Codex の指摘）。
        var id = $"department-{_model.Departments.Count + 1}";
        while (_model.Departments.Any(d => string.Equals(d.Id, id, StringComparison.Ordinal)))
        {
            id += "-2";
        }

        var edit = new DepartmentEdit(
            new DepartmentDefinition(id, "新しい部門", string.Empty, AgentKind.CodexCli, DriveMode.ExternalTerminal,
                ReportDeadlineMinutes: 30),
            isNew: true);

        _model.Departments.Add(edit);
        _model.Selected = edit;
        _model.Message = "責務を書いてから保存してください（空のままでは保存できません）";
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } selected)
        {
            return;
        }

        // 追加しただけでまだ保存していないものは、そのまま消してよい。
        if (selected.IsNew)
        {
            _model.Departments.Remove(selected);
            _model.Selected = _model.Departments.FirstOrDefault();
            _model.Message = $"{selected.DisplayName} を取り消した（保存前だったので、何も消えていない）";
            return;
        }

        switch (_canRemove is null ? null : await _canRemove(selected.Id))
        {
            case DepartmentRemovalDecision.Blocked blocked:
                _model.Message =
                    $"{selected.DisplayName} は消せません:\n・{string.Join("\n・", blocked.Reasons)}";
                return;

            case DepartmentRemovalDecision.NeedsSessionStop:
                // **窓が残ると、人間が制御できない CLI がワークスペースを書ける**（§47）。
                if (_stopSession is not null)
                {
                    await _stopSession(selected.Id);
                    _model.Message = $"{selected.DisplayName} のターミナルを終了した。もう一度「削除」で消えます";
                }

                return;

            case DepartmentRemovalDecision.Allowed:
                _model.Departments.Remove(selected);
                _model.Selected = _model.Departments.FirstOrDefault();
                _model.Message = $"{selected.DisplayName} を一覧から外した（保存で確定します）";
                return;

            default:
                _model.Message = "消してよいか判定できませんでした";
                return;
        }
    }

    private void OnUseTerminal(object? sender, RoutedEventArgs e)
    {
        if (_model.Selected is { } selected)
        {
            selected.Mode = DriveMode.ExternalTerminal;
            _model.Message = $"{selected.DisplayName} を外部ターミナルにした（保存で確定します）";
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            return;
        }

        var departments = _model.Departments.Select(edit => edit.ToDefinition()).ToArray();
        var secretary = new SecretaryDefinition(
            _model.SecretaryAgent,
            Blank(_model.SecretaryModel),
            Blank(_model.SecretaryEffort));

        var result = await _store.SaveAsync(_loaded, departments, secretary, CancellationToken.None);
        switch (result)
        {
            case DefinitionWriteResult.Written written:
                Apply(written.Definition);
                _model.Message = "保存した（新しい設定は、次に渡す仕事から効きます）";
                break;

            case DefinitionWriteResult.Rejected rejected:
                // **上書きしない。** 外で変わっていたら、人間が読み直して決める（Codex の指摘）。
                _model.Message = $"保存できません: {rejected.Reason}";
                break;

            default:
                _model.Message = $"保存できませんでした（{result.GetType().Name}）";
                break;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private static string? Blank(string value) => value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
