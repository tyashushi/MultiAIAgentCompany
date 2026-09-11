namespace MultiAIAgentCompany.Core.Sessions;

/// <param name="Written">その場で書いたか。false なら積んだ。</param>
/// <param name="Queued">自分を含めて、まだ書かれていない数。</param>
public sealed record SendOutcome(bool Written, int Queued)
{
    public static readonly SendOutcome Sent = new(true, 0);
}

/// <summary>
/// 送信を turn ごとに直列化する（設計 §32-12）。
/// </summary>
/// <remarks>
/// <b>1つの turn が終わるまで、次を stdin に書かない。</b>
/// <para>
/// これが無いと、<c>SendUserMessageAsync</c> は<b>行を書くだけ</b>なので、
/// **前の turn がツールを実行している最中に次が割り込む。**
/// 秘書の1通目（protocol を読ませる）と人間の本文で実際にその形があった（§32-12）。
/// </para>
/// <para>
/// <b>待たせる責務を呼び出し側に置かない。</b> 置くと
/// **将来また別の経路から二重送信できる** —— 書き込み口をここ1つに閉じる。
/// </para>
/// <para>
/// <b>終端と認めるのは「turn が終わった」と「もう書けない」だけ。</b>
/// **無音では開けない** —— §7 の「沈黙から推定しない」（quota 超過を無言でリトライして
/// 1時間生存した実測）がそのまま効く。黙ったまま返ってこない turn は、
/// **人間が止めるまで次を流さない。**
/// </para>
/// </remarks>
public sealed class TurnGate(Func<string, CancellationToken, Task> write)
{
    private readonly Func<string, CancellationToken, Task> _write =
        write ?? throw new ArgumentNullException(nameof(write));

    private readonly Queue<string> _pending = new();
    private readonly Lock _gate = new();
    private bool _inFlight;
    private bool _closed;

    /// <summary>いま走っている turn があるか。</summary>
    public bool InFlight { get { lock (_gate) { return _inFlight; } } }

    /// <summary>まだ書かれていない数。<b>画面に出す</b> —— 積んだことを黙らない。</summary>
    public int Queued { get { lock (_gate) { return _pending.Count; } } }

    /// <summary>
    /// 送る。<b>走っている turn があれば積む。</b>
    /// </summary>
    /// <returns>その場で書いたか、積んだか。<b>呼び出し元が人間に伝えられるように返す。</b></returns>
    public async Task<SendOutcome> SendAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            // **積んであるものがあるなら、走っていなくても積む**（レビューで発覚）。
            // ここで割り込むと、**先に積まれたものを追い越す** ——
            // 直列化のためにある型が、順番を壊すことになる。
            if (_inFlight || _pending.Count > 0)
            {
                _pending.Enqueue(text);
                return new SendOutcome(false, _pending.Count);
            }

            _inFlight = true;
        }

        try
        {
            await _write(text, ct).ConfigureAwait(false);
            return SendOutcome.Sent;
        }
        catch
        {
            // **書けなかったなら、走っていない。** ここを開けておかないと、
            // 以後の送信が永久に積まれるだけになる。
            lock (_gate)
            {
                _inFlight = false;
            }

            // **積んであるものを置き去りにしない**（レビューで発覚）。
            // 失敗した turn は `OnTurnFinishedAsync` を呼ばれないので、
            // ここで流さないと**待ち行列が永久に動かない。**
            await DrainAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// turn が終わった。<b>積んであるものがあれば、次の1つを書く。</b>
    /// </summary>
    /// <remarks>
    /// <b>セッションの読み取りループから呼ぶ。</b> 呼ばれないと次が永久に出ないので、
    /// <b>失敗した turn でも呼ぶこと</b> —— 「失敗した」も turn の終わりである。
    /// </remarks>
    public Task OnTurnFinishedAsync(CancellationToken ct) => DrainAsync(ct);

    private async Task DrainAsync(CancellationToken ct)
    {
        while (true)
        {
            string next;
            lock (_gate)
            {
                if (_closed || _pending.Count is 0)
                {
                    _inFlight = false;
                    return;
                }

                next = _pending.Dequeue();
                _inFlight = true;
            }

            try
            {
                await _write(next, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // **1つ書けなくても、残りを道連れにしない。** 次の周で続きを試す。
                // 書けなかったことは呼び出し元（セッション）が診断に出す。
            }
        }
    }

    /// <summary>
    /// もう書けない。<b>積んであったものを返す</b> —— 黙って捨てない（§25-2）。
    /// </summary>
    public IReadOnlyList<string> Close()
    {
        lock (_gate)
        {
            _closed = true;
            var dropped = _pending.ToArray();
            _pending.Clear();
            _inFlight = false;
            return dropped;
        }
    }
}
