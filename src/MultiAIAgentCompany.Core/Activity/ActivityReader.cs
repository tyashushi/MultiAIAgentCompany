using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Activity;

/// <summary>追記分だけを読み、未完の行は次回に回す（設計 §61-1）。</summary>
public sealed class AppendedLines(string path)
{
    private long _position;
    private readonly List<byte> _partial = [];

    public void Read(Action<string> onLine)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(_position, SeekOrigin.Begin);
            var buffer = new byte[4096];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                _position += count;
                foreach (var value in buffer.AsSpan(0, count))
                {
                    if (value == (byte)'\n')
                    {
                        var line = Encoding.UTF8.GetString(_partial.ToArray()).TrimEnd('\r');
                        _partial.Clear();
                        onLine(line);
                    }
                    else _partial.Add(value);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // まだ無いファイルや書き込み中の競合は、次の周期で読む（設計 §61-1）。
        }
    }
}

/// <summary>ログの本文を保持せず、決めた形だけを活動に変換する（設計 §61-3）。</summary>
public sealed class ActivityReader
{
    private static readonly Regex Surfacing = new(@"tool_confirmation_manager\.go:\d+\] Surfacing tool confirmation: """, RegexOptions.CultureInvariant);
    private static readonly Regex Responding = new(@"input_loop\.go:\d+\] Responding to tool confirmation: convID=[^,]*, stepIdx=\d+, approved=(?<approved>true|false)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Completed = new(@"conversation_manager\.go:\d+\] Stream completed for [0-9a-f-]+, clearing ResponsePending", RegexOptions.CultureInvariant);
    private readonly AgentRef _agent;
    private readonly string? _version;
    private readonly AgyLogVersions _versions;
    private readonly TimeProvider _clock;
    private readonly AppendedLines _events;
    private readonly AppendedLines _log;
    private bool _approvalOpen;
    private ActivityState _hookActivity = ActivityState.Unknown;
    private Evidence? _approvalEvidence;

    public ActivityReader(ActivityLaunch launch, AgentKind kind, TimeProvider clock, string? detectedVersion = null)
    {
        _agent = new AgentRef(launch.DepartmentId, kind);
        _clock = clock;
        _version = detectedVersion;
        _versions = new AgyLogVersions(launch.DataRoot);
        _events = new AppendedLines(launch.EventsPath);
        _log = new AppendedLines(launch.AgyLogPath);
    }

    public bool HasHookEvent { get; private set; }
    public event EventHandler<Observed<ActivityState>>? Observed;

    public void Read()
    {
        // 秒が同じでも並べ替えず、ログ→フックの順で適用する（設計 §61-3）。
        if (_agent.Kind is AgentKind.AntigravityCli) _log.Read(ApplyLog);
        _events.Read(ApplyHook);
    }

    public void ApplyHook(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[1] != AgentExecutable.NameOf(_agent.Kind)
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return;
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return; }

        // 入力の文字列を根拠へ流さず、認識したイベントの固定文だけを使う（設計 §61-3）。
        var eventName = parts[2] switch
        {
            "SessionStart" => "SessionStart", "UserPromptSubmit" => "UserPromptSubmit",
            "PreToolUse" => "PreToolUse", "PermissionRequest" => "PermissionRequest",
            "PostToolUse" => "PostToolUse", "Stop" => "Stop",
            "PreInvocation" => "PreInvocation", "PostInvocation" => "PostInvocation",
            _ => null,
        };
        if (eventName is null) return;
        if (_agent.Kind is AgentKind.AntigravityCli)
        {
            if (eventName is not ("PreInvocation" or "PostInvocation" or "PostToolUse" or "Stop")) return;
            _hookActivity = eventName == "Stop" ? ActivityState.Resting : ActivityState.Working;
        }
        else
        {
            if (!ActivityHooks.Events.Contains(eventName)) return;
            if (eventName == "PermissionRequest") _approvalOpen = true;
            if (eventName is "PostToolUse" or "UserPromptSubmit" or "Stop") _approvalOpen = false;
            _hookActivity = eventName is "SessionStart" or "Stop" ? ActivityState.Resting : ActivityState.Working;
        }
        HasHookEvent = true;
        var cli = _agent.Kind switch { AgentKind.ClaudeCode => "Claude Code", AgentKind.CodexCli => "Codex", _ => "agy" };
        var evidence = NewEvidence($"{cli} フック: {eventName}", at);
        if (eventName == "PermissionRequest") _approvalEvidence = evidence;
        Publish(evidence);
    }

    public void ApplyLog(string line)
    {
        if (_agent.Kind is not AgentKind.AntigravityCli) return;
        Evidence evidence;
        if (Surfacing.IsMatch(line))
        {
            _approvalOpen = true;
            // **ログの形の検証はこの行で行う**（§61-9）。`Stream completed` は agy の終了時にしか
            // 出ず（実機で判明）、動いている間に検証する機会が無かった。承認画面の行を照合できたなら、
            // この版でログの形が合っていると言える。
            _versions.Validate(_version);
            evidence = NewEvidence("agy ログ: 承認画面を出した", _clock.GetUtcNow());
            _approvalEvidence = evidence;
        }
        else if (Responding.Match(line) is { Success: true } responding)
        {
            _approvalOpen = false;
            if (responding.Groups["approved"].Value == "false")
            {
                // 拒否したあとはフックが1つも来なかった（§60-5b）。次の指示を待っていると読む。
                // 作業を続けたなら、次のフックで作業中に戻る。
                _hookActivity = ActivityState.Resting;
                evidence = NewEvidence("agy ログ: 承認を拒否した", _clock.GetUtcNow());
            }
            else
            {
                evidence = NewEvidence("agy ログ: 承認した", _clock.GetUtcNow());
            }
        }
        else if (Completed.IsMatch(line))
        {
            // agy の終了時に出る（ターンの終わりではない。§61-9）。
            _approvalOpen = false;
            _hookActivity = ActivityState.Resting;
            evidence = NewEvidence("agy ログ: 会話を閉じた", _clock.GetUtcNow());
        }
        else return;
        Publish(evidence);
    }

    private void Publish(Evidence evidence)
    {
        var activity = _approvalOpen ? ActivityState.AwaitingApproval : _hookActivity;
        if (_agent.Kind is AgentKind.AntigravityCli && activity == ActivityState.Working && !_versions.Contains(_version))
            activity = ActivityState.WorkingOrAwaitingApproval;
        Observed?.Invoke(this, new Observed<ActivityState>(activity, _approvalOpen ? _approvalEvidence! : evidence));
    }

    private Evidence NewEvidence(string summary, DateTimeOffset at) =>
        new(EvidenceSource.LifecycleHook, at, null, null, _agent, _version, null, summary);
}
