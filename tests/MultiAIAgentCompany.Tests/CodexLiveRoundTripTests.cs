using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// Codex app-server の実プロセス往復。<b>偽チャネルでは確かめられないものだけ</b>。
/// </summary>
/// <remarks>
/// 承認を出させるには <c>workspace-write</c> の書き込み可能ルート
/// <c>[workdir, /tmp, $TMPDIR]</c> の<b>外</b>へ書かせる必要がある（設計 §13-2）。
/// だから作業ディレクトリをリポジトリ配下に作り、その兄弟を書き込み先にする。
/// <c>/tmp</c> を使うと承認なしで通ってしまい、往復を測れない。
/// </remarks>
[Collection(LiveCollection.Name)]
public sealed class CodexLiveRoundTripTests
{
    [LiveCodexTheory]
    [InlineData("accept", true, "completed")]
    [InlineData("cancel", false, "interrupted")]
    [InlineData("acceptWithExecpolicyAmendment", true, "completed")]
    public async Task 実プロセスで承認の往復が閉じる(string decisionId, bool expectFile, string _)
    {
        var scratch = Path.Combine(FindRepositoryRoot(), ".live-scratch", Guid.NewGuid().ToString("N"));
        var ws = Path.Combine(scratch, "ws");
        var outside = Path.Combine(scratch, "outside");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "hello.txt");

        try
        {
            var adapter = new CodexCliAdapter();
            var session = (IStructuredSession)await adapter.StartAsync(
                new WorkspaceRef(ws), "実装", DriveMode.Structured, CancellationToken.None);

            var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource<OutcomeVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ApprovalRequested += (_, request) => approval.TrySetResult(request);
            session.TurnFinished += (_, verdict) => finished.TrySetResult(verdict);

            await using (session)
            {
                await session.SendUserMessageAsync(
                    $"Run exactly this shell command and nothing else: echo HELLO > {target}",
                    CancellationToken.None);

                var request = await WaitForApprovalAsync(approval.Task);

                Assert.Equal(AgentKind.CodexCli, request.Agent);
                Assert.Equal(ApprovalKind.Runtime, request.Kind);

                // §7: Codex は threadId / turnId を出すので、根拠を推定なしで埋められる。
                Assert.False(string.IsNullOrWhiteSpace(request.SessionId));
                Assert.False(string.IsNullOrWhiteSpace(request.TurnId));

                // §5: 決定の語彙は版で変わる。提示されたものだけを返す。
                Assert.True(request.Offers(decisionId));
                Assert.False(request.Offers("decline"));

                await session.RespondAsync(
                    request,
                    request.AvailableDecisions.Single(decision => decision.Id == decisionId),
                    decisionId == "cancel" ? "live test: cancelled on purpose" : null,
                    CancellationToken.None);

                var verdict = await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));

                // 「turn が終わった」ではなく「決定どおりに世界が変わった」を見る。
                Assert.Equal(expectFile, File.Exists(target));
                Assert.Equal(expectFile, verdict.Succeeded);

                await session.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("リポジトリルートが見つからない");
    }
    /// <summary>
    /// 承認要求を待つ。<b>来なかったことを「コードが壊れている」と読ませない。</b>
    /// </summary>
    /// <remarks>
    /// エージェントが毎回同じ行動を取るとは限らない —— 依頼どおりにコマンドを実行しようと
    /// しなければ、承認要求はそもそも発生しない。実測（2026-09-06）で、単独なら29秒で通る
    /// ケースが、連続実行の中で3分待っても来ないことがあった。
    /// <para>
    /// §13-5b の「タイムアウトを『Unity が落ちた』と解釈しない」と同じ話で、
    /// <b>混雑や気まぐれと、壊れていることは別</b>。
    /// </para>
    /// </remarks>
    private static async Task<ApprovalRequest> WaitForApprovalAsync(Task<ApprovalRequest> approval)
    {
        try
        {
            return await approval.WaitAsync(TimeSpan.FromMinutes(3));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "3分待っても承認要求が来なかった。コードの失敗とは限らない —— "
                + "エージェントが依頼どおりにコマンドを実行しようとしなければ、承認要求は発生しない。"
                + "単独で再実行して切り分けること。", exception);
        }
    }
}

/// <summary><c>MAC_LIVE_CODEX=1</c> のときだけ走る。立っていなければスキップとして出る。</summary>
internal sealed class LiveCodexTheoryAttribute : TheoryAttribute
{
    public LiveCodexTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAC_LIVE_CODEX") != "1")
        {
            Skip = "MAC_LIVE_CODEX=1 のときだけ走る（実プロセスの往復）";
        }
    }

}
