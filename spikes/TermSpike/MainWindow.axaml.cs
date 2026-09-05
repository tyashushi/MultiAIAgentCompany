using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;

namespace TermSpike;

// Codex の実験① : 「入力できない」の原因が本当にターミナルなのかを判定する。
//   - Window のネイティブ活性状態(IsActive) を記録する
//   - すべての KeyDown / TextInput を handledEventsToo で拾い、Source と Handled を記録する
//   - 素の Enter による送信はやめる（IME 確定と衝突するため。送信はボタンか Cmd+Enter）
public partial class MainWindow : Window
{
    private TerminalControl _term = null!;
    private TextBox _input = null!;
    private TextBlock _hint = null!;
    private readonly string _res = Environment.GetEnvironmentVariable("SPIKE_RES") ?? "/tmp/termspike-b";
    private readonly bool _noTerm = Environment.GetEnvironmentVariable("SPIKE_NO_TERMINAL") == "1";

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _term = this.FindControl<TerminalControl>("Term")!;
        _input = this.FindControl<TextBox>("Input")!;
        _hint = this.FindControl<TextBlock>("Hint")!;
        Directory.CreateDirectory(_res);
        Opened += OnOpened;
    }

    private void Log(string s) =>
        File.AppendAllText(Path.Combine(_res, "b.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n");

    private static string Describe(string t) =>
        t.Replace("\r", "\\r").Replace("\n", "\\n")
        + " [U+" + string.Join(" U+", t.Select(c => ((int)c).ToString("X4"))) + "]";

    private string Who()
    {
        var f = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return $"IsActive={IsActive} focus={f?.GetType().Name ?? "-"} inputFocused={_input.IsFocused}";
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Log($"起動 / SPIKE_NO_TERMINAL={_noTerm}");

        // 観測: すべてのキー入力を、処理済みのものも含めて拾う
        AddHandler(InputElement.KeyDownEvent, (object? s, KeyEventArgs a) =>
            Log($"KeyDown key={a.Key} sym={a.KeySymbol ?? "-"} src={a.Source?.GetType().Name} handled={a.Handled} | {Who()}"),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        AddHandler(InputElement.TextInputEvent, (object? s, TextInputEventArgs a) =>
            Log($"TextInput {Describe(a.Text ?? "")} src={a.Source?.GetType().Name} handled={a.Handled} | {Who()}"),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // PTY へ実際に出て行くバイト列を全部記録する。
        // 実キーの Enter が何を送っているかを、これで観測する。
        _term.InputSent += (_, sent) =>
        {
            var hex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(sent));
            var vis = sent.Replace("\u001b", "<ESC>").Replace("\r", "<CR>").Replace("\n", "<LF>");
            Log($"PTY<< {hex}   \"{vis}\"");
        };

        // claude が端末に要求しているモードを観測する。
        // Kitty keyboard protocol (CSI > flags u / CSI = flags ; mode u) を有効化しているか、
        // bracketed paste(2004) / focus(1004) / mouse(1000,1006) を入れているかを見る。
        _term.OutputReceived += (_, oe) =>
        {
            var text = oe.Output ?? "";
            foreach (System.Text.RegularExpressions.Match mm in
                     System.Text.RegularExpressions.Regex.Matches(
                         text, "\u001b\\[[>=?]?[0-9;]*[uhl]"))
            {
                var v = mm.Value.Replace("\u001b", "<ESC>");
                Log($"PTY>> モード要求 {v}");
            }
        };

        Activated += (_, _) => { Log("Window Activated | " + Who()); _input.Focus(); UpdateHint(); };
        Deactivated += (_, _) => Log("Window Deactivated | " + Who());

        _input.GotFocus += (_, _) => { Log("入力欄 GotFocus | " + Who()); UpdateHint(); };
        _input.LostFocus += (_, _) => { Log("入力欄 LostFocus | " + Who()); UpdateHint(); };
        _input.TextChanged += (_, _) => Log("入力欄の中身: " + Describe(_input.Text ?? ""));

        this.FindControl<Button>("SendBtn")!.Click += (_, _) => Send("ボタン");
        this.FindControl<Button>("FocusBtn")!.Click += (_, _) =>
        {
            SetTerminalFocusable(true);
            _term.Focus();
            Log("ターミナルへフォーカスを渡した | " + Who());
        };

        // 実測(2026-09-05)で、IME の変換確定 Enter は KeyDown として届かないことを確認した。
        // 届く Return は確定後にユーザーが改めて押したものだけなので、素の Enter を送信に使える。
        _input.AddHandler(InputElement.KeyDownEvent, (object? s, KeyEventArgs a) =>
        {
            if (a.Key is not (Key.Enter or Key.Return)) return;
            a.Handled = true;
            Send(a.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "Cmd+Enter" : "Enter");
        }, RoutingStrategies.Bubble);

        if (_noTerm)
        {
            _term.IsVisible = false;
            Log("ターミナルを配置しない（比較用）");
        }
        else
        {
            _term.LaunchProcess(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                "/bin/zsh", "-l");
            Log("zsh を起動");
        }

        // 内側の TerminalView まで含めてフォーカス禁止にする（外側だけでは効かない）
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            SetTerminalFocusable(false);
            Activate();              // ウィンドウを OS レベルで前面に出す
            _input.Focus();
            Log("初期化おわり | " + Who());
            UpdateHint();
        }, TimeSpan.FromMilliseconds(1200));
    }

    private void SetTerminalFocusable(bool value)
    {
        _term.Focusable = value;
        var view = _term.GetVisualDescendants().FirstOrDefault(v => v.GetType().Name == "TerminalView");
        if (view is InputElement ie) { ie.Focusable = value; Log($"TerminalView.Focusable={value}"); }
        else Log("TerminalView が見つからない");
    }

    private void UpdateHint() =>
        _hint.Text = (_input.IsFocused ? "入力欄にフォーカスあり" : "入力欄にフォーカス無し")
                     + $" / ウィンドウ活性={IsActive} / 送信はボタンか Cmd+Enter";

    private async void Send(string how)
    {
        var text = _input.Text ?? "";
        if (text.Length == 0) { Log($"{how}: 空"); return; }
        Log($"{how} で送信: {Describe(text)}");
        _input.Text = "";
        // TUI は「短時間にまとめて届いた入力」を貼り付けと見なし、その中の改行を
        // 送信ではなく改行として扱う。人が打つのと同じく、本文と Enter を分けて送る。
        var delay = int.TryParse(Environment.GetEnvironmentVariable("SPIKE_ENTER_DELAY_MS"), out var d) ? d : 150;
        try
        {
            await _term.SendInputAsync(text, CancellationToken.None);
            await Task.Delay(delay);
            var seqHex = Environment.GetEnvironmentVariable("SPIKE_ENTER_SEQ");
            var seq = string.IsNullOrEmpty(seqHex)
                ? "\r"
                : System.Text.Encoding.UTF8.GetString(Convert.FromHexString(seqHex));
            await _term.SendInputAsync(seq, CancellationToken.None);
            Log($"本文を送信 → {delay}ms 後に Enter 列 [{seqHex ?? "0D(既定)"}] を単独送信");
        }
        catch (Exception ex) { Log("送信失敗: " + ex.Message); }
    }
}
