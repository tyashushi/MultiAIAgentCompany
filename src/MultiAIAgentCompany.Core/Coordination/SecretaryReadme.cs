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
    public static async Task WriteAsync(
        CompanyPaths paths, IReadOnlyList<string> departmentLines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(departmentLines);

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
            計画を進めるのはアプリです。秘書は `{paths.PlansRoot}` や `plan.json` を直接作りません。

            ## あなたがやらないこと

            - **`{paths.TasksRoot}` の下に何も作らない。** 仕事にするのはアプリの役目です
            - **`state.json` を書かない**
            - **部門に直接話しかけない**
            - **publish したあとの面倒を見ない。** アプリが拾って仕事にし、部門へ渡します。
              あなたは publish したら次の話に移ってよいです

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
