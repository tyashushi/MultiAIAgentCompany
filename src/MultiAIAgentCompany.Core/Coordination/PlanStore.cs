using System.Text.Json;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 計画の保存口（設計 §37）。進行の判断は <see cref="PlanAdvance.Decide"/> に任せる。
/// </summary>
public sealed class PlanStore
{
    private const string PlanFileName = "plan.json";
    private static readonly JsonSerializerOptions JsonOptions = new(TaskStateJson.Options)
    {
        // IsReview は ReviewsStep から導ける。保存すると正本が2つになる（§31-1）。
        IgnoreReadOnlyProperties = true,
    };

    private readonly CompanyPaths _paths;
    private readonly TimeProvider _clock;

    public PlanStore(CompanyPaths paths, TimeProvider clock)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<IReadOnlyList<string>> ListIdsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(_paths.PlansRoot))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        // slug に使えないディレクトリは計画ではない。走査全体を落とさない。
        IReadOnlyList<string> ids = Directory.EnumerateDirectories(_paths.PlansRoot)
            .Select(Path.GetFileName)
            .Where(CompanyPaths.IsValidSlug)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(ids);
    }

    public async Task<PlanReadResult> ReadAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var planPath = _paths.PlanFile(id);
        try
        {
            // Exists はアクセス不能も false にする。開いて確かめ、読めないものを「無い」にしない。
            await using var stream = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            var plan = await JsonSerializer.DeserializeAsync<Plan>(stream, JsonOptions, ct);
            if (plan is null)
            {
                return new PlanReadResult.Unreadable("plan.json が空です");
            }

            if (!string.Equals(plan.Id, id, StringComparison.Ordinal))
            {
                return new PlanReadResult.Unreadable("plan.json の Id がディレクトリ名と一致しません");
            }

            if (plan.Goal is null || plan.Steps is null
                || plan.Steps.Any(step => step is null || step.DepartmentId is null || step.Handover is null))
            {
                return new PlanReadResult.Unreadable("plan.json の必須項目が null です");
            }

            if (plan.Steps.Any(step => step.Verdict is { } verdict && !Enum.IsDefined(verdict)))
            {
                return new PlanReadResult.Unreadable("plan.json に未定義の列挙値があります");
            }

            return new PlanReadResult.Found(plan);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PlanReadResult.Missing();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new PlanReadResult.Unreadable($"plan.json を読めません: {exception.Message}");
        }
    }

    public async Task<PlanWriteResult> CreateAsync(
        string id, string goal, IReadOnlyList<PlanStep> steps, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = _paths.PlanDirectory(id);
        var planPath = _paths.PlanFile(id);
        Directory.CreateDirectory(directory);
        if (File.Exists(planPath))
        {
            return new PlanWriteResult.Conflicted("既にある");
        }

        var now = _clock.GetUtcNow();
        var plan = new Plan(id, goal, steps, 0, false, 1, now, now);
        try
        {
            await WritePlanAtomicallyAsync(planPath, plan, overwrite: false, ct);
            return new PlanWriteResult.Written(plan);
        }
        catch (IOException) when (File.Exists(planPath))
        {
            // 確認から置換までの間に別のアプリが作った計画も上書きしない。
            return new PlanWriteResult.Conflicted("既にある");
        }
    }

    public async Task<PlanWriteResult> WriteAsync(Plan expected, Plan next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(next);
        ct.ThrowIfCancellationRequested();

        if (!string.Equals(expected.Id, next.Id, StringComparison.Ordinal))
        {
            return new PlanWriteResult.Rejected("計画の Id は変更できません");
        }

        var read = await ReadAsync(expected.Id, ct);
        if (read is PlanReadResult.Missing)
        {
            return new PlanWriteResult.Rejected("plan.json が存在しません");
        }

        if (read is PlanReadResult.Unreadable unreadable)
        {
            return new PlanWriteResult.Conflicted($"plan.json を検証できません: {unreadable.Reason}");
        }

        var current = ((PlanReadResult.Found)read).Plan;
        if (current.Revision != expected.Revision)
        {
            return new PlanWriteResult.Conflicted(
                $"Revision が一致しません（expected: {expected.Revision}, actual: {current.Revision}）");
        }

        // 知らない書き換えはマージしない。通った更新だけを1つ進める（§14-1）。
        var updated = next with
        {
            Revision = checked(current.Revision + 1),
            UpdatedAt = _clock.GetUtcNow(),
        };
        await WritePlanAtomicallyAsync(_paths.PlanFile(expected.Id), updated, overwrite: true, ct);
        return new PlanWriteResult.Written(updated);
    }

    private static async Task WritePlanAtomicallyAsync(string planPath, Plan plan, bool overwrite, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(planPath)!;
        var temporaryPath = Path.Combine(directory, $".{PlanFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, plan, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(temporaryPath, planPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public abstract record PlanReadResult
{
    public sealed record Found(Plan Plan) : PlanReadResult;
    public sealed record Missing : PlanReadResult;
    public sealed record Unreadable(string Reason) : PlanReadResult;
}

public abstract record PlanWriteResult
{
    public sealed record Written(Plan Plan) : PlanWriteResult;
    public sealed record Rejected(string Reason) : PlanWriteResult;
    public sealed record Conflicted(string Reason) : PlanWriteResult;
}
