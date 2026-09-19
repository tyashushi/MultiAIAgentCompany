namespace MultiAIAgentCompany.Core.Agents;

/// <summary>
/// 秘書の <c>Bash</c> を、<b>読むだけの命令と、<c>.company/</c> の中だけで書く命令に限って</b>自動で通す（設計 §38 / §62-15）。
/// </summary>
/// <remarks>
/// <b>§35 は「Bash は通さない。ここを広げない」と書いていた。</b>
/// 人間がその決めを改めた（2026-09-12）—— ただし<b>広げ方を「命令の形」で縛る</b>ので、
/// 「コマンドは何でもできる」という §35 の心配そのものは残していない。
/// <para>
/// <b>狭く始める。</b> 許すのは名前で挙げた命令だけで、要るものが出たら足す ——
/// 逆向き（広く許して危ないものを除く）にすると、**除き忘れが通る側に倒れる。**
/// </para>
/// <para>
/// <b>長く走る命令は止められない</b>（<c>tail -f</c> など）。turn が返ってこないことは
/// §36 が帯に出すので、ここでは見ない —— <b>同じことを2箇所で見張らない。</b>
/// </para>
/// </remarks>
public static class SecretaryBashPolicy
{
    /// <summary>
    /// 名前で許す命令。<b>どれも、それ自体では何も書かない。</b>
    /// </summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "wc", "file", "stat", "pwd", "grep", "rg", "diff", "tree", "git",
        "find", "echo", "basename", "dirname",
    };

    /// <summary>
    /// <c>.company/</c> の中だけで書く命令（設計 §62-15、人間が広げた）。
    /// </summary>
    /// <remarks>
    /// <b>Write / Edit と同じ線を引く</b>（§38）: 場所は <c>.company/</c> の中、
    /// <c>state.json</c> と <c>lease.json</c> は除く。秘書は提案と計画を tmp → rename で publish するので、
    /// <c>mv</c> を聞くと<b>出すたびに必ず人間の承認で止まっていた</b>（実機で発覚）。
    /// </remarks>
    private static readonly Dictionary<string, (int MinOperands, int MaxOperands)> CompanyWriteCommands =
        new(StringComparer.Ordinal)
        {
            ["mv"] = (2, 2),
            ["cp"] = (2, 2),
            ["mkdir"] = (1, int.MaxValue),
            ["touch"] = (1, int.MaxValue),
        };

    /// <summary>
    /// <c>git</c> の中で読むだけの副命令。<b>ここに無いものは通さない</b> ——
    /// <c>git</c> は <c>commit</c> も <c>checkout</c> も <c>config</c> も同じ名前で入ってくる。
    /// </summary>
    private static readonly HashSet<string> ReadOnlyGitSubcommands = new(StringComparer.Ordinal)
    {
        "status", "diff", "log", "show", "branch", "remote", "ls-files", "rev-parse", "describe", "blame", "tag",
    };

    /// <summary>
    /// <b>引数を付けると書き換わる副命令</b>（レビューで発覚、2026-09-12）。
    /// </summary>
    /// <remarks>
    /// <c>git branch</c> は一覧だが <c>git branch -D x</c> は消す。
    /// <c>git tag</c> は一覧だが <c>git tag v1</c> は作る。
    /// <c>git remote</c> は一覧だが <c>git remote remove origin</c> は消す。
    /// <b>副命令の名前だけ見ると、同じ顔で通る。</b>
    /// → <b>裸のときだけ通す</b>（旗以外の語を後ろに付けない）。
    /// </remarks>
    private static readonly HashSet<string> BareOnlyGitSubcommands = new(StringComparer.Ordinal)
    {
        "branch", "tag", "remote",
    };

    /// <summary>
    /// 命令ごとに許す旗。<b>ここに無い旗は聞く。</b>
    /// </summary>
    /// <remarks>
    /// <b>旗を読み飛ばしてはいけない</b>（自分の検査で見つけた、2026-09-12）。
    /// <c>git --exec-path=/tmp status</c> は**補助プログラムの場所を差し替え**、
    /// <c>git log --ext-diff</c> は**設定に書かれた外部 diff を起こす** ——
    /// どちらも「読むだけの命令」の顔をして**別のプログラムを走らせる。**
    /// <para>
    /// <b>命令ごとに分ける。</b> <c>-c</c> は <c>wc -c</c> では無害だが
    /// <c>git -c core.pager=sh</c> では外部を起こす —— **同じ綴りでも意味が違う。**
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string[]> AllowedFlags = new(StringComparer.Ordinal)
    {
        ["ls"] = ["-l", "-a", "-la", "-al", "-lh", "-h", "-R", "-t", "-r", "-1", "-A"],
        ["cat"] = ["-n"],
        ["head"] = ["-n"],
        ["tail"] = ["-n"],
        ["wc"] = ["-l", "-w", "-c", "-m"],
        ["file"] = [],
        ["stat"] = [],
        ["pwd"] = [],
        ["echo"] = ["-n"],
        ["basename"] = [],
        ["dirname"] = [],

        // **`-exec` / `-delete` / `-fprint` は名前で許していないので聞く**（読むだけの顔で書く・走らせる）。
        ["find"] = ["-name", "-iname", "-type", "-maxdepth", "-mindepth", "-path", "-ipath"],
        ["mv"] = [],
        ["cp"] = [],
        ["mkdir"] = ["-p"],
        ["touch"] = [],
        ["tree"] = ["-L", "-a", "-d", "-F"],
        ["diff"] = ["-u", "-r", "-w", "-b", "-q", "-N"],
        ["grep"] = ["-n", "-i", "-r", "-R", "-l", "-c", "-v", "-w", "-E", "-F", "-A", "-B", "-C", "--include", "--exclude"],
        ["rg"] = ["-n", "-i", "-l", "-c", "-w", "-F", "-A", "-B", "-C", "--type", "-t", "--glob", "-g", "--hidden", "--no-ignore"],
        ["git"] = [
            "-C", "--stat", "--numstat", "--shortstat", "--name-only", "--name-status",
            "--oneline", "--graph", "--decorate", "--abbrev-commit", "--cached", "--staged",
            "-n", "--max-count", "-s", "--short", "--porcelain", "-v", "--all", "--no-color",
            "--pretty", "--format", "--date", "--follow", "-p", "--patch", "--",
        ],
    };

    /// <summary>値を次の語で取る旗。<b>次の語を副命令と間違えない。</b></summary>
    private static readonly HashSet<string> FlagsTakingValue = new(StringComparer.Ordinal)
    {
        "-C", "-n", "-A", "-B", "-t", "-g", "-L",
        "-name", "-iname", "-type", "-maxdepth", "-mindepth", "-path", "-ipath",
    };

    /// <summary>
    /// 命令を繋いだり、出力を書き出したりする字。<b>1つでもあれば通さない。</b>
    /// </summary>
    /// <remarks>
    /// <b>中身を解釈しない。</b> <c>;</c> や <c>|</c> や <c>&gt;</c> を許すと、
    /// 読むだけの命令の後ろに何でも書ける ——
    /// **許した名前の意味が無くなる。**
    /// <para>
    /// <b>バックスラッシュも含める</b>（レビュー2周目で発覚）。
    /// <c>cat ..\/secret.md</c> を bash は <c>../secret.md</c> として読むのに、
    /// こちらは「<c>..\</c> という名前のフォルダ」と見て**中だと判定する** ——
    /// <b>解釈が食い違うものは、通さない側に倒す。</b>
    /// </para>
    /// </remarks>
    private static readonly char[] Chaining =
        [';', '|', '&', '>', '<', '`', '\n', '\r', '(', ')', '{', '}', '\\'];

    /// <param name="command">実行しようとしている命令そのもの。</param>
    /// <param name="workspaceRoot">作業フォルダ。<b>ここから出る読み取りは通さない。</b></param>
    public static ApprovalVerdict Decide(string? command, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        if (command is not { Length: > 0 } raw || raw.Trim().Length is 0)
        {
            return new ApprovalVerdict(false, "実行しようとしている命令が分からないので聞く");
        }

        if (raw.IndexOfAny(Chaining) >= 0 || raw.Contains("$", StringComparison.Ordinal))
        {
            return new ApprovalVerdict(false, "命令を繋ぐ・書き出す・展開する字が入っているので聞く");
        }

        if (Tokenize(raw) is not { Count: > 0 } tokens)
        {
            return new ApprovalVerdict(false, "命令を読み取れないので聞く");
        }

        var name = tokens[0];
        var writes = CompanyWriteCommands.TryGetValue(name, out var operandCount);
        if (!ReadOnlyCommands.Contains(name) && !writes)
        {
            return new ApprovalVerdict(false, $"{name} は自動で通さない");
        }

        // **旗も名前で許す。** 読み飛ばすと、読むだけの命令の顔をしたまま
        // **別のプログラムを走らせる旗**が通る（`git --exec-path` / `git log --ext-diff`）。
        var allowed = AllowedFlags[name];
        foreach (var token in tokens.Skip(1).Where(t => t.StartsWith('-')))
        {
            // `--date=short` のように `=` で値を付ける形は、名前の側だけ見る。
            var flag = token.Split('=', 2)[0];
            if (!allowed.Contains(flag, StringComparer.Ordinal)
                && !(name is "git" && IsCountFlag(flag)))
            {
                return new ApprovalVerdict(false, $"{name} の {flag} は自動で通さない");
            }
        }

        if (name is "git")
        {
            var sub = FirstSubcommand(tokens);
            if (sub is null || !ReadOnlyGitSubcommands.Contains(sub))
            {
                return new ApprovalVerdict(false, $"git {sub ?? "（副命令なし）"} は自動で通さない");
            }

            // **一覧にしかならない形だけ通す**（レビューで発覚）。
            if (BareOnlyGitSubcommands.Contains(sub) && HasArgumentAfter(tokens, sub))
            {
                return new ApprovalVerdict(false, $"git {sub} に引数が付いているので聞く（書き換わる形がある）");
            }
        }

        if (writes)
        {
            return DecideCompanyWrite(name, tokens, operandCount, workspaceRoot);
        }

        var root = Full(workspaceRoot);
        foreach (var token in tokens.Skip(1))
        {
            if (!LooksLikePath(token))
            {
                continue;
            }

            // **`~` は展開しない。** シェルはホームに展開するのに、こちらが
            // 「作業フォルダの中の `~` という名前」として解決すると、
            // **`~/.ssh` を「中」と判定して通してしまう**（テストで見つけた）。
            if (token.StartsWith('~'))
            {
                return new ApprovalVerdict(false, $"{token} は展開先が分からないので聞く");
            }

            // `Path.Combine` は根付きの語（`/x`、`C:/x`、`D:x`）を渡されると、そちらだけを返す。
            var full = Full(Path.Combine(workspaceRoot, token));
            if (full is null || !IsInside(full, root))
            {
                return new ApprovalVerdict(false, $"{token} は作業フォルダの外なので聞く");
            }
        }

        return new ApprovalVerdict(true, $"{name}（作業フォルダの中を読むだけ）");
    }

    /// <summary>
    /// <c>.company/</c> の中だけで書く命令を確かめる（設計 §62-15）。
    /// </summary>
    /// <remarks>
    /// <b>旗でない語は全部、場所として見る</b>（読む命令と違い、<c>/</c> を持たない名前も
    /// 作業フォルダの直下を書き換える）。フォルダごとの <c>mv</c> / <c>cp</c> は聞く ——
    /// 中の <c>state.json</c> ごと動かせてしまう。
    /// </remarks>
    private static ApprovalVerdict DecideCompanyWrite(
        string name, IReadOnlyList<string> tokens, (int Min, int Max) operandCount, string workspaceRoot)
    {
        var operands = tokens.Skip(1).Where(token => !token.StartsWith('-')).ToArray();
        if (operands.Length < operandCount.Min || operands.Length > operandCount.Max)
        {
            return new ApprovalVerdict(false, $"{name} の引数の数がいつもの形ではないので聞く");
        }

        var company = Full(Path.Combine(workspaceRoot, ".company"));
        for (var index = 0; index < operands.Length; index++)
        {
            var operand = operands[index];
            if (operand.StartsWith('~'))
            {
                return new ApprovalVerdict(false, $"{operand} は展開先が分からないので聞く");
            }

            var full = Full(Path.Combine(workspaceRoot, operand));
            if (full is null || company is null
                || !full.StartsWith(company + Path.DirectorySeparatorChar, SecretaryApprovalPolicy.PathComparison))
            {
                return new ApprovalVerdict(false, $"{operand} は .company/ の外なので聞く");
            }

            var fileName = Path.GetFileName(full);
            if (string.Equals(fileName, "state.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "lease.json", StringComparison.OrdinalIgnoreCase))
            {
                return new ApprovalVerdict(false, $"{fileName} を書くのはアプリだけ（§14-1 / §14-2）");
            }

            if (name is "mv" or "cp" && index is 0 && Directory.Exists(full))
            {
                return new ApprovalVerdict(false, $"{operand} はフォルダなので聞く（中の state.json ごと動く）");
            }
        }

        return new ApprovalVerdict(true, $"{name}（.company/ の中に書く）");
    }

    /// <summary>
    /// 空白で切る。<b>引用符は外すが、中身は解釈しない</b>（展開の字は上で弾いてある）。
    /// </summary>
    private static IReadOnlyList<string>? Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;

        foreach (var c in command)
        {
            if (quote is { } open)
            {
                if (c == open) quote = null;
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        // 閉じていない引用符は、読み取れていない。
        if (quote is not null) return null;
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>最初の「旗ではない」語。<c>git -C x status</c> の <c>status</c> を拾う。</summary>
    private static string? FirstSubcommand(IReadOnlyList<string> tokens)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith('-'))
            {
                // 値を次の語で取る旗は、**次の語を副命令と間違えない**
                // （`=` で付けている形は語が分かれないので、ここは通らない）。
                if (FlagsTakingValue.Contains(token)) i++;
                continue;
            }

            return token;
        }

        return null;
    }

    /// <summary>その副命令の後ろに、旗ではない語があるか。</summary>
    private static bool HasArgumentAfter(IReadOnlyList<string> tokens, string subcommand)
    {
        var index = tokens.ToList().IndexOf(subcommand);
        for (var i = index + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith('-'))
            {
                if (FlagsTakingValue.Contains(token)) i++;
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary><c>git log -20</c> のような件数の旗。</summary>
    private static bool IsCountFlag(string flag) =>
        flag.Length > 1 && flag[0] is '-' && flag[1..].All(char.IsAsciiDigit);

    /// <summary>
    /// その語が場所を指しているか。
    /// </summary>
    /// <remarks>
    /// <b>旗と、ただの文字列（検索語）は見ない。</b> 作業フォルダから出るには
    /// <c>/</c> か <c>~</c> か <c>..</c> が要るので、それを持つ語だけ確かめれば足りる。
    /// <para>
    /// <b>Windows では <c>:</c> も場所の印</b>（2026-09-14）。<c>cat D:secret.txt</c> は
    /// <c>/</c> を持たないのに、別のドライブを読む。
    /// </para>
    /// </remarks>
    private static bool LooksLikePath(string token) =>
        !token.StartsWith('-')
        && (token.Contains('/', StringComparison.Ordinal)
            || token.StartsWith('~')
            || token is "." or ".."
            || (OperatingSystem.IsWindows() && token.Contains(':', StringComparison.Ordinal)));

    private static string? Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsInside(string full, string? root) =>
        root is not null
        && (full.Equals(root, SecretaryApprovalPolicy.PathComparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, SecretaryApprovalPolicy.PathComparison));
}
