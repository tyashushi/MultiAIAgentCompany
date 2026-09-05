namespace MultiAIAgentCompany.Core.Agents;

/// <summary>
/// TUI モードで「送信」にあたる端末バイト列。設計 §13-8 とその訂正。
/// </summary>
/// <remarks>
/// <b>Enter（<c>0D</c>）だと仮定してはいけない。</b> 実測 2026-09-05 の環境では
/// <c>~/.claude/keybindings.json</c> が <c>enter</c> を <c>chat:newline</c> に、
/// <c>meta+enter</c> を <c>chat:submit</c> に割り当てていたため、送信は
/// <c>1B 0D</c>（ESC + CR）だった。これは Claude Code の仕様ではなく、その環境の設定である。
/// <para>
/// 「設定項目にする」だけでは足りない。利用者が設定を変えるたびにアプリが壊れる。
/// アダプタは<b>そのエージェントの設定ファイルを読んで送信キーを導出する責務</b>を持つ。
/// </para>
/// <para>
/// 判定できないときは <see cref="Unknown"/> を返し、<b>黙って <c>0D</c> を送らない</b>。
/// 改行だけが入って送信されない、という静かな失敗を作ってはいけない。
/// </para>
/// </remarks>
public sealed record SubmitKey
{
    private SubmitKey(byte[]? bytes, string origin)
    {
        this.bytes = bytes;
        Origin = origin;
    }

    private readonly byte[]? bytes;

    /// <summary>送信に相当する端末バイト列の複製。判定できていなければ null。</summary>
    /// <remarks>複製を返す。呼び出し側が書き換えても、この <see cref="SubmitKey"/> は変わらない。</remarks>
    public byte[]? Bytes => bytes?.ToArray();

    /// <summary>どこからそう判定したか。人間に見せる。例: <c>~/.claude/keybindings.json: meta+enter</c>。</summary>
    public string Origin { get; }

    /// <summary>判定できたか。false なら部門の状態として「送信キー不明」を表示する。</summary>
    public bool IsResolved => Bytes is not null;

    /// <summary>ESC + CR。<c>meta+enter</c> 相当。</summary>
    /// <remarks>呼ぶたびに新しい配列を返す。static readonly の配列は中身を書き換えられる。</remarks>
    public static byte[] AltEnter => [0x1B, 0x0D];

    /// <summary>CR。素の <c>enter</c> 相当。</summary>
    public static byte[] Enter => [0x0D];

    /// <summary>判定できた。空のバイト列は「送信できるキー」ではないので受け付けない。</summary>
    public static SubmitKey Resolved(byte[] bytes, string origin)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        if (bytes.Length == 0)
        {
            throw new ArgumentException("空のバイト列は送信キーにならない", nameof(bytes));
        }

        return new SubmitKey(bytes.ToArray(), origin);
    }

    /// <summary>判定できなかった。<c>0D</c> にフォールバックしないこと。</summary>
    public static SubmitKey Unknown(string reason) => new(null, reason);
}

/// <summary>
/// エージェント自身の設定ファイルを読む口。設計 §13-8 訂正の 3。
/// Claude Code / Codex CLI / Antigravity CLI それぞれにある。
/// </summary>
public interface IAgentConfigReader
{
    AgentKind Kind { get; }

    /// <summary>TUI 経路の送信キーを、その環境の設定から導出する。</summary>
    SubmitKey ResolveSubmitKey();
}
