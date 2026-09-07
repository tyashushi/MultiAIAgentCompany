using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// ウィンドウの大きさ・位置とペインの幅を覚える（設計 §28-5）。
/// </summary>
/// <remarks>
/// <b>覚えるのは見た目だけ。</b> §21-3 と同じで、**状態は覚えない** ——
/// trust も部門の稼働も、毎回読み直す（§7）。
/// <para>
/// <b>読めないときは既定に戻す。</b> ここが壊れてもアプリは開けるべきなので、
/// <c>state.json</c>（§14-1）のように「読めない」を人間へ突き返さない。
/// </para>
/// </remarks>
public sealed class WindowLayoutMemory(string settingsPath)
{
    private readonly string _path = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));

    public static WindowLayoutMemory CreateDefault() =>
        new(Path.Combine(WorkspaceMemory.RuntimeRoot, "window.json"));

    /// <returns>覚えていた見た目。無い・読めない・画面に収まらないなら null。</returns>
    public WindowLayout? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var layout = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(_path), Options);

            // **おかしな値は使わない。** 0 幅や、画面から外れた位置を復元すると
            // 「起動したのに何も見えない」になる —— 一番たちの悪い壊れ方。
            return layout is { Width: >= 640, Height: >= 480 } ? layout : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(WindowLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(layout, Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくてもアプリは動く。**閉じることを止めない。**
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <param name="Width">ウィンドウの幅。</param>
/// <param name="Height">ウィンドウの高さ。</param>
/// <param name="Maximized">最大化していたか。</param>
/// <param name="LeftPane">左ペインの幅。</param>
/// <param name="RightPane">右ペインの幅。</param>
public sealed record WindowLayout(
    double Width, double Height, bool Maximized, double LeftPane, double RightPane);
