# icon-slicer

キャラクターシート1枚から、部門アイコン（設計 §15）を作る道具。

**本体とは独立に動く**（`spikes/Directory.Build.props` が壁）。ソリューションにも入れていない。

```bash
# 1. シートを塊ごとに切り出す（格子では切らない —— ノートPCや煙がセルからはみ出す）
dotnet run -- slice ~/Downloads/sheet.png out/raw

# 2. 倍率を揃える（**頭の大きさ**で揃える。外接矩形で揃えると、座りポーズの頭が小さくなる）
dotnet run -- normalize out/raw ../../src/MultiAIAgentCompany.Desktop/Assets/poses

# 3. 実寸（40px / 24px、ライト・ダーク）で並べて見る —— 設計 §15-5 の判定
dotnet run -- sheet ../../src/MultiAIAgentCompany.Desktop/Assets/poses out/check.png
```

**倍率は手で決めてある**（`Program.cs` の `Poses`）。頭の幅の自動検出は、
ノートPC や煙を頭と数えて外した —— 6枚しかないので、目で見て決める方が早くて確かである。

アプリの中にも同じ確かめがある（**表示 → アイコンの一覧を見る**）。
**そちらが正本** —— 外のシートはビルドと一緒に古くなる。
