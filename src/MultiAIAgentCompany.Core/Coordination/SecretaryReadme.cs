namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 秘書の protocol の正本を書く（設計 §17-6）。
/// </summary>
/// <remarks>
/// <b>protocol を会話に埋めない。</b> 埋めると正本が会話かコード中の文字列に寄り、
/// §17-3 の「会話は正本ではない」と相性が悪くなる。
/// 起動直後に送るのは「これを読んで」だけ。
/// </remarks>
public static class SecretaryReadme
{
    /// <summary>起動直後に送る1通目。<b>protocol 本体は送らない。</b></summary>
    /// <remarks>
    /// <b>人間の1通目と、同じ turn にまとめる</b>（2026-09-09）。
    /// <para>
    /// 以前は「protocol を読んで」を1通目、人間の本文を2通目として<b>続けて書いていた。</b>
    /// <c>SendUserMessageAsync</c> は<b>行を書くだけで turn の完了を待たない</b>ので、
    /// **1通目がツールを実行している最中に2通目が割り込む**（§32-12）。
    /// </para>
    /// <para>
    /// <b>これは 400 の原因ではなかった</b>（あれは間欠的な上流エラーで、
    /// 素の <c>claude -p</c> でも出た。§32-12 に顛末を書いた）。
    /// それでもこの形にするのは、<b>二重送信の道が実際に開いているから</b>と、
    /// 別 turn にすると「1通目で読ませたはず」を<b>アプリが会話の外で仮定する</b>ことに
    /// なるからである。
    /// </para>
    /// <para>
    /// <b>protocol を会話に埋めてはいない</b>（§17-6）—— 埋めているのは
    /// <b>「正本を読め」という参照だけ</b>である。むしろ別 turn にするほうが、
    /// 「1通目で読ませたはず」をアプリが会話の外で仮定することになって壊れやすい。
    /// </para>
    /// </remarks>
    /// <param name="paths">ワークスペースの <c>.company/</c>。</param>
    /// <param name="firstMessage">
    /// 人間が最初に打った本文。null なら protocol を読ませるだけ。
    /// </param>
    public static string StartupMessage(CompanyPaths paths, string? firstMessage = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var header = $"""
            {paths.SecretaryReadme} を読んで、その protocol に従ってください。
            会話履歴は正本ではありません。仕事の提案は outbox に publish してください。
            """;

        return string.IsNullOrWhiteSpace(firstMessage)
            ? header
            : $"""
              {header}

              そのうえで、次の依頼を処理してください。

              <人間からの依頼>
              {firstMessage}
              </人間からの依頼>
              """;
    }

    /// <summary>protocol を書き出す。既にあれば上書きする（形式が変わることがあるため）。</summary>
    /// <param name="audit">
    /// 監査部門があるときだけ渡す（設計 §59）。<b>無い部門を足せと言わない</b> —— 解決できない計画になる。
    /// </param>
    public static async Task WriteAsync(
        CompanyPaths paths, IReadOnlyList<string> departmentLines, CancellationToken ct, AuditRule? audit = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(departmentLines);

        var auditSection = audit is null ? "" : $"""

            ### 計画の最後に監査を足す（設計 §59）

            作業ツリーを書き換える部門（{string.Join(" / ", audit.WriterIds.Select(id => $"`{id}`"))}）の工程を
            **1つでも含む計画には、最後の工程として必ず次を足してください。** 人間に頼まれていなくても足します。

            ```
            step: {audit.AuditId} reviews=<その計画で最後に作業ツリーを書き換えた部門ID> / commit・push の前に監査する
            ```

            書き換える工程が無い計画には足しません。
            人間に「監査して」と頼まれたら、1件の仕事として `department: {audit.AuditId}` で提案してください。
            **監査は直しません。** 問題があれば、見られた工程へ自動で送り直され、直らないまま上限に達すると人間が呼ばれます。
            """;

        Directory.CreateDirectory(paths.SecretaryRoot);
        Directory.CreateDirectory(paths.SecretaryOutbox);

        var content = $"""
            # 秘書の protocol

            あなたは**秘書**です。人間が画面の中央で会話する唯一の相手です。
            部門に仕事を割り振るのがあなたの役目ですが、**部門に直接話しかけません。**
            やり取りはすべてファイルを通します。

            ## 部門

            {string.Join("\n", departmentLines)}

            ## 仕事を提案する

            人間との会話から「これは部門にやってもらう仕事だ」と判断したら、
            **`{paths.SecretaryOutbox}` に提案を publish してください。**

            ### 書き方（この手順を守ること）

            1. `{paths.SecretaryOutbox}/<好きなID>.md.tmp.<任意>` に書く
            2. ファイルを閉じる
            3. **同じディレクトリの中で** `<好きなID>.md` へ rename する

            **途中まで書いたファイルを最終名で置かないこと。** こちらは最終名の出現を
            「書き終わった」の合図として読みます。

            ### 中身の形

            ```
            department: <上の一覧の部門 ID>

            <指示の本文。部門が読んで作業できるように書く>
            ```

            ### 複数の工程を計画として publish する（設計 §37）

            計画も同じ outbox に、上の tmp → rename の手順で `.md` を publish します。
            先頭行を `plan:` にすると計画、`department:` なら従来どおり1件の仕事です。

            ```
            plan: ログイン画面を作る
            step: research / 現状の認証まわりを調べる
            step: design / 調査結果をもとに設計する
            step: review reviews=design / 設計を外から見る
            step: implementation / 設計どおりに実装する
            ```

            `plan:` は人間の目的を言い換えずに書きます。
            各 `step:` は `<部門ID> [reviews=<部門ID>] / <次の工程への一言>` です。
            `reviews=<部門ID>` は、それより前にある同じ部門の最後の工程を指します。
            解決できない計画は自動にせず、人間に見えるところへ残します。
            前の工程の報告はそのまま渡します。秘書が要約せず、足すのは次の工程への一言だけです。

            レビュー工程の報告には `{ReviewVerdicts.Key}: {ReviewVerdicts.OkValue}` か
            `{ReviewVerdicts.Key}: {ReviewVerdicts.ReviseValue}` の行が要ります。
            **これはアプリがその工程の `instruction.md` に書くので、あなたは書かなくて構いません。**
            判定が無い・読めないときは推測で進めず、人間を呼びます。
            途中の工程が報告に `{ReportOutcomes.Key}: {ReportOutcomes.PartialValue}` か `{ReportOutcomes.Key}: {ReportOutcomes.BlockedValue}` を書くと、計画は止まり人間が呼ばれます（設計 §62-5）。
            計画を進めるのはアプリです。秘書は `{paths.PlansRoot}` や `plan.json` を直接作りません。
            {auditSection}

            ## あなたがやらないこと

            - **仕事を作らない。** `{paths.TasksRoot}` の下に新しい仕事のフォルダを作るのは
              アプリの役目です。あなたは提案か計画を publish してください
            - **`state.json` と `lease.json` を書かない。** あれはアプリだけが書きます
              （人間に頼まれても書かないこと）
            - **部門に直接話しかけない**
            - **publish したあとの面倒を見ない。** アプリが拾って仕事にし、部門へ渡します。
              あなたは publish したら次の話に移ってよいです

            ## 聞かれずにできること（設計 §38）

            次は人間に確認を取らずに通ります。**通したことは作業ログに出ます。**

            - **読む**（`Read` / `Glob` / `Grep` / `LS`）…… 作業フォルダの中
            - **書く**（`Write` / `Edit`）…… `{paths.Root}` の中
              （`state.json` と `lease.json` を除く）
            - **`git status` などの読むだけのコマンド**（`Bash`）…… 作業フォルダの中。
              `;` `|` `>` `&&` や `$(...)` を含むものは通りません ——
              **1つの命令として書いてください**

            それ以外は人間に聞きます。**聞かれたくないからといって、
            命令を繋いで1行にまとめないでください**（かえって通らなくなります）。

            ## 人間からの添付（設計 §58）

            人間の発言の末尾に `{Attachments.Heading}` があれば、その下の `- ` で始まる行は
            **人間が添付したファイル**です（`{paths.AttachmentsRoot}` の下に複製してあります）。

            - 中身が話の前提なら、**読んでから**答えてください（画像も読めるなら見てください）
            - 部門に必要なら、**提案の本文にそのパスを書いてください**。部門も同じパスで読めます
            - 添付のファイルは**書き換えない・消さない**でください

            ## 覚えておくこと

            - **会話は残りません。** アプリを閉じると消えます。
              残したいことは提案として publish してください
            - **提案は publish するとすぐ仕事になり、部門へ渡ります**（設計 §34-1）。
              **押す確認はありません** —— 出すときは、そのまま作業させてよい形で書いてください
            - **宛先（`department:`）が分からない提案だけは、自動になりません。**
              人間に見えるところへ残り、理由が出ます
            """;

        await File.WriteAllTextAsync(paths.SecretaryReadme, content, ct);
    }
}

/// <summary>秘書に「計画の最後に監査を足す」と頼むための材料（設計 §59）。</summary>
/// <param name="AuditId">監査部門の ID。</param>
/// <param name="WriterIds">作業ツリーを書き換える部門（<c>ReadsOnly</c> でない部門）。</param>
public sealed record AuditRule(string AuditId, IReadOnlyList<string> WriterIds);
