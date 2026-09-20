using System.Text;

namespace MultiAIAgentCompany.Core.Terminal;

/// <summary>
/// Terminal.app に渡す文字列の組み立て（設計 §32）。
/// </summary>
/// <remarks>
/// <b>OS の API を呼ばないものだけをここに置く。</b> 文字列を作るだけなので
/// どの OS でも動き、**どの OS でもテストできる** ——
/// <see cref="MacTerminalLauncher"/> の方は macOS 専用（<c>osascript</c> を起こす）。
/// </remarks>
public static class MacTerminalScript
{
    /// <summary>
    /// 起動用のシェルスクリプト。
    /// </summary>
    /// <remarks>
    /// <b><c>exit_code</c> を書かない</b>（設計 §32-2e）。CLI は turn が終わっても
    /// セッションを終了しないので、終了コードは**人間が窓を閉じたとき**にしか現れず、
    /// 仕事の終わりとは無関係である。**完了の信号は <c>report.md</c> だけ**（§16-1）。
    /// <para>
    /// <c>exec</c> で置き換えるので、<b>PID ファイルが指すのは CLI 自身</b>になる。
    /// プロセスグループはシェルのものが引き継がれるので、
    /// <c>kill -TERM -&lt;pgid&gt;</c> がそのまま効く。
    /// </para>
    /// </remarks>
    public static string Build(TerminalLaunchRequest request, string pidPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#!/bin/bash");
        builder.AppendLine("# MultiAIAgentCompany が生成した起動スクリプト（設計 §32）。");
        builder.AppendLine($"echo $$ > {Quote(pidPath)}");

        // 窓の見出し。**人間が見分けるためのもの**で、照合には使わない（§32-2c）。
        builder.AppendLine($"printf '\\033]0;%s\\007' {Quote(request.Title)}");
        builder.AppendLine($"cd {Quote(request.WorkingDirectory)} || exit 1");

        // フックの中身は固定し、起動の行き先だけを環境で渡す（設計 §61-1）。
        if (request.EnvironmentVariables is { } environment)
            foreach (var (name, value) in environment)
                builder.AppendLine($"export {name}={Quote(value)}");

        var command = new StringBuilder(Quote(request.Command));
        foreach (var argument in request.Arguments)
        {
            command.Append(' ').Append(Quote(argument));
        }

        builder.AppendLine($"exec {command}");
        return builder.ToString();
    }

    /// <summary>シングルクォートで囲む。中の <c>'</c> は閉じて足して開き直す。</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>AppleScript の文字列リテラルへ入れる。</summary>
    public static string EscapeForAppleScriptString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// <c>"tab 1 of window id 43990 tty /dev/ttys013"</c> を読む（設計 §62-24）。
    /// </summary>
    /// <remarks>
    /// <b>TTY は無くても読める。</b> 古い形（<c>tab 1 of window id 43990</c>）でも
    /// 窓とタブは取れる —— **取れなかったものを捏造しない**（§7）。
    /// </remarks>
    public static TerminalHandle? ParseHandle(string output, string pidPath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output, @"tab\s+(\d+)\s+of\s+window\s+id\s+(-?\d+)");
        if (!match.Success) return null;

        var tty = System.Text.RegularExpressions.Regex.Match(output, @"tty\s+(/dev/\S+)");
        return new TerminalHandle(
            match.Groups[2].Value, int.Parse(match.Groups[1].Value), pidPath,
            tty.Success ? tty.Groups[1].Value : null);
    }

    /// <summary>
    /// そのタブを前に出す AppleScript（設計 §62-24）。
    /// </summary>
    /// <remarks>
    /// <b>TTY があれば、それで探す。</b> 見つからなければ<b>何も選ばない</b> ——
    /// そのタブはもう無いのだから、**代わりに隣を選ぶのは「前に出した」の嘘になる**（§7）。
    /// TTY が無いハンドル（古い起動・Windows）は、これまでどおり位置で選ぶ。
    /// </remarks>
    public static string FocusScript(TerminalHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.Tty is not { Length: > 0 } tty)
        {
            return $"""
                tell application "Terminal"
                    activate
                    set index of window id {handle.WindowId} to 1
                    set selected of tab {handle.TabIndex} of window id {handle.WindowId} to true
                end tell
                """;
        }

        return $"""
            tell application "Terminal"
                activate
                repeat with w in windows
                    repeat with t in tabs of w
                        if tty of t is "{EscapeForAppleScriptString(tty)}" then
                            set index of w to 1
                            set selected of t to true
                            return "focused"
                        end if
                    end repeat
                end repeat
                error "tab not found" number 1730
            end tell
            """;
    }
}
