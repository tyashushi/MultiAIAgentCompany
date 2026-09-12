# MultiAIAgentCompany 🏢

> **人間は「社長」、対話は「秘書」ただ1人。**  
> 複数の AI コーディングエージェント（Claude Code / Codex CLI / Antigravity CLI）を「仮想企業」として統括し、自律的に連携・分業させるデスクトップオーケストレーター。

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia-12.0.4-8B5CF6?logo=avalonia)](https://avaloniaui.net/)
[![Platform](https://img.shields.io/badge/Platform-macOS-000000?logo=apple)](https://www.apple.com/macos/)
[![Tests](https://img.shields.io/badge/Tests-1550%2B%20Passing-success)](#)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## 🌟 概要 (Overview)

**MultiAIAgentCompany** は、主要な対話型 AI コーディングエージェントを「会社の各専門部門」に見立てて協調動作させる macOS デスクトップアプリケーションです。

人間がすべてのエージェントと個別にチャットするのではなく、**「秘書（Claude Code）」** に要件を伝えるだけで、秘書がタスクを計画・分解し、各専門部門（設計・実装・調査・レビュー・テスト）へと自律的に仕事をパスします。

人間は現場監督としてチャットを中継する「伝書鳩」から解放され、**要所での意思決定（承認・質問への回答・成果物の合否判定）を行う「社長」** として振る舞うことができます。

```
                    ┌─────────────────────────┐
                    │      人間 (社長)        │
                    └───────────┬─────────────┘
                                │ (相談 / 最終意思決定)
                                ▼
                    ┌─────────────────────────┐
                    │    秘書 (Claude Code)    │
                    └───────────┬─────────────┘
                                │ (タスク計画 / バトンパス)
      ┌────────────────┬────────┴────────┬────────────────┐
      ▼                ▼                 ▼                ▼
┌───────────┐    ┌───────────┐     ┌───────────┐    ┌───────────┐
│ 設計部門  │    │ 調査部門  │     │ レビュー  │    │ 実装部門  │
│(Claude)   │    │(Antigravity)    │ (Codex)   │    │ (Codex)   │
└───────────┘    └───────────┘     └───────────┘    └───────────┘
```

---

## 💡 コア思想：なぜ作ったのか？ (Core Philosophy)

### 1. 「AI組織の作り方」レベル4（共有ドキュメント方式）の実装
エージェント同士の口頭伝言ゲームではなく、ワークスペース内の共有フォルダ（`.company/`）に配置された **Markdown / JSON ドキュメント（指示書、報告書、相談、状態）** を介して非同期に仕事を受け渡します。
アプリがクラッシュしても、PC を再起動しても、**すべての仕事と進捗がファイルとしてディスクに残るため、いつでも 100% 確実に復旧** できます。

### 2. 単一フォルダ共有 ＋ 書き込み権（Write Lease）
他のマルチエージェントツール（Orca 等）のように **Git worktree（フォルダ複製）を強制しません**。
Unity やゲームエンジン、巨大なモノレポなど、「1つのプロセスがフォルダを掴んでいる」「キャッシュが巨大で複製できない」現場のプロジェクトでも、**単一フォルダの書き込み権（`lease.json`）を排他的に制御する** ことで安全に共存できます。

### 3. 外部ターミナル連携による「安心の手動承認」
エージェントを無理やりヘッドレス（全自動）で動かすために危険な全自動承認（`--dangerously-skip-permissions`）を強制することを排除しました。
各部門は **OS ネイティブのターミナル（macOS Terminal.app）** で対話起動するため、CLI 本来のリッチな TUI、思考ログ、カラー差分プレビューを確認しながら、人間が自分の手で安全に `y` / Enter を押して承認できます。

---

## 🖥️ 画面構成（3ペイン UI）

| ペイン | 役割と主な機能 |
| :--- | :--- |
| **左ペイン**<br>相談スレッド一覧 | ・**スレッド履歴**: 「＋ 新しい相談」ボタンと過去の相談履歴（Claude / Antigravity アプリ同様の UX）<br>・**相談の片付け**: 不要になったスレッドを消さずに `.company/archive/` へ安全退避<br>・**復旧パネル**: 起動時に中断タスクや未読 lease を検出し、人間が再送・破棄を判断<br>・**作業ログ**: 下部に折りたたまれたシステム実行ログ（ドラッグで全行一括コピー可能） |
| **中央ペイン**<br>秘書との対話 | ・**秘書チャット**: 要件や設計の相談を入力（Enter 送信）<br>・**自律パイプライン進行**: 秘書が立てた計画（調査 → 設計 → レビュー → 実装）が自動で進行<br>・**成果物の即時表示**: 部門から `report.md` が上がると中央ペインに即時プレビュー表示 |
| **右ペイン**<br>部門ステータス一覧 | ・**部門タイル**: 3軸ステータス（稼働 / 活動 / 仕事）＋その根拠、担当モデル名、タスク件名を表示<br>・**ロボットアイコン**: 活動状態に応じた 3 頭身ロボットの 6 ポーズベクター描画<br>・**アクション**: ターミナル前面化、仕事の個別作成、報告書の「受理」または「差し戻して送り直す」 |

---

## 🚀 主な機能 (Key Features)

- **🏢 既定の専門部門セット**:
  - **設計 (`design`)**: Claude Code
  - **実装 (`implementation`)**: Codex CLI
  - **調査 (`research`)**: Antigravity CLI
  - **レビュー (`review`)**: Claude Code
  - **テスト (`testing`)**: Codex CLI
  - **設計レビュー 整合 (`design-review-consistency`)**: Codex CLI（`ReadsOnly: true`）
  - **設計レビュー 外から (`design-review-outside`)**: Antigravity CLI（`ReadsOnly: true`）
- **🔄 自律パイプライン & レビュー差し戻し往復**:
  - 秘書が作成した計画に基づき、前工程の成果物を次工程の指示書へ自動でバトンパス。
  - レビュー部門の判定（`verdict: ok / revise`）をパースし、修正が必要な場合は最大 3 回まで自動で差し戻し往復。上限に達した際は安全に人間の判断へハンドオフ。
- **⚙️ GUI 部門設定画面**:
  - アプリのメニューバーから「部門の設定」を開き、部門の追加・編集・削除、モデル名、思考強度（Reasoning Effort）を柔軟にカスタマイズ可能。
  - 各 CLI の引数差異（Claude の `--effort`、Codex の `-c model_reasoning_effort`、Antigravity のモデル名埋め込み）を自動吸収。
- **🎨 洗練されたデザインシステム**:
  - `Tokens.axaml` によるライト / ダークテーマ完全対応。
  - 日本語フォント（Inter + Hiragino Sans）の全ウィンドウ適用による文字化け防止。
  - 「人間の出番があるタイル」だけが控えめにハイライトされる設計。

---

## 🛠️ 動作要件 (Requirements)

- **OS**: macOS 14 (Sonoma) 以上（Apple Silicon / Intel 両対応）
- **ランタイム / SDK**: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（開発・ビルド時）
- **対象 AI CLI**:
  - [Claude Code](https://docs.anthropic.com/en/docs/agents-and-tools/claude-code/overview) (`claude`)
  - [Codex CLI](https://github.com/openai/codex) (`codex`)
  - [Google Antigravity CLI](https://antigravity.google) (`agy`)
  ※ 利用するエージェントが `$PATH` 上で実行可能であること。

---

## 📦 ビルドと実行 (Getting Started)

### 1. リポジトリのクローン
```bash
git clone https://github.com/<your-account>/MultiAIAgentCompany.git
cd MultiAIAgentCompany
```

### 2. ビルド
```bash
dotnet build MultiAIAgentCompany.slnx
```

### 3. テストの実行（1,550+ 件）
```bash
dotnet test MultiAIAgentCompany.slnx
```

### 4. アプリの起動
```bash
dotnet run --project src/MultiAIAgentCompany.Desktop/MultiAIAgentCompany.Desktop.csproj
```

---

## 📖 基本的な使い方 (Workflow)

1. **フォルダを選ぶ**:
   アプリ右上の「フォルダを選ぶ」または `Cmd+O` で、作業対象のリポジトリ（ワークスペース）を選択します。
2. **秘書に相談する**:
   中央ペインの入力欄から秘書に話しかけます（例: `「ユーザー認証のAPIを設計して実装まで進めて」`）。
3. **計画の自動進行**:
   秘書が計画を立てると、自動的に最初の部門（調査や設計）へタスクが発行され、Mac の `Terminal.app` が立ち上がります。
4. **ターミナルでの確認と承認**:
   ターミナル上でエージェントの思考ログやツールの承認プロンプトを確認し、`y` / Enter で承認します。
5. **報告の受理と次の工程へ**:
   作業が完了して `report.md` が出力されると、アプリの中央ペインに報告書が即時表示されます。レビューが通れば次の工程（実装やテスト）へ自動で進みます。

---

## 📁 調整基盤のディレクトリ構造 (.company/)

ワークスペース直下の `.company/` ディレクトリで全状態を永続管理します：

```text
<ワークスペース>/.company/
  departments.json       # 部門定義（担当CLI・モデル・思考強度など）
  lease.json             # ワークスペース書き込み権（排他制御）
  secretary/             # 秘書用領域
    threads/             # 会話スレッド履歴（meta.json, transcript.jsonl）
    outbox/              # 秘書が発行した提案
  tasks/<task-slug>/     # タスクごとの共有ドキュメント
    instruction.md       # 指示書
    report.md            # 報告書（成果物）
    question.md          # 仕様確認・判断の相談（AI → 人間）
    answer.md            # 人間の回答
    rejection.md         # 差し戻し理由
    state.json           # タスク状態、世代(attempt)、Revision
  archive/               # 片付けられた相談スレッドや完了タスクの退避先
```

---

## 🗺️ 今後の展望 (Roadmap)

- [ ] **Windows Terminal (`wt.exe`) 対応**: Windows 環境でのネイティブ外部ターミナル連携
- [ ] **自律パイプラインの進捗インジケーター**: 全体工程のビジュアルプログレスバー表示
- [ ] **Git コミット / 差分プレビュー連携**: タスク受理時の自動コミットとアプリ内 diff 表示
- [ ] **Slack 連携 (Socket Mode)**: 出先やスマホから報告書の承認・差し戻しができる「モバイル社長」機能

---

## 📄 ライセンス (License)

本プロジェクトは [MIT License](LICENSE) のもとで公開されています。
