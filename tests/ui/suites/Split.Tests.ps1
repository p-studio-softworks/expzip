# ファイルの分割 (#59) と、受け取った側で動く結合用のプログラム (#61, #106, #160)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Split'
Set-Settings

$source = Join-Path $script:Work '資料.bin'
$output = Join-Path $script:Work 'parts'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$bytes = New-Object byte[] (3MB + 12345)
(New-Object System.Random 7).NextBytes($bytes)
[System.IO.File]::WriteAllBytes($source, $bytes)
$hash = (Get-FileHash $source -Algorithm SHA256).Hash

Section '分割の窓'
$app = Start-Expzip
Push (ById $app.Window 'SplitButton')
$dialog = Find-Window $app 'ファイルの分割'
Check '窓が開く' ($null -ne $dialog)

Set-Text (ById $dialog 'SourceBox') ''
Set-Text (ById $dialog 'DestinationBox') ''
Check '対象が無いとき' ((ById $dialog 'PreviewText').Current.Name -eq '分割するファイルを選択してください。') (ById $dialog 'PreviewText').Current.Name
Set-Text (ById $dialog 'SourceBox') $source
Check '保存先が無いとき' ((ById $dialog 'PreviewText').Current.Name -eq '分割ファイルの保存先を選択してください。') (ById $dialog 'PreviewText').Current.Name
Set-Text (ById $dialog 'DestinationBox') $output

$unit = ById $dialog 'UnitCombo'
Expand-Element $unit
Select-Element (ByName $unit 'MB')
Set-Text (ById $dialog 'SizeBox') '10'
Check '分ける必要が無いとき' ((ById $dialog 'PreviewText').Current.Name -eq '分割の必要はありません。1つの大きさが分割するファイルより大きいです。') (ById $dialog 'PreviewText').Current.Name
Set-Text (ById $dialog 'SizeBox') '1'
$preview = (ById $dialog 'PreviewText').Current.Name
Check '何ができるかを言う' ($preview -eq '4 個の分割ファイルと、結合用のプログラムを作成します。元のファイルはそのまま残ります。') $preview
Check '口の名前' ((ById $dialog 'SplitButton').Current.Name -eq '分割' -and (ById $dialog 'CancelButton').Current.Name -eq 'キャンセル')

# 結合用のバッチファイル (#343)。デフォルトは作らない。名前に % があると押せない
$batchCheck = ById $dialog 'BatchCheck'
Check 'バッチファイルの欄の名前' ($batchCheck.Current.Name -eq '結合用のバッチファイルも作成する') $batchCheck.Current.Name
Check 'バッチファイルはデフォルトで作らない' (-not (IsChecked $batchCheck))
$note = (ById $dialog 'BatchNote').Current.Name
Check 'バッチファイルの説明' ($note -eq '受け取った人の PC で、結合用のプログラムが Windows に止められて実行できないときに使います。') $note
Set-Text (ById $dialog 'SourceBox') (Join-Path $script:Work 'a %OS% b.bin')
Check '名前に % があると押せない' (-not $batchCheck.Current.IsEnabled)
$note = (ById $dialog 'BatchNote').Current.Name
Check '押せない理由' ($note -eq 'ファイル名に % があるため、バッチファイルは作成できません。') $note
Set-Text (ById $dialog 'SourceBox') $source
Check '名前を戻すと押せる' ($batchCheck.Current.IsEnabled -and -not (IsChecked $batchCheck))

Section '分割する'
Push (ById $dialog 'SplitButton')
$box = Find-MessageBox $app 30000
Check '終わったと知らせる' ($null -ne $box)
if ($box) {
    Check '件数' ($box.Text -match '^4 個に分割しました。') $box.Text
    Check '保存先' ($box.Text -match [regex]::Escape("保存先: $output"))
    Check '戻し方' ($box.Text -match 'すべての分割ファイルを同じフォルダーに置いて「資料\.bin\.exe」を実行してください。')
    Check 'ほかのソフトでも結合できる' ($box.Text -match '7-Zip などほかのソフトでも結合できます。') $box.Text
    Check 'バッチファイルを作らなければ言わない' ($box.Text -notmatch '\.cmd') $box.Text
    Close-MessageBox $box 'OK'
}
Check 'ステータスバー' ((Get-Status $app) -eq '4 個に分割しました') (Get-Status $app)

Section 'バッチファイルも作る'
$batchOutput = Join-Path $script:Work 'parts-batch'
New-Item -ItemType Directory -Force -Path $batchOutput | Out-Null
Push (ById $app.Window 'SplitButton')
$dialog = Find-Window $app 'ファイルの分割'
Set-Text (ById $dialog 'SourceBox') $source
Set-Text (ById $dialog 'DestinationBox') $batchOutput
Toggle-Check (ById $dialog 'BatchCheck')
Push (ById $dialog 'SplitButton')
$box = Find-MessageBox $app 30000
if ($box) {
    Check 'バッチファイルのことを言う' ($box.Text -match 'Windows に止められて実行できないときは、「資料\.bin\.cmd」を実行してください。') $box.Text
    Close-MessageBox $box 'OK'
}
Check 'バッチファイルができる' (Test-Path (Join-Path $batchOutput '資料.bin.cmd'))
Check '選んだことを覚える' ((Read-Settings) -match '"SplitMakeBatch":\s*true') (Read-Settings)
Stop-Expzip $app

$batch = Start-Process cmd.exe -ArgumentList '/d', '/c', "`"`"$(Join-Path $batchOutput '資料.bin.cmd')`" -q`"" -WindowStyle Hidden -PassThru
Check 'バッチファイルが終わる' ($batch.WaitForExit(60000))
Check 'バッチファイルが成功を返す' ($batch.ExitCode -eq 0) $batch.ExitCode
$joinedByBatch = Join-Path $batchOutput '資料.bin'
Check 'バッチファイルで元と同じファイルができる' ((Test-Path $joinedByBatch) -and (Get-FileHash $joinedByBatch -Algorithm SHA256).Hash -eq $hash)

$files = @(Get-ChildItem $output | ForEach-Object { $_.Name } | Sort-Object)
Check '分割ファイルと結合用のプログラムができる' ($files.Count -eq 5 -and $files -contains '資料.bin.exe') ($files -join ', ')
Check '元のファイルは残る' ((Get-FileHash $source -Algorithm SHA256).Hash -eq $hash)

Section '結合用のプログラム'
$joiner = Start-Process (Join-Path $output '資料.bin.exe') -WorkingDirectory $output -PassThru
$message = Read-NativeMessage $joiner
Check '知らせが出る' ($null -ne $message)
if ($message) {
    Check '見出し' ($message.Title -eq 'ファイルの結合') $message.Title
    Check '文言' ($message.Text -eq '結合が完了し、元のファイルと一致することを確認しました。') $message.Text
}
Check '結合用のプログラムが終わる' ($joiner.WaitForExit(10000))
$joined = Join-Path $output '資料.bin'
Check '元と同じファイルができる' ((Test-Path $joined) -and (Get-FileHash $joined -Algorithm SHA256).Hash -eq $hash)

Section 'もう一度実行したとき'
$joiner = Start-Process (Join-Path $output '資料.bin.exe') -WorkingDirectory $output -PassThru
$message = Read-NativeMessage $joiner
Check '同じ名前があると言う' ($message -and $message.Text -eq 'ファイルを結合できませんでした。既に同じ名前のファイルが存在する可能性があります。') $(if ($message) { $message.Text })
$joiner.WaitForExit(10000) | Out-Null
if (-not $joiner.HasExited) { $joiner.Kill() }
Check '元のファイルを壊さない' ((Get-FileHash $joined -Algorithm SHA256).Hash -eq $hash)

Complete-Suite
