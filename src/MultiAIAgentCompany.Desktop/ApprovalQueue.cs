using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// (a) ランタイム承認を人間に見せて、決定を返す（設計 §3 / §5）。
/// </summary>
/// <remarks>
/// <b>(b) 判断の相談はここに来ない。</b> あれはファイル（<c>.company/</c>）で扱う ——
/// ベンダー非依存で、アプリが落ちても残る（§6）。
/// <para>
/// <b>Antigravity は承認の往復を持たない</b>ので、そもそもここへ要求が来ない。
/// 来ないことを「承認済み」と読まないこと（§2）。
/// </para>
/// </remarks>
/// <summary>承認の出どころ。<b>表示場所が違う</b>（設計 §1 / §17-2）。</summary>
public enum ApprovalSource
{
    /// <summary>部門。部門タイルの「承認を見る」から寄せる。</summary>
    Department,

    /// <summary>秘書。<b>中央の会話ペインに inline で出す</b>（§1）。</summary>
    Secretary,
}

public sealed class ApprovalQueue
{
    /// <summary>
    /// 拒否のときにエージェントへ渡す一言。<b>人間に文章を書かせない</b>が、
    /// 空のまま返しもしない（設計 §15-7）。
    /// </summary>
    public const string DenyReason = "人間が拒否しました。同じ操作を試し直さないでください。";

    /// <summary>
    /// 画面に出ている待ち行列。<b>これは写しであって正本ではない</b>（レビュー2周目で発覚）。
    /// </summary>
    /// <remarks>
    /// <c>ObservableCollection</c> は UI スレッドでしか触れないので、
    /// 追加も削除も post になる。**post の間は、並んでいるはずのものがここに居ない** ——
    /// その隙に「取り下げる」が走ると<b>取りこぼし、あとから死んだカードが現れる</b>。
    /// 正本は <see cref="_live"/> に置く。
    /// </remarks>
    public ObservableCollection<PendingApproval> Pending { get; } = [];

    /// <summary>いま人間の返事を待っているもの（正本）。</summary>
    private readonly HashSet<PendingApproval> _live = [];

    private readonly Lock _gate = new();

    /// <summary>
    /// その出どころの要求が並んでいるか。<b>写しではなく正本を見る</b>（設計 §36-1）。
    /// </summary>
    public bool HasPending(ApprovalSource source)
    {
        lock (_gate)
        {
            return _live.Any(pending => pending.Source == source);
        }
    }

    public void Add(PendingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        approval.Resolved += OnResolved;

        lock (_gate)
        {
            _live.Add(approval);
        }

        Dispatcher.UIThread.Post(() =>
        {
            // **並べる前に、まだ生きているか確かめる。** post を待っている間に
            // 取り下げ（相手のプロセスが終わった）が走っていることがある ——
            // 確かめないと**誰も消さないカード**が画面に残る。
            lock (_gate)
            {
                if (!_live.Contains(approval)) return;
            }

            Pending.Add(approval);
        });

        // **背面でも気付けるようにする**（設計 §28-4）。承認は人間の出番なので、
        // 気付かれないと部門が止まったまま待ち続ける。
        // **中身は書かない**（§10）—— 何を要求されたかはアプリの中で見る。
        DesktopNotifier.Notify("承認まち", $"{approval.DepartmentName} が承認を求めています");
    }

    /// <summary>
    /// もう答えられなくなった要求を取り下げる（設計 §36-3b）。
    /// </summary>
    /// <remarks>
    /// <b>相手のプロセスが終わったら、その承認カードは嘘になる。</b> 押しても届かないし、
    /// 残っていると<b>「人間の番だ」と言い続ける</b> —— §36 の沈黙の判定がそれを見るので、
    /// <b>次の turn が本当に返ってこなくても帯が出なくなる</b>（レビューで発覚）。
    /// <para>
    /// <b>黙って消さない</b>（§25-2）。何件取り下げたかを呼び出し元が人間に出す。
    /// </para>
    /// </remarks>
    /// <param name="withdrawn">
    /// 何件取り下げたか。<b>UI スレッドで呼ばれる。</b>
    /// 0 件でも呼ぶ —— 呼ばない分岐を作ると、呼び出し側が「言う／言わない」を2箇所で判断する。
    /// </param>
    public void Withdraw(ApprovalSource source, Action<int> withdrawn)
    {
        ArgumentNullException.ThrowIfNull(withdrawn);

        // **正本から先に外す。** ここを先にやるので、まだ並べられていない要求
        // （<see cref="Add"/> の post が走る前のもの）も確実に取りこぼさない。
        PendingApproval[] targets;
        lock (_gate)
        {
            targets = _live.Where(pending => pending.Source == source).ToArray();
            foreach (var approval in targets)
            {
                _live.Remove(approval);
            }
        }

        foreach (var approval in targets)
        {
            approval.Resolved -= OnResolved;
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var approval in targets)
            {
                Pending.Remove(approval);
            }

            withdrawn(targets.Length);
        });
    }

    private void OnResolved(object? sender, PendingApproval approval)
    {
        approval.Resolved -= OnResolved;

        lock (_gate)
        {
            _live.Remove(approval);
        }

        Dispatcher.UIThread.Post(() => Pending.Remove(approval));
    }
}

/// <summary>人間の返事を待っている承認要求1件。</summary>
public sealed class PendingApproval : INotifyPropertyChanged
{
    private readonly Func<ApprovalDecision, string?, CancellationToken, Task> _respond;
    /// <summary>秘書は3軸の状態を持たない（設計 §17-2）ので null になる。</summary>
    private readonly DepartmentStatusTracker? _tracker;
    private bool _busy;

    public PendingApproval(
        string departmentName,
        ApprovalRequest request,
        DepartmentStatusTracker? tracker,
        Func<ApprovalDecision, string?, CancellationToken, Task> respond,
        ApprovalSource source = ApprovalSource.Department)
    {
        DepartmentName = departmentName;
        Source = source;
        Request = request;
        _tracker = tracker;
        _respond = respond;
        RespondCommand = new RespondToApproval(this);
    }

    /// <summary>決定ボタンから呼ぶ。引数は<b>提示された決定そのもの</b>（設計 §5）。</summary>
    public ICommand RespondCommand { get; }

    public string DepartmentName { get; }

    /// <summary>どこに出すか（設計 §1 / §17-2）。</summary>
    public ApprovalSource Source { get; }

    public bool IsSecretary => Source is ApprovalSource.Secretary;

    public bool IsDepartment => Source is ApprovalSource.Department;

    public ApprovalRequest Request { get; }

    public string Title => Request.Title;

    public string Details => Request.Details;

    /// <summary>
    /// ボタンは<b>提示された決定から作る</b>（設計 §5）。
    /// Codex では版によって出る決定が変わり、<c>decline</c> は提示されない。
    /// ここで語彙を書き足さない。
    /// </summary>
    public IReadOnlyList<ApprovalDecision> Decisions => Request.AvailableDecisions;

    /// <summary>
    /// 「今回だけ / 常に許可」の材料（設計 §13-1）。<b>安全な要約だけ</b>で、
    /// ツールの入力そのものは含まない（§10）。v1 は読み取り専用で、ルールは書かない。
    /// </summary>
    public IReadOnlyList<string> SuggestedRules => Request.SuggestedRules;

    public bool HasSuggestions => SuggestedRules.Count > 0;

    /// <summary>返事の送信中。二重に押させない。</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            _busy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRespond)));
        }
    }

    public bool CanRespond => !_busy;

    /// <summary>
    /// 返事を送れなかった理由。<b>握りつぶさない</b> ——
    /// 例えば Antigravity には承認の往復が無く、送ろうとすると例外になる（§2）。
    /// </summary>
    public string? Error { get; private set; }

    public bool HasError => Error is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<PendingApproval>? Resolved;

    public async Task RespondAsync(ApprovalDecision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (_busy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 拒否のときだけ定型の一言を添える（§15-7）。Codex には送り先が無く、
            // その場合は Observed に残るだけになる —— この非対称は隠さない（§13-2）。
            var reason = IsDenial(decision.Id) ? ApprovalQueue.DenyReason : null;
            await _respond(decision, reason, ct);

            // 決定を送ったことは、エージェントが動き出した証拠ではない（§7）。
            // Working に戻さず、次の観測を待つ。
            _tracker?.OnApprovalResolved(Request.RequestId);
            Resolved?.Invoke(this, this);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 返事が届かなかったことを、届いたことにしない。要求は待ち行列に残す。
            // 例外のメッセージは人間に見せてよい（こちら側の型名と説明で、
            // エージェントの出力ではない）。
            Error = $"返事を送れなかった: {exception.Message}";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 拒否にあたる決定か。<b>提示された Id からしか判断しない</b> ——
    /// Codex は <c>cancel</c>、Claude は <c>deny</c> で、<c>decline</c> は提示されない（§13-2）。
    /// </summary>
    private static bool IsDenial(string decisionId) =>
        decisionId is "deny" or "cancel" or "decline" or "reject";
}

/// <summary>決定ボタン用の最小のコマンド。</summary>
internal sealed class RespondToApproval(PendingApproval approval) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => parameter is ApprovalDecision && approval.CanRespond;

    public async void Execute(object? parameter)
    {
        if (parameter is ApprovalDecision decision)
        {
            await approval.RespondAsync(decision, CancellationToken.None);
        }
    }
}
