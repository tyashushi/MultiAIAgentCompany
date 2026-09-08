namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 部門の protocol の正本を書く（設計 §32-5）。
/// </summary>
/// <remarks>
/// <b>秘書の README（§17-6）と同じ姿勢。</b> protocol は会話にも argv にも埋めず、
/// ファイルに置いて「これを読んで」とだけ渡す。
/// <para>
/// <b>ここは実測から決まった文言である</b>（§32-5、2026-09-09）。
/// 初版は「ターミナルで人間に直接その質問をして、答えを待つ」と書いていて、
/// <b>Codex がそれを「シェルツールを使え」と読み</b>、誰も打ち込めないシェルの
/// <c>read</c> で止まった。agy は自前のプロンプトで、Claude Code は専用の質問 UI で聞いた ——
/// <b>同じ1文が3通りに読まれた。</b>
/// </para>
/// <para>
/// <b>だから「どうやって聞くか」を書かない。</b> 揃っているのはファイルだけである。
/// 「<c>question.md</c> を書いて turn を終える」だけを約束にすると、
/// あとは人間が CLI 自身の入力欄に答えを打てばよく、**3つとも同じ形になる。**
/// </para>
/// </remarks>
public static class DepartmentReadme
{
    /// <summary>protocol の置き場所。</summary>
    public static string PathIn(CompanyPaths paths) =>
        Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).Root, "README-department.md");

    /// <summary>
    /// 外部ターミナルで起動するときに渡す1行（設計 §32-2f）。
    /// </summary>
    /// <remarks>
    /// <b>指示書の本文を渡さない。</b> argv は <c>ps</c> に出るので、
    /// 秘密が入り得るものを流さない。渡すのは<b>在り処だけ</b>。
    /// </remarks>
    public static string LaunchPrompt(CompanyPaths paths, string slug)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return $"{PathIn(paths)} と {paths.Instruction(slug)} を読んで、その指示に従ってください。";
    }

    /// <summary>protocol を書き出す。既にあれば上書きする（形式が変わることがあるため）。</summary>
    public static async Task WriteAsync(CompanyPaths paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.Root);

        var content =
            """
            # 部門の protocol

            あなたは**部門**です。人間はいま、あなたが動いているこのターミナルの前に居ます。

            仕事の指示は `instruction.md` に書いてあります。**会話履歴は正本ではありません。**

            ## 報告のしかた

            仕事が終わったら、その仕事のフォルダ（`instruction.md` と同じ場所）に
            **`report.md`** を書いてください。**これが唯一の「終わった」の合図です。**
            画面に何を出しても、`report.md` が無ければ、仕事は終わっていないものとして扱われます。

            ## 判断が必要になったとき

            指示に書かれていないことを決める必要が出たら、**自分で決めないでください。**

            1. 質問を、同じフォルダの **`question.md`** に書く（何を聞きたいかを1〜2行で）
            2. **そこで作業を終える。** 人間があとで、この入力欄から続きを指示します
            3. 続きを指示されたら作業を再開し、`report.md` を書く

            **自分から人間に聞きに行こうとしないでください。**
            とくに、シェルを使って入力を待つ（`read` など）ことはしないでください ——
            **そこには誰も打ち込めません。** あなたは待つ必要がなく、ただ turn を終えれば、
            人間の側から声がかかります。

            `report.md` には、**何を聞き、何と答えられ、だから何を決めたか**も書いてください。
            人間が入力欄に打った答えは、この protocol の外にあるファイルには残りません。
            あなたが報告に書かなければ、**あとから誰も辿れません。**

            ## やらないこと

            - 他の部門のフォルダを書き換えない
            - `state.json` を書かない（アプリだけが書きます）
            - 書きかけのファイルを最終名で置かない（できあがってから、その名前にする）
            """;

        await File.WriteAllTextAsync(PathIn(paths), content, ct);
    }
}
