# Expzip で作業するときの決まり

GitHub Copilot に向けた案内です。決まりの全体は [`.claude/CLAUDE.md`](../.claude/CLAUDE.md) にあります。**作業を始める前に読んでください。**
仕様書は [`docs/SPEC.md`](../docs/SPEC.md) です。

## ビルド

- **Windows で、リポジトリの直下の `build.cmd` を実行してください。`dotnet build` や `dotnet publish` を直接使わないでください。**
  - 配布用の `Expzip.exe` は、`build.cmd` を実行すると `publish\win-x64\Expzip.exe` に 1 つだけできます (約 66 MB)。この 1 つで動きます
  - `dotnet build` でできる `Expzip.dll` や小さな `Expzip.exe` (`bin` の下) は開発用です。単独では動きません。できあがりとして扱わないでください
  - 開発用にビルドするだけなら `build.cmd -Debug`、画面のテストまで走らせるなら `build.cmd -Test`
- **コミットしていない変更があるままビルドしたものを、配布しないでください。** バージョン情報に「変更あり」と出て、あとから同じソースを取り出せなくなります
- 画面のテスト (`tests/ui/Run-UiTests.ps1`) は Expzip を実際に動かします。走っている間は、マウスとキーボードに触らないよう利用者に伝えてください

## やりとり

- 利用者とは日本語で話してください。コミットのメッセージや文書も日本語で書きます
