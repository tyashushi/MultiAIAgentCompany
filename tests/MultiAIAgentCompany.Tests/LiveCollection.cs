using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 実プロセスを起動するテストは<b>直列に走らせる</b>。
/// </summary>
/// <remarks>
/// xunit は既定でテストクラスを並列に走らせるが、そうすると Claude と Codex の CLI が
/// 同時に同じアカウントを叩き、<b>待たされた側が承認要求を受け取る前にタイムアウトする</b>。
/// 実測（2026-09-06）: 単独なら 3/3 通るのに、両方を同時に走らせると1件がタイムアウトした。
/// <para>
/// これは §13-5b で Unity MCP について確かめたのと同じ形 ——
/// <b>並列度を上げてもスループットは増えず、レイテンシだけが伸びて待った側が失敗する。</b>
/// そして「混雑によるタイムアウト」を「コードの失敗」と読んではいけない。
/// 対策も同じで、直列化ではなく<b>入場制限</b>（§14-2）にあたる。
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCollection
{
    public const string Name = "live-process";
}
