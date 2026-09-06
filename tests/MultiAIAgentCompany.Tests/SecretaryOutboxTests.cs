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
}
