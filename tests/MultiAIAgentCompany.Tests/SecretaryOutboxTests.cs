using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §17-6 —— outbox が未処理の提案の正本。</summary>
public sealed class SecretaryOutboxTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private SecretaryOutbox Outbox => new(_workspace.Paths);

    private void Publish(string id, string content)
    {
        Directory.CreateDirectory(_workspace.Paths.SecretaryOutbox);
        File.WriteAllText(Path.Combine(_workspace.Paths.SecretaryOutbox, $"{id}.md"), content);
    }

    [Fact]
    public void brief_から後ろは前提として受け取り_工程として読まない()
    {
        // 設計 §62-4。
        Publish("login", """
            plan: ログイン画面を作る
            step: design / 設計する
            brief: 受入条件
            - メールでログインできる

            step: implementation / これは工程ではない
            """);

        var plan = Assert.Single(Outbox.ReadPlans());
        Assert.Equal(["design"], plan.Steps.Select(step => step.DepartmentId));
        Assert.Equal("受入条件\n\n- メールでログインできる\n\nstep: implementation / これは工程ではない", plan.Brief);
    }

    [Theory]
    [InlineData("plan: 目的\nstep: design / やる")]
    [InlineData("plan: 目的\nstep: design / やる\nbrief:\n\n")]
    public void brief_が無い_空なら_前提は無い(string content)
    {
        Publish("login", content);
        Assert.Null(Assert.Single(Outbox.ReadPlans()).Brief);
    }

    [Fact]
    public void 計画の目的と工程とレビュー先を読む()
    {
        Publish("login", """
            plan: ログイン画面を作る
            step: research / 現状の認証まわりを調べる
            step: design / 調査結果をもとに設計する
            step: review reviews=design / 設計を外から見る
            step: implementation / 設計どおりに実装する
            """);

        var plan = Assert.Single(Outbox.ReadPlans());

        Assert.Equal("login", plan.Id);
        Assert.Equal("ログイン画面を作る", plan.Goal);
        Assert.Equal(new PlanStep[]
        {
            new("research", "現状の認証まわりを調べる"),
            new("design", "調査結果をもとに設計する"),
            new("review", "設計を外から見る", 1),
            new("implementation", "設計どおりに実装する"),
        }, plan.Steps);
        Assert.Empty(Outbox.Read());
    }

    [Fact]
    public void レビュー先は前にある同じ部門の最後の工程になる()
    {
        Publish("p1", "plan: 目的\nstep: design / 最初\nstep: design / 次\nstep: review reviews=design / 確認\n");

        Assert.Equal(1, Assert.Single(Outbox.ReadPlans()).Steps[2].ReviewsStep);
    }

    [Theory]
    [InlineData("step: design / 設計\nstep: implementation / 実装\nstep: review reviews=design / 確認", "工程 3（review）は工程 1（design）を見るが、間に工程 2（implementation）がある")]
    [InlineData("step: design / 設計\nstep: review reviews=design / 確認\nstep: implementation / 実装\nstep: audit reviews=design / 監査", "工程 4（audit）は工程 1（design）を見るが、間に工程 3（implementation）がある")]
    public void レビューが見る相手のすぐ後に無い計画は_理由を添えて人間へ残す(string steps, string reason)
    {
        // 設計 §62-13（人間の決定）。
        Publish("p1", $"plan: 目的\n{steps}");

        Assert.Empty(Outbox.ReadPlans());
        var proposal = Assert.Single(Outbox.Read());
        Assert.Null(proposal.DepartmentId);
        Assert.StartsWith(reason, proposal.PlanProblem);
        Assert.Equal($"plan: 目的\n{steps}", proposal.Body);
    }

    [Fact]
    public void 同じ工程を見るレビューは続けて置ける()
    {
        Publish("p1", "plan: 目的\nstep: design / 設計\nstep: review reviews=design / 確認\nstep: audit reviews=design / 監査\nstep: implementation / 実装\n");

        var plan = Assert.Single(Outbox.ReadPlans());
        Assert.Equal([null, 0, 0, null], plan.Steps.Select(step => step.ReviewsStep));
    }

    [Theory]
    [InlineData("step: review reviews=missing / 確認")]
    [InlineData("step: review reviews=review / 自分自身")]
    [InlineData("step: review reviews=design / 確認\nstep: design / 後続")]
    public void レビュー先を解決できなければ本文を人間へ残す(string steps)
    {
        var content = $"plan: 目的\n{steps}";
        Publish("p1", content);

        Assert.Empty(Outbox.ReadPlans());
        var proposal = Assert.Single(Outbox.Read());
        Assert.Null(proposal.DepartmentId);
        Assert.Equal(content, proposal.Body);
        Assert.True(File.Exists(Path.Combine(_workspace.Paths.SecretaryOutbox, "p1.md")));
    }

    [Fact]
    public void 計画と従来の提案は先頭行で分け部門の実在は調べない()
    {
        Publish("p1", "plan: 目的\nstep: unknown / 一言");
        Publish("p2", "department: implementation\n\nplan: これは指示の本文");

        Assert.Equal("unknown", Assert.Single(Assert.Single(Outbox.ReadPlans()).Steps).DepartmentId);
        var proposal = Assert.Single(Outbox.Read());
        Assert.Equal("p2", proposal.Id);
        Assert.Equal("implementation", proposal.DepartmentId);
        Assert.Equal("plan: これは指示の本文", proposal.Body);
    }

    [Fact]
    public void 空行があっても計画として読む()
    {
        // **秘書は人間が読む文書を書く。** ここで厳しくすると、
        // 正しい計画が「宛先不明の提案」に化けて自動にならない。
        Publish("p1", "plan: 目的\n\nstep: research / 調べる\n\n  step: design / 設計する\n");

        var plan = Assert.Single(Outbox.ReadPlans());
        Assert.Equal(2, plan.Steps.Count);
        Assert.Empty(Outbox.Read());
    }

    [Fact]
    public void 知らない行があれば計画にせず人間へ返す()
    {
        Publish("p1", "plan: 目的\nstep: research / 調べる\nよろしくお願いします");

        Assert.Empty(Outbox.ReadPlans());
        Assert.Single(Outbox.Read());
    }

    [Fact]
    public void 工程が1つも無い計画は計画ではない()
    {
        Publish("p1", "plan: 目的\n");

        Assert.Empty(Outbox.ReadPlans());
        Assert.Single(Outbox.Read());
    }

    [Fact]
    public void 計画もpublish途中のファイルは読まない()
    {
        Assert.Empty(Outbox.ReadPlans());
        Publish("p1.md.tmp.abc", "plan: 目的\nstep: design / 一言");

        Assert.Empty(Outbox.ReadPlans());
        Assert.Empty(Outbox.Read());
    }

    [Fact]
    public async Task protocolに計画と共通のレビュー判定を書く()
    {
        await SecretaryReadme.WriteAsync(_workspace.Paths, [], CancellationToken.None);
        var content = await File.ReadAllTextAsync(_workspace.Paths.SecretaryReadme);

        Assert.Contains("plan: ログイン画面を作る", content);
        Assert.Contains("step: review reviews=design /", content);
        Assert.Contains($"{ReviewVerdicts.Key}: {ReviewVerdicts.OkValue}", content);
        Assert.Contains($"{ReviewVerdicts.Key}: {ReviewVerdicts.ReviseValue}", content);

        // **判定行を書くのは秘書ではない**（§37-5）—— 頼む先は部門の instruction.md。
        Assert.Contains("あなたは書かなくて構いません", content);
    }

    [Fact]
    public void 宛先と本文に分ける()
    {
        Publish("p1", "department: implementation\n\nREADME を読んで報告して");

        var proposal = Assert.Single(Outbox.Read());

        Assert.Equal("implementation", proposal.DepartmentId);
        Assert.Equal("README を読んで報告して", proposal.Body);
    }

    [Fact]
    public void 宛先が無くても本文は残す()
    {
        // 人間が読んで判断できるようにする（§17-6）。
        Publish("p1", "宛先を書き忘れた提案");

        var proposal = Assert.Single(Outbox.Read());

        Assert.Null(proposal.DepartmentId);
        Assert.Equal("宛先を書き忘れた提案", proposal.Body);
    }

    [Fact]
    public void publish途中のファイルは読まない()
    {
        // 最終名の出現が「書き終わった」の合図（§16-1 の publish 契約）。
        Directory.CreateDirectory(_workspace.Paths.SecretaryOutbox);
        File.WriteAllText(Path.Combine(_workspace.Paths.SecretaryOutbox, "p1.md.tmp.abc.md"), "department: x\n\n途中");

        Assert.Empty(Outbox.Read());
    }

    [Fact]
    public void 受理しても却下しても消さない()
    {
        // 消すと「提案があったが人間が仕事にしなかった」が失われる（§16-4 / §17-6）。
        Publish("p1", "department: implementation\n\nやること");
        Publish("p2", "department: implementation\n\n別のこと");

        Outbox.Accept("p1", "task-1", DateTimeOffset.UnixEpoch);
        Outbox.Reject("p2", DateTimeOffset.UnixEpoch);

        Assert.Empty(Outbox.Read());
        Assert.Single(Directory.EnumerateFiles(_workspace.Paths.SecretaryAccepted));
        Assert.Single(Directory.EnumerateFiles(_workspace.Paths.SecretaryRejected));
    }

    [Fact]
    public void 受理したファイル名に仕事の名前が残る()
    {
        Publish("p1", "department: implementation\n\nやること");

        Outbox.Accept("p1", "task-20260906-1", DateTimeOffset.UnixEpoch);

        Assert.Contains(Directory.EnumerateFiles(_workspace.Paths.SecretaryAccepted),
            f => f.Contains("task-20260906-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 監査部門があるときだけ計画の最後に監査を足せと書き例の行は計画として読める()
    {
        // **無い部門を足せと言わない**（設計 §59）—— 解決できない計画になる。
        await SecretaryReadme.WriteAsync(_workspace.Paths, [], CancellationToken.None);
        Assert.DoesNotContain("監査", await File.ReadAllTextAsync(_workspace.Paths.SecretaryReadme));

        await SecretaryReadme.WriteAsync(_workspace.Paths, [], CancellationToken.None,
            new AuditRule("audit", ["design", "implementation"]));
        var readme = await File.ReadAllTextAsync(_workspace.Paths.SecretaryReadme);
        Assert.Contains("`design` / `implementation`", readme);
        Assert.Contains("department: audit", readme);

        // README の例の行を、そのまま計画の最後に置いて読めること。
        var example = readme.Split('\n').Single(line => line.StartsWith("step: audit ", StringComparison.Ordinal));
        Publish("with-audit", $"""
            plan: 機能を足す
            step: implementation / 実装する
            {example.Replace("<その計画で最後に作業ツリーを書き換えた部門ID>", "implementation", StringComparison.Ordinal)}
            """);
        var plan = Assert.Single(Outbox.ReadPlans());
        Assert.Equal(new PlanStep("audit", "commit・push の前に監査する", 0), plan.Steps[1]);
    }
}
