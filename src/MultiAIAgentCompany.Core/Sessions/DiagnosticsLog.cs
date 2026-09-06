namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>
/// 診断の直近だけを持つ（設計 §22-2）。<b>ライブ専用。保存しない。</b>
/// </summary>
/// <remarks>
/// <b>上限を持ち、捨てた数を数える。</b> 無制限に貯めると、長時間動かした部門で
/// メモリが伸び続ける。かといって黙って捨てると、**画面に出ている範囲が全部だと
/// 人間が思い込む** —— この案件で繰り返している「静かに壊れて動いているように見える」形。
/// <para>
/// <b>redact しない中身を持つ</b>ので、ここから <see cref="Status.Evidence"/> へ渡さない（§10）。
/// </para>
/// </remarks>
public sealed class DiagnosticsLog
{
    private readonly Queue<LiveDiagnostic> _lines = new();
    private readonly object _gate = new();
    private readonly int _capacity;

    public DiagnosticsLog(int capacity = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>捨てた行数。<b>0 でないなら画面に出す</b> —— 全部だと思わせない。</summary>
    public int Dropped { get; private set; }

    public void Add(LiveDiagnostic line)
    {
        ArgumentNullException.ThrowIfNull(line);

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
                Dropped++;
            }
        }
    }

    /// <summary>新しい順に最大 <paramref name="count"/> 行。</summary>
    public IReadOnlyList<LiveDiagnostic> Recent(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        lock (_gate)
        {
            return [.. _lines.Reverse().Take(count)];
        }
    }

    /// <summary>
    /// 何も無いことを「正常」と読ませないための1行（設計 §7）。
    /// </summary>
    public string Summary => Dropped is 0
        ? $"診断 {Count} 行"
        : $"診断 {Count} 行（古い {Dropped} 行は捨てた）";

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }
}
