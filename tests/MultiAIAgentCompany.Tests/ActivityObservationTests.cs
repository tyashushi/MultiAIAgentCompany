using MultiAIAgentCompany.Core.Activity;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Terminal;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>試作で測った文字列と遷移を固定する（設計 §60 / §61）。</summary>
public sealed class ActivityObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maac-activity-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private static readonly string[] Events = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop"];
    private const string Surface = "I0917 tool_confirmation_manager.go:123] Surfacing tool confirmation: \"shell\" at step 3";
    private const string Response = "I0917 input_loop.go:456] Responding to tool confirmation: convID=abcd, stepIdx=3, approved=false";
    private const string Complete = "I0917 conversation_manager.go:789] Stream completed for abcdef-1234, clearing ResponsePending";
    private ActivityLaunch Launch => ActivityLaunch.Create(_root, "/workspace", "implementation", _clock.GetUtcNow());

    private static string CommandJson(string cli, string eventName) =>
        """
        "sh -c 'cat >/dev/null; printf \"%s <cli> <event>\\n\" \"$(date +%s)\" >> \"$MAAC_ACTIVITY_EVENTS\"'"
        """
            .Replace("<cli>", cli).Replace("<event>", eventName);

    [Theory]
    [InlineData(AgentKind.ClaudeCode, "claude")]
    [InlineData(AgentKind.CodexCli, "codex")]
    [InlineData(AgentKind.AntigravityCli, "agy")]
    public void フックコマンドは固定の一行(AgentKind kind, string cli)
    {
        Assert.Equal("sh -c 'cat >/dev/null; printf \"%s " + cli + " Stop\\n\" \"$(date +%s)\" >> \"$MAAC_ACTIVITY_EVENTS\"'",
            ActivityHooks.Command(AgentExecutable.NameOf(kind), "Stop"));
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.CodexCli)]
    [InlineData(AgentKind.AntigravityCli)]
    public void 起動引数が全て一致し指示は最後(AgentKind kind)
    {
        var launch = Launch;
        var expected = AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high", AgentPermissionMode.Auto).ToList();
        var hooks = new List<string>();
        if (kind == AgentKind.ClaudeCode)
        {
            hooks.AddRange(["--settings", "{\"hooks\":{" + string.Join(",", Events.Select(e =>
                "\"" + e + "\":[{\"matcher\":\"\",\"hooks\":[{\"type\":\"command\",\"command\":" + CommandJson("claude", e) + "}]}]")) + "}}"]);
        }
        else if (kind == AgentKind.CodexCli)
        {
            foreach (var e in Events)
                hooks.AddRange(["-c", "hooks." + e + "=[{hooks=[{type=\"command\",command=" + CommandJson("codex", e) + "}]}]"]);
        }
        else hooks.AddRange(["--add-dir", Path.Combine(_root, "agy-hooks"), "--log-file", Path.Combine(launch.DirectoryPath, "agy.log")]);
        expected.InsertRange(expected.Count - (kind == AgentKind.AntigravityCli ? 2 : 1), hooks);
        var actual = AgentExecutable.InteractiveArguments(kind, "読んで", "model", "high", AgentPermissionMode.Auto, launch);
        Assert.Equal(expected, actual);
        Assert.Equal("読んで", actual[^1]);
        Assert.DoesNotContain("--dangerously-bypass-hook-trust", actual);
    }

    [Fact]
    public void Windows相当の構成には観測引数も環境も無い()
    {
        using var workspace = new TemporaryWorkspace();
        var dispatcher = new TaskDispatcher(workspace.Paths, new TaskStore(workspace.Paths, _clock), new LeaseStore(workspace.Paths, _clock), _clock);
        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            var department = new DepartmentDefinition("implementation", "実装", "実装する", kind, DriveMode.ExternalTerminal);
            var request = dispatcher.TerminalRequestFor(department, "task");
            Assert.Null(request.Activity);
            Assert.Null(request.EnvironmentVariables);
            Assert.Equal(AgentExecutable.InteractiveArguments(kind, DepartmentReadme.LaunchPrompt(workspace.Paths, "task")), request.Arguments);
        }
    }

    [Fact]
    public void macOSの構成は起動要求へ観測の引数と環境を渡す()
    {
        using var workspace = new TemporaryWorkspace();
        var dispatcher = new TaskDispatcher(workspace.Paths, new TaskStore(workspace.Paths, _clock), new LeaseStore(workspace.Paths, _clock), _clock, _root);
        var request = dispatcher.TerminalRequestFor(new DepartmentDefinition("implementation", "実装", "実装する", AgentKind.CodexCli, DriveMode.ExternalTerminal), "task");
        Assert.NotNull(request.Activity);
        Assert.Equal("implementation", request.EnvironmentVariables!["MAAC_DEPARTMENT"]);
        Assert.Equal(request.Activity.EventsPath, request.EnvironmentVariables["MAAC_ACTIVITY_EVENTS"]);
        Assert.Equal(12 + 1, request.Arguments.Count);
        Assert.False(Directory.Exists(request.Activity.DirectoryPath));
    }

    [Fact]
    public void agy定義は指定の形で同じ内容なら書き直さない()
    {
        var launch = Launch;
        launch.Prepare(AgentKind.AntigravityCli);
        var expected = "{\"maac-activity\":{" + string.Join(",", new[] { "PreInvocation", "PostInvocation", "PostToolUse", "Stop" }.Select(e =>
            "\"" + e + "\":" + (e == "PostToolUse" ? "[{\"matcher\":\"\",\"hooks\":" : "")
            + "[{\"type\":\"command\",\"command\":" + CommandJson("agy", e) + "}]" + (e == "PostToolUse" ? "}]" : ""))) + "}}";
        var path = Path.Combine(launch.AgyHooksDirectory, ".agents", "hooks.json");
        Assert.Equal(expected, File.ReadAllText(path));
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch);
        launch.Prepare(AgentKind.AntigravityCli);
        Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void 環境変数は引用してexecより前に出す()
    {
        var script = MacTerminalScript.Build(new TerminalLaunchRequest("title", "/workspace", "/cli", ["prompt"],
            EnvironmentVariables: new Dictionary<string, string> { ["MAAC_DEPARTMENT"] = "a'b", ["MAAC_ACTIVITY_EVENTS"] = "/a b/events" }), "/pid");
        Assert.Contains("export MAAC_DEPARTMENT='a'\\''b'\nexport MAAC_ACTIVITY_EVENTS='/a b/events'\nexec '/cli' 'prompt'", script);
    }

    private (ActivityReader Reader, List<Observed<ActivityState>> Seen) Reader(AgentKind kind, bool verified = false, ActivityLaunch? launch = null, string? version = "v1")
    {
        if (verified) new AgyLogVersions(_root).Validate(version);
        var reader = new ActivityReader(launch ?? Launch, kind, _clock, version);
        var seen = new List<Observed<ActivityState>>();
        reader.Observed += (_, observation) => seen.Add(observation);
        return (reader, seen);
    }

    private static void Hook(ActivityReader reader, string cli, string e) => reader.ApplyHook($"1700000000 {cli} {e}");

    [Theory]
    [InlineData(AgentKind.ClaudeCode, "claude")]
    [InlineData(AgentKind.CodexCli, "codex")]
    public void ClaudeとCodexの実測の並び(AgentKind kind, string cli)
    {
        var (reader, seen) = Reader(kind);
        foreach (var e in Events) Hook(reader, cli, e);
        Assert.Equal(new[] { ActivityState.Resting, ActivityState.Working, ActivityState.Working,
            ActivityState.AwaitingApproval, ActivityState.Working, ActivityState.Resting }, seen.Select(s => s.Value));
        Assert.All(seen, s => Assert.Equal(EvidenceSource.LifecycleHook, s.Evidence.Source));
        Assert.Equal((kind == AgentKind.ClaudeCode ? "Claude Code" : "Codex") + " フック: Stop", seen[^1].Evidence.RedactedSummary);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode, "claude")]
    [InlineData(AgentKind.CodexCli, "codex")]
    public void 承認待ちは指定のイベントだけで解く(AgentKind kind, string cli)
    {
        foreach (var clearing in new[] { "PostToolUse", "UserPromptSubmit", "Stop" })
        {
            var (reader, seen) = Reader(kind);
            Hook(reader, cli, "PermissionRequest");
            Hook(reader, cli, "PreToolUse");
            Hook(reader, cli, "SessionStart");
            Assert.All(seen, s => Assert.Equal(ActivityState.AwaitingApproval, s.Value));
            Hook(reader, cli, clearing);
            Assert.Equal(clearing == "Stop" ? ActivityState.Resting : ActivityState.Working, seen[^1].Value);
        }
    }

    [Fact]
    public void agy検証済みの実測の並び()
    {
        var (reader, seen) = Reader(AgentKind.AntigravityCli, verified: true);
        Hook(reader, "agy", "PreInvocation");
        reader.ApplyLog(Surface);
        reader.ApplyLog(Response.Replace("false", "true"));
        foreach (var e in new[] { "PostToolUse", "PostInvocation", "Stop" }) Hook(reader, "agy", e);
        Assert.Equal(new[] { ActivityState.Working, ActivityState.AwaitingApproval, ActivityState.Working,
            ActivityState.Working, ActivityState.Working, ActivityState.Resting }, seen.Select(s => s.Value));
    }

    [Fact]
    public void agyの拒否はStop無しでも入力待ちになり続けて作業すれば作業中に戻る()
    {
        // 拒否したあとはフックが来なかった（§60-5b）。`Stream completed` は終了時にしか出ない（§61-9）。
        var (reader, seen) = Reader(AgentKind.AntigravityCli, verified: true);
        Hook(reader, "agy", "PreInvocation");
        reader.ApplyLog(Surface);
        reader.ApplyLog(Response);
        Assert.Equal(new[] { ActivityState.Working, ActivityState.AwaitingApproval, ActivityState.Resting }, seen.Select(s => s.Value));
        Assert.Equal("agy ログ: 承認を拒否した", seen[^1].Evidence.RedactedSummary);
        Hook(reader, "agy", "PreInvocation");
        Assert.Equal(ActivityState.Working, seen[^1].Value);
    }

    [Fact]
    public void agy未検証でも承認画面は直接の根拠になる()
    {
        var (reader, seen) = Reader(AgentKind.AntigravityCli);
        Hook(reader, "agy", "PreInvocation");
        Assert.Equal(ActivityState.WorkingOrAwaitingApproval, seen[^1].Value);
        reader.ApplyLog(Surface);
        Hook(reader, "agy", "Stop");
        Assert.Equal(ActivityState.AwaitingApproval, seen[^1].Value);
        Assert.Equal("agy ログ: 承認画面を出した", seen[^1].Evidence.RedactedSummary);
    }

    [Fact]
    public void 承認画面の行でその版だけ検証し終了の行や版不明では検証しない()
    {
        // `Stream completed` は agy の終了時にしか出ない（実機で判明、§61-9）ので検証に使わない。
        var (closing, closingSeen) = Reader(AgentKind.AntigravityCli);
        closing.ApplyLog(Complete);
        Hook(closing, "agy", "PreInvocation");
        Assert.Equal(ActivityState.WorkingOrAwaitingApproval, closingSeen[^1].Value);
        Assert.False(new AgyLogVersions(_root).Contains("v1"));

        var (reader, seen) = Reader(AgentKind.AntigravityCli);
        reader.ApplyLog(Surface);
        reader.ApplyLog(Response.Replace("false", "true"));
        Hook(reader, "agy", "PreInvocation");
        Assert.Equal(ActivityState.Working, seen[^1].Value);
        Assert.True(new AgyLogVersions(_root).Contains("v1"));
        Assert.False(new AgyLogVersions(_root).Contains("v2"));

        var (unknown, unknownSeen) = Reader(AgentKind.AntigravityCli, version: null);
        unknown.ApplyLog(Surface);
        unknown.ApplyLog(Response.Replace("false", "true"));
        Hook(unknown, "agy", "PreInvocation");
        Assert.Equal(ActivityState.WorkingOrAwaitingApproval, unknownSeen[^1].Value);
    }

    [Fact]
    public void 承認の応答の行は承認か拒否かが読めなければ照合しない()
    {
        var (reader, seen) = Reader(AgentKind.AntigravityCli, verified: true);
        reader.ApplyLog(Surface);
        reader.ApplyLog("I0917 input_loop.go:456] Responding to tool confirmation: convID=abcd, stepIdx=3");
        Assert.Equal(ActivityState.AwaitingApproval, seen[^1].Value);
    }

    [Fact]
    public void 同じ読み取りはログの後にフックを当てる()
    {
        var launch = Launch;
        launch.Prepare(AgentKind.AntigravityCli);
        var (reader, seen) = Reader(AgentKind.AntigravityCli, verified: true, launch: launch);
        File.WriteAllText(launch.AgyLogPath, Complete + "\n");
        File.WriteAllText(launch.EventsPath, "1700000000 agy PreInvocation\n");
        reader.Read();
        Assert.Equal(new[] { ActivityState.Resting, ActivityState.Working }, seen.Select(s => s.Value));
        File.AppendAllText(launch.AgyLogPath, Surface + "\n");
        File.AppendAllText(launch.EventsPath, "1700000000 agy PostToolUse\n");
        reader.Read();
        Assert.Equal(ActivityState.AwaitingApproval, seen[^1].Value);
        var count = seen.Count;
        reader.Read();
        Assert.Equal(count, seen.Count);
    }

    [Fact]
    public void 未完の行は次回へ回し追記した分だけ読む()
    {
        var launch = Launch;
        launch.Prepare(AgentKind.CodexCli);
        var (reader, seen) = Reader(AgentKind.CodexCli, launch: launch);
        reader.Read();
        Assert.Empty(seen);
        File.WriteAllText(launch.EventsPath, "1700000000 codex UserPrompt");
        reader.Read();
        Assert.Empty(seen);
        File.AppendAllText(launch.EventsPath, "Submit\n1700000000 codex Stop");
        reader.Read();
        Assert.Equal(ActivityState.Working, Assert.Single(seen).Value);
        File.AppendAllText(launch.EventsPath, "\n");
        reader.Read();
        reader.Read();
        Assert.Equal(2, seen.Count);
        Assert.Equal(ActivityState.Resting, seen[^1].Value);
    }

    [Fact]
    public void 許可設定など照合しないログと未知のフックは状態を変えない()
    {
        var (reader, seen) = Reader(AgentKind.AntigravityCli);
        Hook(reader, "agy", "PreInvocation");
        reader.ApplyLog("permission settings: allow command secret-token");
        reader.ApplyLog("conversation_manager.go:12] Stream completed for INVALID, clearing ResponsePending");
        reader.ApplyLog("tool_confirmation_manager.go] Surfacing tool confirmation: \"tool\"");
        Hook(reader, "agy", "PreToolUse");
        Hook(reader, "codex", "Stop");
        reader.ApplyHook("999999999999999999 agy Stop");
        Assert.Single(seen);
        Assert.DoesNotContain("secret-token", seen[0].Evidence.RedactedSummary);
    }

    [Theory]
    [InlineData(ActivityState.Working)]
    [InlineData(ActivityState.Resting)]
    [InlineData(ActivityState.WorkingOrAwaitingApproval)]
    public void フックの活動は五分で消えず消滅で消える(ActivityState activity)
    {
        var tracker = new DepartmentStatusTracker(new AgentRef("implementation", AgentKind.CodexCli), _clock, TimeSpan.FromMinutes(5));
        var evidence = new Evidence(EvidenceSource.LifecycleHook, _clock.GetUtcNow(), null, null,
            new AgentRef("implementation", AgentKind.CodexCli), null, null, "Codex フック: Stop");
        tracker.OnLifecycleActivity(new(activity, evidence));
        _clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(activity, tracker.Current.Activity.Value);
        tracker.OnDisappeared();
        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
        tracker.OnLifecycleActivity(new(activity, evidence));
        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
    }

    [Fact]
    public void 同じ秒の同じ活動でも最後のイベントを表示し案内を消す()
    {
        var agent = new AgentRef("implementation", AgentKind.CodexCli);
        var tracker = new DepartmentStatusTracker(agent, _clock, TimeSpan.FromMinutes(5));
        tracker.OnLifecycleStarted();
        Assert.True(tracker.AwaitingFirstLifecycleHook);
        var (reader, _) = Reader(AgentKind.CodexCli);
        reader.Observed += (_, observation) => tracker.OnLifecycleActivity(observation);
        Hook(reader, "codex", "UserPromptSubmit");
        Hook(reader, "codex", "PreToolUse");
        Assert.False(tracker.AwaitingFirstLifecycleHook);
        Assert.Equal("Codex フック: PreToolUse", tracker.Current.Activity.Evidence.RedactedSummary);
        tracker.OnLifecycleUnavailable();
        Assert.False(tracker.AwaitingFirstLifecycleHook);
        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
        tracker.OnLifecycleStarted();
        Assert.True(tracker.AwaitingFirstLifecycleHook);
    }

    [Fact]
    public void 構造化イベントの鮮度と同じ強さの比較を保つ()
    {
        var agent = new AgentRef("implementation", AgentKind.CodexCli);
        var tracker = new DepartmentStatusTracker(agent, _clock, TimeSpan.FromMinutes(5));
        var structured = new Evidence(EvidenceSource.StructuredEvent, _clock.GetUtcNow(), null, null, agent, null, null, "構造化");
        var hook = structured with { Source = EvidenceSource.LifecycleHook, ObservedAt = _clock.GetUtcNow().AddSeconds(1) };
        Assert.Same(hook, Evidence.Stronger(structured, hook));
        Assert.Same(hook, Evidence.Stronger(hook, structured));
        var newer = structured with { ObservedAt = hook.ObservedAt.AddSeconds(1) };
        Assert.Same(newer, Evidence.Stronger(hook, newer));
        tracker.OnObserved(structured);
        _clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
    }

    [Fact]
    public void 承認の用件は外部ターミナルだけ窓へ案内する()
    {
        Assert.Equal("窓で承認する", DepartmentCallToAction.ApprovalLabel(externalTerminal: true));
        Assert.Equal("承認を見る", DepartmentCallToAction.ApprovalLabel(externalTerminal: false));
        var agent = new AgentRef("implementation", AgentKind.CodexCli);
        var tracker = new DepartmentStatusTracker(agent, _clock, TimeSpan.FromMinutes(5));
        var evidence = new Evidence(EvidenceSource.LifecycleHook, _clock.GetUtcNow(), null, null, agent, null, null, "観測");
        tracker.OnLifecycleActivity(new(ActivityState.WorkingOrAwaitingApproval, evidence));
        var call = DepartmentCallToAction.From(tracker.Current, sessionRunning: true, externalTerminal: true);
        Assert.Equal(DepartmentPose.Working, call.Pose);
        Assert.False(call.NeedsHuman);
        Assert.Equal(DepartmentAction.None, call.Action);
        tracker.OnLifecycleActivity(new(ActivityState.AwaitingApproval, evidence));
        Assert.Equal(DepartmentAction.ShowApproval, DepartmentCallToAction.From(tracker.Current, externalTerminal: true).Action);
    }

    [Fact]
    public void Codex設定のフックだけを警告し信頼の状態は除く()
    {
        Assert.Empty(DepartmentWarnings.CodexHookOverrides("[hooks.state.\"/<session-flags>/config.toml:Stop:0:0\"]\ntrusted_hash = \"hash\"\nhooks.state = {}\n# [[hooks.Stop]]"));
        Assert.Equal(new[] { "SessionStart", "PreToolUse", "Stop" }, DepartmentWarnings.CodexHookOverrides(
            "[[hooks.Stop]]\n[hooks.PreToolUse]\nhooks.SessionStart = []\n[[hooks.Stop.hooks]]"));
        Assert.Equal(new[] { "Stop" }, DepartmentWarnings.CodexHookOverrides("[hooks.\"Stop\"]"));
        var departments = new[] { new DepartmentDefinition("dev", "開発", "開発する", AgentKind.CodexCli, DriveMode.ExternalTerminal) };
        Assert.Equal("Codex の config.toml のフック（Stop）は、部門の起動時に置き換わる",
            Assert.Single(DepartmentWarnings.For(departments, "[[hooks.Stop]]")).Message);
    }

    [Fact]
    public void 起動の場所と七日より古い記録の片付け()
    {
        var old = Launch;
        old.Prepare(AgentKind.CodexCli);
        Assert.Matches(@"/activity/[0-9a-f]{16}/implementation/\d{8}-\d{6}-[0-9a-f]{4}$", old.DirectoryPath);
        File.WriteAllText(old.EventsPath, "event\n");
        File.SetLastWriteTimeUtc(old.EventsPath, _clock.GetUtcNow().UtcDateTime.AddDays(-8));
        Directory.SetLastWriteTimeUtc(old.DirectoryPath, _clock.GetUtcNow().UtcDateTime.AddDays(-8));
        var recent = Launch;
        recent.Prepare(AgentKind.CodexCli);
        File.WriteAllText(recent.EventsPath, "event\n");
        Directory.SetLastWriteTimeUtc(recent.DirectoryPath, _clock.GetUtcNow().UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(recent.EventsPath, _clock.GetUtcNow().UtcDateTime);
        ActivityLaunch.Cleanup(_root, "/workspace", _clock.GetUtcNow());
        Assert.False(Directory.Exists(old.DirectoryPath));
        Assert.True(Directory.Exists(recent.DirectoryPath));
        recent.Delete();
        recent.Delete();
        Assert.False(Directory.Exists(recent.DirectoryPath));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("1.2.4\n", "1.2.4")]
    [InlineData("  1.10.0  ", "1.10.0")]
    [InlineData("agy 1.2.4", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void agyの版はversionの出力の数字だけの行から読む(string? output, string? expected) =>
        Assert.Equal(expected, MultiAIAgentCompany.Core.Activity.AgyVersion.Parse(output));
}
