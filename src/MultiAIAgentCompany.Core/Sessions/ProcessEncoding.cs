using System.Text;

namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>子プロセスの標準入出力は UTF-8（BOM なし）で読み書きする。</summary>
/// <remarks>
/// <b>指定しないと Windows ではコンソールのコードページで stdout を読む。</b>
/// cp932 のコンソールから起動したアプリで、秘書の stream-json の日本語が化けて
/// turn の終わりを読めず、自動承認が化けた入力を <c>updatedInput</c> として送り返して
/// 化けたファイルを書かせた（2026-09-18、Windows 11 の実機）。stdin の既定は UTF-8 なので
/// 片側だけ壊れる。BOM を付けると stdin の1行目が JSON として読めなくなる。
/// </remarks>
public static class ProcessEncoding
{
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
