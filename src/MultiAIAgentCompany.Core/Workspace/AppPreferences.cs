using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// アプリ全体の好み（設計 §62-25）。
/// </summary>
/// <remarks>
/// <b>ワークスペースの設定ではない。</b> 部門や秘書の設定は <c>.company/departments.json</c>
/// （そのフォルダの取り決め）だが、ここは<b>この人のアプリの使い方</b>なので
/// アプリのデータフォルダに置く（§21-3）。
/// <para>
/// <b>読めないときは既定に戻す。</b> ここが壊れてもアプリは開けるべきなので、
/// <see cref="WindowLayoutMemory"/> と同じ姿勢にする（<c>state.json</c> のようには突き返さない）。
/// </para>
/// </remarks>
public sealed class AppPreferences(string settingsPath)
{
    private readonly string _path = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));

    public static AppPreferences CreateDefault() =>
        new(Path.Combine(WorkspaceMemory.RuntimeRoot, "preferences.json"));

    public AppPreferenceValues Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppPreferenceValues>(File.ReadAllText(_path), Options) ?? new()
                : new AppPreferenceValues();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppPreferenceValues();
        }
    }

    /// <summary>書けなければ理由を返す。<b>黙って失敗しない</b>（§28-1）。</summary>
    public string? Save(AppPreferenceValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values, Options));
            File.Move(temporary, _path, overwrite: true);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <param name="CloseTerminalsWhenDone">
/// 仕事が終わった部門のターミナルの窓を閉じるか（設計 §62-25、人間が決めた既定は「閉じる」）。
/// <b>閉じるのは、中の CLI が終わったことを確かめてからだけ</b>。
/// </param>
public sealed record AppPreferenceValues(bool CloseTerminalsWhenDone = true);
