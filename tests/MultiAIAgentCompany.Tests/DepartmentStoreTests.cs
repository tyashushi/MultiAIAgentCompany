using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class DepartmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"multi-ai-agent-company-departments-{Guid.NewGuid():N}");
    private readonly CompanyPaths _paths;
    private readonly DepartmentStore _store;

    public DepartmentStoreTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new CompanyPaths(_root);
        _store = new DepartmentStore(_paths);
    }

    [Theory]
    [InlineData(AgentPermissionMode.Auto)]
    [InlineData(AgentPermissionMode.Manual)]
    [InlineData(AgentPermissionMode.AcceptEdits)]
    [InlineData(AgentPermissionMode.Plan)]
    public async Task 権限モードは文字列で保存され読み戻せる(AgentPermissionMode mode)
    {
        var department = new DepartmentDefinition(
            "design", "設計", "設計する", AgentKind.ClaudeCode, DriveMode.ExternalTerminal, PermissionMode: mode);
        Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new(0, []), [department], CancellationToken.None));

        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        Assert.Equal(department, Assert.Single(read.Definition.Departments));
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_paths.Departments));
        var stored = json.RootElement.GetProperty("departments")[0].GetProperty("permissionMode");
        Assert.Equal(System.Text.Json.JsonValueKind.String, stored.ValueKind);
        Assert.Equal(mode.ToString(), stored.GetString());
        Assert.Empty(await _store.FindUnreadKeysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 権限モードのない既存ファイルはCLI任せで読める()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.Departments)!);
        await File.WriteAllTextAsync(_paths.Departments, """
            {"revision":1,"departments":[{"id":"design","displayName":"設計","responsibility":"設計する",
            "agent":"ClaudeCode","mode":"ExternalTerminal"}]}
            """);
        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        Assert.Null(Assert.Single(read.Definition.Departments).PermissionMode);
    }

    [Theory]
    [InlineData(AgentKind.CodexCli, AgentPermissionMode.Plan)]
    [InlineData(AgentKind.AntigravityCli, AgentPermissionMode.Auto)]
    public async Task 持たない権限モードでも読み書きを拒まず警告する(AgentKind kind, AgentPermissionMode mode)
    {
        var department = new DepartmentDefinition(
            "design", "設計", "設計する", kind, DriveMode.ExternalTerminal, PermissionMode: mode);
        Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new(0, []), [department], CancellationToken.None));
        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        Assert.Equal(department, Assert.Single(read.Definition.Departments));
        var warning = Assert.Single(DepartmentWarnings.For(read.Definition.Departments));
        Assert.Equal("design", warning.DepartmentId);
        Assert.Contains("渡さずに起動", warning.Message);
        Assert.Contains("departments.json", warning.Message);
        Assert.Contains("`design`", warning.Message);
        Assert.Contains("permissionMode", warning.Message);
        Assert.Contains("消すか", warning.Message);
    }

    [Fact]
    public void 構造化の権限モードは効かないと警告する()
    {
        var department = new DepartmentDefinition(
            "design", "設計", "設計する", AgentKind.ClaudeCode, DriveMode.Structured,
            PermissionMode: AgentPermissionMode.Plan);
        var warning = Assert.Single(DepartmentWarnings.For([department]));
        Assert.Equal("design", warning.DepartmentId);
        Assert.Contains("外部ターミナルの部門にしか効きません", warning.Message);
        Assert.Contains("permissionMode", warning.Message);
        Assert.Contains("ExternalTerminal", warning.Message);
    }

    [Fact]
    public void 持たないモードを構造化に指定すると両方の警告が出る()
    {
        var department = new DepartmentDefinition(
            "implementation", "実装", "実装する", AgentKind.CodexCli, DriveMode.Structured,
            PermissionMode: AgentPermissionMode.Plan);
        Assert.Equal(2, DepartmentWarnings.For([department]).Count);
    }

    [Fact]
    public void 持っている権限モードを外部ターミナルで使うときは警告しない()
    {
        foreach (var kind in new[] { AgentKind.ClaudeCode, AgentKind.CodexCli, AgentKind.AntigravityCli })
        {
            foreach (var mode in AgentPermissionModes.For(kind))
            {
                var department = new DepartmentDefinition(
                    "design", "設計", "設計する", kind, DriveMode.ExternalTerminal, PermissionMode: mode);
                Assert.Empty(DepartmentWarnings.For([department]));
            }
        }
    }

    [Fact]
    public async Task 既定の部門集合は保存して読み直せる()
    {
        // **既定が検証を通らない、を作らない**（レビューで発覚、設計 §13-3 追記2）。
        // Antigravity は承認の往復が無いが、握りつぶしは denied_actions で検出できる。
        var defaults = DepartmentStore.CreateDefaultDepartments();

        Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new(0, []), defaults, CancellationToken.None));
        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));

        Assert.Equal(defaults, read.Definition.Departments);
    }

    [Fact]
    public async Task 保存して読むと往復する()
    {
        var saved = Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(new(0, []), Departments(), CancellationToken.None));
        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        // record の等値比較はコレクションを要素で見ない（配列と List は別物になる）。
        // 中身を比べる。
        Assert.Equal(saved.Definition.Revision, read.Definition.Revision);
        Assert.Equal(saved.Definition.Departments, read.Definition.Departments);
        Assert.Equal(1, read.Definition.Revision);
    }

    [Fact]
    public async Task 古いexpectedは拒否されファイルを変えない()
    {
        var first = Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(new(0, []), Departments(), CancellationToken.None));
        var second = Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(first.Definition, Departments("review"), CancellationToken.None));
        var before = await File.ReadAllTextAsync(_paths.Departments);
        Assert.IsType<DefinitionWriteResult.Rejected>(await _store.SaveAsync(first.Definition, Departments("testing"), CancellationToken.None));
        Assert.Equal(before, await File.ReadAllTextAsync(_paths.Departments));
        Assert.Equal(2, second.Definition.Revision);
    }

    [Fact]
    public async Task 無いファイルはMissingで壊れたJSONはUnreadable()
    {
        Assert.IsType<DefinitionReadResult.Missing>(await _store.ReadAsync(CancellationToken.None));
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.Departments, "{");
        Assert.IsType<DefinitionReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 重複IdはUnreadable()
    {
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.Departments,
            "{\"revision\":1,\"departments\":[{\"id\":\"same\",\"displayName\":\"設計\",\"responsibility\":\"x\",\"agent\":\"ClaudeCode\",\"mode\":\"Structured\"},{\"id\":\"same\",\"displayName\":\"実装\",\"responsibility\":\"x\",\"agent\":\"CodexCli\",\"mode\":\"Structured\"}]}");
        Assert.IsType<DefinitionReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 未定義の権限モードはUnreadable()
    {
        // **数値で書くと enum に入ってしまう**（Codex の指摘）。Agent / Mode と同じく弾く（§51）。
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.Departments,
            "{\"revision\":1,\"departments\":[{\"id\":\"impl\",\"displayName\":\"実装\",\"responsibility\":\"x\",\"agent\":\"CodexCli\",\"mode\":\"ExternalTerminal\",\"permissionMode\":999}]}");
        Assert.IsType<DefinitionReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Antigravityの_Structured_は握りつぶしを検出できるので保存できる()
    {
        // **2026-09-06 に規則を変えた**（設計 §13-3 追記2 / §22-4）。
        // 承認の往復は無いが、握りつぶしは `result.denied_actions` で検出できる ——
        // `status:"SUCCESS"` が嘘をつくときの唯一の手がかりがそれ（§14-4 の3層判定）。
        // 往復の有無だけで弾いていたので、既定を Structured に変えたあと
        // **既定の部門集合が保存できなくなっていた。**
        var departments = new[] { new DepartmentDefinition("research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.Structured) };

        Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(new(0, []), departments, CancellationToken.None));
    }

    [Fact]
    public async Task 日本語はエスケープされず欠けたフィールドは既定値で埋めない()
    {
        await _store.SaveAsync(new(0, []), Departments(), CancellationToken.None);
        var json = await File.ReadAllTextAsync(_paths.Departments);
        Assert.Contains("設計", json); Assert.DoesNotContain("\\u", json);
        await File.WriteAllTextAsync(_paths.Departments, "{\"revision\":1,\"departments\":[{\"id\":\"design\",\"displayName\":\"設計\",\"agent\":\"ClaudeCode\",\"mode\":\"Structured\"}]}");
        Assert.IsType<DefinitionReadResult.Unreadable>(await _store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void 既定の部門は能力の既定モードを使う()
    {
        var departments = DepartmentStore.CreateDefaultDepartments();
        Assert.Equal(9, departments.Count);
        Assert.All(departments, d => Assert.Equal(AgentCapabilities.For(d.Agent).DefaultDriveMode, d.Mode));
    }

    [Fact]
    public async Task 報告期限の分数は往復し計算値はJSONへ書かない()
    {
        var definition = Departments()[0] with { ReportDeadlineMinutes = 45 };
        Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new(0, []), [definition], CancellationToken.None));

        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        var department = Assert.Single(read.Definition.Departments);
        Assert.Equal(45, department.ReportDeadlineMinutes);
        Assert.Equal(TimeSpan.FromMinutes(45), department.ReportDeadline);

        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_paths.Departments));
        var saved = json.RootElement.GetProperty("departments")[0];
        Assert.Equal(45, saved.GetProperty("reportDeadlineMinutes").GetInt32());
        Assert.False(saved.TryGetProperty("reportDeadline", out _));
    }

    [Fact]
    public async Task 期限のキーが無い部門は既定の30分を使う()
    {
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.Departments,
            """
            {"revision":1,"departments":[{"id":"design","displayName":"設計","responsibility":"設計する","agent":"ClaudeCode","mode":"Structured"}]}
            """);

        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        var department = Assert.Single(read.Definition.Departments);
        Assert.Null(department.ReportDeadlineMinutes);
        Assert.Equal(TimeSpan.FromMinutes(30), DepartmentDefinition.DefaultReportDeadline);
        Assert.Equal(DepartmentDefinition.DefaultReportDeadline, department.ReportDeadline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ゼロ以下の期限も保存して読み直せて期限を見ない(int minutes)
    {
        var definition = Departments()[0] with { ReportDeadlineMinutes = minutes };
        Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new(0, []), [definition], CancellationToken.None));

        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        var department = Assert.Single(read.Definition.Departments);
        Assert.Equal(minutes, department.ReportDeadlineMinutes);
        Assert.Null(department.ReportDeadline);
    }

    [Fact]
    public void 既定の部門は報告期限を30分と明示する()
    {
        var departments = DepartmentStore.CreateDefaultDepartments();
        Assert.Equal(9, departments.Count);
        Assert.All(departments, department => Assert.Equal(30, department.ReportDeadlineMinutes));
    }

    private static IReadOnlyList<DepartmentDefinition> Departments(string id = "design") =>
        [new(id, "設計", "設計する", AgentKind.ClaudeCode, DriveMode.Structured)];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
    [Fact]
    public async Task 廃止したキーが残っていても読めるが黙って無視される()
    {
        // **これは「いまの形の弱点」を固定するテストであって、望ましい姿ではない**（設計 §32-10）。
        // JSON は未知のキーを既定で捨てるので、**人間が書いた `autoApproveAllTools` は
        // 読み流される** —— §30-6 で潰したはずの「設定したのに黙って無視される」が、
        // **廃止した側から**戻ってくる。
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.Departments)!);
        await File.WriteAllTextAsync(_paths.Departments,
            """
            {
              "revision": 1,
              "departments": [
                {
                  "id": "research",
                  "displayName": "調査",
                  "responsibility": "調べる",
                  "agent": "AntigravityCli",
                  "mode": "Structured",
                  "autoApproveAllTools": true
                }
              ]
            }
            """);

        var read = Assert.IsType<DefinitionReadResult.Found>(await _store.ReadAsync(CancellationToken.None));
        var department = Assert.Single(read.Definition.Departments);

        // 読めてしまう。**エラーにならない。**
        Assert.Equal("research", department.Id);

        // そして古い駆動モードのまま残る —— agy は Structured では
        // 権限を人間に聞けない（§30-1 の実測）。**今朝これを実機で踏んだ。**
        Assert.Equal(DriveMode.Structured, department.Mode);
    }

    [Fact]
    public async Task 読まなかったキーを挙げる()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.Departments)!);
        await File.WriteAllTextAsync(_paths.Departments,
            """
            {
              "revision": 1,
              "departments": [
                {
                  "id": "research", "displayName": "調査", "responsibility": "調べる",
                  "agent": "AntigravityCli", "mode": "Structured",
                  "autoApproveAllTools": true, "むかしのメモ": "何か"
                }
              ]
            }
            """);

        var unread = await _store.FindUnreadKeysAsync(CancellationToken.None);

        Assert.Contains("research: autoApproveAllTools", unread);
        Assert.Contains("research: むかしのメモ", unread);
    }

    [Fact]
    public async Task 既定の部門には読まなかったキーが無い()
    {
        // **`KnownKeys` を直し忘れると、読んでいるキーを「読んでいない」と言う。**
        // 既定の部門を往復させて見張る（DepartmentDefinition を足したら、ここが落ちる）。
        Assert.IsType<DefinitionWriteResult.Written>(await _store.SaveAsync(
            new CompanyDefinition(0, []), DepartmentStore.CreateDefaultDepartments(), CancellationToken.None));

        Assert.Empty(await _store.FindUnreadKeysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Antigravity_に思考の強さを設定させない()
    {
        // **実機で踏んだ**（設計 §47-2）。あちらは強さがモデル名に畳まれていて、
        // `--model gemini-3.8-flash-high --effort low` は
        // **`conflicts with --effort=low` で起動しない。**
        var broken = new DepartmentDefinition(
            "research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.ExternalTerminal,
            Model: "gemini-3.8-flash-high", ReasoningEffort: "low");

        var rejected = Assert.IsType<DefinitionWriteResult.Rejected>(
            await _store.SaveAsync(new CompanyDefinition(0, []), [broken], CancellationToken.None));
        Assert.Contains("思考の強さ", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Antigravity_でもモデルだけなら保存できる()
    {
        var fine = new DepartmentDefinition(
            "research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.ExternalTerminal,
            Model: "gemini-3.8-flash-high");

        Assert.IsType<DefinitionWriteResult.Written>(
            await _store.SaveAsync(new CompanyDefinition(0, []), [fine], CancellationToken.None));
    }

    [Fact]
    public void 権限を人間に聞けない組み合わせを見つける()
    {
        // **2026-09-09 に実機で踏んだ**（§30-1 の再現）。
        var broken = new DepartmentDefinition(
            "research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.Structured);
        var fine = new DepartmentDefinition(
            "research2", "調査2", "調べる", AgentKind.AntigravityCli, DriveMode.ExternalTerminal);

        // 承認の往復を持つ CLI は Structured でも聞ける（§13-1 / §13-2）。
        var claude = new DepartmentDefinition(
            "design", "設計", "設計する", AgentKind.ClaudeCode, DriveMode.Structured);

        Assert.True(DepartmentWarnings.CannotAskHuman(broken));
        Assert.False(DepartmentWarnings.CannotAskHuman(fine));
        Assert.False(DepartmentWarnings.CannotAskHuman(claude));

        var warning = Assert.Single(DepartmentWarnings.For([broken, fine, claude]));
        Assert.Equal("research", warning.DepartmentId);
        Assert.Contains("ExternalTerminal", warning.Message);
    }

    [Fact]
    public void 既定の部門は誰も権限で詰まらない()
    {
        Assert.Empty(DepartmentWarnings.For(DepartmentStore.CreateDefaultDepartments()));
    }

    [Fact]
    public void 既定の部門はすべて同じ駆動モードになる()
    {
        // **AI ごとに分けない**（設計 §32-3、2026-09-09）。
        // ブリーフ #3 が「部門は全部ターミナル、例外は秘書だけ」と最初から書いている。
        // ここが割れると、人間が「この部門は聞いてくるが、あの部門は聞いてこない」を
        // 覚える羽目になる。
        var departments = DepartmentStore.CreateDefaultDepartments();

        Assert.Equal(9, departments.Count);
        Assert.All(departments, department => Assert.Equal(DriveMode.ExternalTerminal, department.Mode));
    }

    [Fact]
    public void 設計レビューとデザイナーと監査が読むだけの部門()
    {
        // **書き込み権を取るかどうかが変わる**（設計 §29-1）。
        // 安全側の既定は「取る」なので、読むだけと宣言したものだけがここに出る。
        var readsOnly = DepartmentStore.CreateDefaultDepartments()
            .Where(department => department.ReadsOnly)
            .Select(department => department.Id)
            .ToArray();

        Assert.Equal(["design-review-consistency", "design-review-outside", "designer", "audit"], readsOnly);
    }

    [Fact]
    public void デザイナーには画像の置き場所と報告の約束を渡す()
    {
        // **責務は秘書が振り分ける根拠にもなる**（設計 §56-2 / §56-3）。
        var designer = Assert.Single(DepartmentStore.CreateDefaultDepartments(), department => department.Id == "designer");
        Assert.Equal("デザイナー", designer.DisplayName);
        Assert.Equal(AgentKind.CodexCli, designer.Agent);
        Assert.Equal(AgentCapabilities.For(AgentKind.CodexCli).DefaultDriveMode, designer.Mode);
        Assert.True(designer.ReadsOnly);
        Assert.Equal(30, designer.ReportDeadlineMinutes);
        Assert.Contains("画像生成", designer.Responsibility);
        Assert.Contains("instruction.md と同じ場所", designer.Responsibility);
        Assert.Contains("images/", designer.Responsibility);
        Assert.Contains("空白を入れず", designer.Responsibility);
        Assert.Contains("report.md", designer.Responsibility);
        Assert.Contains("ワークスペースからの相対パス .company/tasks/<slug>/images/<name>.png", designer.Responsibility);
    }

    [Fact]
    public void 監査はAntigravityで個人情報とライセンスと著作権を見て直さない()
    {
        // 設計 §59。git の操作は人間の仕事のまま —— 監査は見て報告するだけ。
        var audit = Assert.Single(DepartmentStore.CreateDefaultDepartments(), department => department.Id == DepartmentStore.AuditDepartmentId);
        Assert.Equal("監査", audit.DisplayName);
        Assert.Equal(AgentKind.AntigravityCli, audit.Agent);
        Assert.Equal(AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode, audit.Mode);
        Assert.True(audit.ReadsOnly);
        Assert.Equal(30, audit.ReportDeadlineMinutes);
        foreach (var expected in new[] { "個人情報", "ライセンス", "著作権", "git diff HEAD", "@{u}..", "直さない", "report.md" })
        {
            Assert.Contains(expected, audit.Responsibility);
        }
    }

    [Fact]
    public void 設計レビューの2部門は違う問いを持つ()
    {
        // 同じ問いを2人に投げると、費用は2倍で発見はほとんど増えない（§29-2）。
        var paths = new CompanyPaths(_root);
        var consistency = CompanyInstruction.ComposeDesignReview(
            "docs/x.md", DesignReviewLens.Consistency, paths, "task-1");
        var outside = CompanyInstruction.ComposeDesignReview(
            "docs/x.md", DesignReviewLens.Outside, paths, "task-1");

        Assert.Contains("矛盾している", consistency, StringComparison.Ordinal);
        Assert.Contains("使う人が、何に困るか", outside, StringComparison.Ordinal);
        Assert.NotEqual(consistency, outside);

        // どちらも「作業ツリーは書き換えない」と言う（読むだけの部門なので）。
        Assert.All([consistency, outside],
            text => Assert.Contains("作業ツリーのファイルを書き換えない", text, StringComparison.Ordinal));

        // **報告まで禁じない**（レビューで発覚）。禁じると、忠実な CLI は report.md も
        // 書かずに正常終了し、仕事が Dispatched のまま止まる。
        Assert.All([consistency, outside],
            text => Assert.Contains("report.md", text, StringComparison.Ordinal));
    }

    [Fact]
    public void 危険モードは廃止されたので設定として残っていない()
    {
        // **§30-4 の抜け道は 2026-09-09 に消した**（設計 §32-3）。
        // 外部ターミナルという「人間に聞く手段」ができたので要らない ——
        // **聞けるのに聞かない、を残さない。**
        //
        // 消したものが復活しないように、**書き出しに綴りが現れないこと**で見張る。
        var json = System.Text.Json.JsonSerializer.Serialize(
            new CompanyDefinition(0, DepartmentStore.CreateDefaultDepartments()),
            TaskStateJson.Options);

        Assert.DoesNotContain("autoApproveAllTools", json);
        Assert.DoesNotContain("runsWithAllToolsApproved", json);
        Assert.DoesNotContain("dangerousModeApplies", json);
    }

    [Fact]
    public void 既定の部門はすべて外部ターミナルで動く()
    {
        // **AI ごとに分けない**（設計 §32-3）。ブリーフ #3 が
        // 「部門は全部ターミナル、例外は秘書だけ」と最初から書いている。
        Assert.All(
            DepartmentStore.CreateDefaultDepartments(),
            d => Assert.Equal(DriveMode.ExternalTerminal, d.Mode));
    }
}
