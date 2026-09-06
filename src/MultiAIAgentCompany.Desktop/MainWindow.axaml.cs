using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace MultiAIAgentCompany.Desktop;

public partial class MainWindow : Window
{
    private readonly ShellComposer? _composer;
    private readonly DepartmentRunner? _runner;
    private DepartmentTile? _selected;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public MainWindow(ShellComposer composer, DepartmentRunner runner) : this()
    {
        _composer = composer;
        _runner = runner;
        DataContext = composer.Shell;
    }

    /// <summary>
    /// 部門パネルを押す。設計 §1 —— <b>押すとその部門のセッションを開く</b>。
    /// </summary>
    private async void OnDepartmentClick(object? sender, RoutedEventArgs e)
    {
        if (_composer is null || _runner is null
            || (sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);

        if (_composer.Workspace is not { } workspace)
        {
            Note("先にワークスペースを選ぶ（部門は選んだフォルダで動く）");
            return;
        }

        if (_runner.IsRunning(tile.Id))
        {
            return;
        }

        var failure = await _runner.StartAsync(_composer.DefinitionOf(tile.Id), workspace, CancellationToken.None);
        Note(failure ?? $"{tile.Name} を起動した");
    }

    /// <summary>
    /// 選んだ部門へ直接1メッセージ送る。
    /// <b>これは調整基盤の経路ではない</b>（§6 / §14-1）—— 配線を実物で確かめるための直通路。
    /// </summary>
    private async void OnSendToDepartment(object? sender, RoutedEventArgs e)
    {
        if (_runner is null || _selected is null
            || this.FindControl<TextBox>("MessageBox") is not { } box
            || string.IsNullOrWhiteSpace(box.Text))
        {
            return;
        }

        var text = box.Text;
        box.Text = string.Empty;
        Note($"{_selected.Name} へ送った: {text}");
        await _runner.SendAsync(_selected.Id, text, CancellationToken.None);
    }

    private void Select(DepartmentTile tile)
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = tile;
        tile.IsSelected = true;
    }

    private void Note(string line)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.WorkLog.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {line}");
        }
    }

    /// <summary>
    /// ワークスペースを選ぶ。選んだら各 CLI の trust を読み直す（設計 §13-9）。
    /// <b>アプリは trust を書かない</b> —— 読むだけ。
    /// </summary>
    private async void OnPickWorkspace(object? sender, RoutedEventArgs e)
    {
        if (_composer is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "AI たちが働くフォルダを選ぶ",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        await _composer.SelectWorkspaceAsync(path, CancellationToken.None);
    }
}
