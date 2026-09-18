using System.Text.RegularExpressions;
using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Workspace.Trust;

/// <summary><c>~/.codex/config.toml</c> の projects セクションだけを読む trust 検査。</summary>
public sealed partial class CodexCliTrustProbe : IWorkspaceTrustProbe
{
    private readonly string _settingsPath;

    public CodexCliTrustProbe() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }

    public CodexCliTrustProbe(string homeDirectory) =>
        _settingsPath = Path.Combine(Path.GetFullPath(homeDirectory), ".codex", "config.toml");

    public AgentKind Kind => AgentKind.CodexCli;

    public Task<bool> HasNoRecordAsync(CancellationToken ct) => Task.FromResult(!File.Exists(_settingsPath));

    public async Task<bool?> IsTrustedAsync(WorkspaceRef workspace, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_settingsPath)) return null;
        try
        {
            var lines = await File.ReadAllLinesAsync(_settingsPath, ct);
            var inTarget = false;
            var foundTarget = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var section = ProjectSection().Match(line);
                if (section.Success)
                {
                    // **Windows のパスはリテラル文字列（'C:\...'）で書かれる。** `\` をエスケープしなくて
                    // 済む形を toml の書き手が選ぶので、こちらは中身を解釈せずにそのまま使う。
                    var path = section.Groups["literal"].Success
                        ? section.Groups["literal"].Value
                        : UnescapeTomlBasicString(section.Groups["path"].Value);
                    if (path is null) return null;
                    inTarget = WorkspacePathNormalizer.Equals(path, workspace.Root);
                    foundTarget |= inTarget;
                    continue;
                }
                if (line.StartsWith('['))
                {
                    // **[projects. で始まるのに読めない見出しだけが致命的。**
                    // その中に対象が隠れているかもしれないので、false と答えてはいけない。
                    if (line.StartsWith("[projects.", StringComparison.Ordinal)) return null;

                    // それ以外の見出し（mcp_servers / plugins / tui など）は読み飛ばす。
                    inTarget = false;
                    continue;
                }

                // **無関係な行では諦めない。** §13-9 規則4 の「想定した形でなければ null」は
                // *読んでいる箇所* の話であって、ファイル全体の話ではない。
                // ここで諦めると、配列やネストしたテーブルを含む本物の config.toml では
                // **必ず「判定できない」になり、probe が役に立たなくなる**
                // （2026-09-06、実機の画面で発覚）。
                if (inTarget && line.StartsWith("trust_level", StringComparison.Ordinal))
                {
                    var match = TrustLevel().Match(line);
                    if (!match.Success) return null;
                    return string.Equals(match.Groups["value"].Value, "trusted", StringComparison.Ordinal);
                }
            }
            return foundTarget ? null : false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? UnescapeTomlBasicString(string value)
    {
        // trust の設定が出す通常の quoted key に限定する。未知のエスケープは解釈しない。
        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                result.Append(value[index]);
                continue;
            }
            if (++index == value.Length) return null;
            result.Append(value[index] switch
            {
                '\\' => '\\', '"' => '"', 'b' => '\b', 't' => '\t',
                'n' => '\n', 'f' => '\f', 'r' => '\r', _ => '\0',
            });
            if (result[^1] == '\0') return null;
        }
        return result.ToString();
    }

    [GeneratedRegex("^\\[projects\\.(?:\\\"(?<path>(?:\\\\.|[^\\\"\\\\])*)\\\"|'(?<literal>[^'\\r\\n]*)')\\]\\s*(?:#.*)?$")]
    private static partial Regex ProjectSection();
    [GeneratedRegex("^\\[[A-Za-z0-9_.-]+\\]\\s*(?:#.*)?$")]
    private static partial Regex Section();
    [GeneratedRegex("^[A-Za-z0-9_.-]+\\s*=.*$")]
    private static partial Regex KeyValue();
    [GeneratedRegex("^trust_level\\s*=\\s*\\\"(?<value>[^\\\"]*)\\\"\\s*(?:#.*)?$")]
    private static partial Regex TrustLevel();
}
