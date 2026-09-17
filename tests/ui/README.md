# 画面越しの確認

Expzip を実際に起動し、UI Automation で押したり読んだりして確かめます。
画面に出る文言もここで確かめています。

## 走らせ方

```powershell
.\tests\ui\Run-UiTests.ps1                 # ビルドして全部
.\tests\ui\Run-UiTests.ps1 Edit Password   # 一部だけ
.\tests\ui\Run-UiTests.ps1 -NoBuild Browse # ビルドを省く
```

- **走っている間はマウスとキーボードに触らないでください。**窓を前に出し、キーを送って操作します
- Windows の表示言語が日本語の環境を前提にしています (MessageBox の「はい(Y)」など)
- 結果は `%TEMP%\ExpzipUiTests\results` に 1 本ずつ残ります

## 仕組み

- `Run-UiTests.ps1` が Expzip を `%TEMP%\ExpzipUiTests\app` へビルドし、`suites\*.Tests.ps1` を 1 本ずつ別のプロセスで走らせます。
  同じプロセスで続けると Add-Type がぶつかり、前の窓が前面を奪うためです
- 設定とルールのファイルは exe の隣に置かれます。確認ごとに消して作り直すので、**普段使っている Expzip や `publish\` には触りません**
- 検体の書庫は確認の中でその都度作ります。外のファイルには頼りません
- AI の接続先は、この PC の中に立てた偽物です (`Start-AiStub`)。外へは何も送りません
- 共通の道具は `Common.ps1` にあります。1 項目の結果は `Check '名前' (条件) 詳細` で出します

## 書くときの注意

- ファイルは BOM 付き UTF-8 にしてください。PowerShell 5.1 は BOM が無いと日本語を読み違えます
- MessageBox の口は `Close-MessageBox` で押してください。UI Automation の Invoke は閉じ終わるまで戻らず固まることがあります
- 前の結果が残っている欄を待つときは、字が変わるまで待ってください (`AiSettings.Tests.ps1` の `Run-Test`)
