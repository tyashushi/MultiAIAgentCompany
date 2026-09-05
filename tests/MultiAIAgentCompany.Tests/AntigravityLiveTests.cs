using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.Antigravity;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// Antigravity の実プロセス。<b>ここで確かめるのは「往復が閉じた」ではない</b> ——
/// 承認の往復そのものが無いので（設計 §2 / §3）、確かめるのは
/// <b>「嘘の成功を見破れたか」</b>。
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class AntigravityLiveTests
{
    [LiveAgyFact]
    public async Task 実プロセスで嘘の成功を見破る()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mac-agy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var target = Path.Combine(scratch, "hello.txt");

        try
        {
            var adapter = new AntigravityAdapter();
            var session = (IStructuredSession)await adapter.StartAsync(
                new WorkspaceRef(scratch), "調査", DriveMode.Structured, CancellationToken.None);

            var verdicts = new List<OutcomeVerdict>();
            var gate = new SemaphoreSlim(0);
            session.TurnFinished += (_, verdict) => { verdicts.Add(verdict); gate.Release(); };

            await using (session)
            {
                // 1ターン目: ツールを要さない依頼。素直に成功するはず。
                await session.SendUserMessageAsync(
                    "Reply with exactly the word ONE and nothing else. Do not use any tools.",
                    CancellationToken.None);
                Assert.True(await gate.WaitAsync(TimeSpan.FromMinutes(3)));

                // 2ターン目: shell を要する依頼。承認できないので握りつぶされるが SUCCESS が返る。
                await session.SendUserMessageAsync(
                    $"Run exactly this shell command and nothing else: echo HELLO > {target}",
                    CancellationToken.None);
                Assert.True(await gate.WaitAsync(TimeSpan.FromMinutes(3)));

                await session.StopAsync(CancellationToken.None);
            }

            // 1プロセスで2ターン回ること（設計 §13-3 追記2）。
            Assert.Equal(2, verdicts.Count);
            Assert.True(verdicts[0].Succeeded);

            // ここが本題。status:"SUCCESS" を成功にしない。
            Assert.False(verdicts[1].Succeeded);
            Assert.False(File.Exists(target));
            Assert.Contains("RunCommand", verdicts[1].Reason);

            // init に版が無いので、版は分からないと言う（設計 §13-3 追記、§7）。
            Assert.Null(adapter.DetectedVersion);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }
        }
    }
}

/// <summary><c>MAC_LIVE_AGY=1</c> のときだけ走る。</summary>
internal sealed class LiveAgyFactAttribute : FactAttribute
{
    public LiveAgyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAC_LIVE_AGY") != "1")
        {
            Skip = "MAC_LIVE_AGY=1 のときだけ走る（実プロセス）";
        }
    }
}
