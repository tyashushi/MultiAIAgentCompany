using System.Diagnostics;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// OS の通知を出す（設計 §28-4）。
/// </summary>
/// <remarks>
/// <b>承認は人間の出番</b>（§3 の (a)）なのに、ウィンドウが背面だと気付けなかった（§25-2 の 24番）。
/// <para>
/// <b>依存を増やさない。</b> Avalonia に OS 通知の API は無いので、macOS では
/// <c>osascript</c> に投げる。**それ以外の OS では何もしない** ——
/// 出せないことを、出したことにしない（§7）。
/// </para>
/// <para>
/// <b>中身を書かない</b>（§10）。通知は画面の外へ出ていく表示なので、
/// 部門名と要件の種類までにする。何が要求されたかはアプリの中で見る。
/// </para>
/// </remarks>
public static class DesktopNotifier
{
    /// <summary>出せたか。<b>出せなかったことを黙らない</b>ために返す。</summary>
    public static bool Notify(string title, string body)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("osascript")
            {
                ArgumentList =
                {
                    "-e",
                    $"display notification \"{Escape(body)}\" with title \"{Escape(title)}\"",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });

            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 通知が出せなくてもアプリは続く。**画面の中の表示が正本**（§1）。
            return false;
        }
    }

    /// <summary>
    /// AppleScript の文字列に入れてよい形にする。
    /// </summary>
    /// <remarks>
    /// <b>引用符と改行を潰す。</b> 部門名は人間が設定ファイルに書けるので、
    /// そのまま埋めるとスクリプトの構造が変わり得る。
    /// </remarks>
    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .ReplaceLineEndings(" ");
}
