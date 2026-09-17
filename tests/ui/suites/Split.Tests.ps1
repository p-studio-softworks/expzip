# ファイルの分割 (#59) と、受け取った側で動く連結プログラム (#61, #106)
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
Check '分ける必要が無いとき' ((ById $dialog 'PreviewText').Current.Name -eq '分割の必要はありません。1つの大きさが元のファイルより大きくなっています。') (ById $dialog 'PreviewText').Current.Name
Set-Text (ById $dialog 'SizeBox') '1'
$preview = (ById $dialog 'PreviewText').Current.Name
Check '何ができるかを言う' ($preview -eq '4 個の分割ファイルと、つなぎ直すためのプログラムを作成します。元のファイルはそのまま残ります。') $preview
Check '口の名前' ((ById $dialog 'SplitButton').Current.Name -eq '分割' -and (ById $dialog 'CancelButton').Current.Name -eq 'キャンセル')

Section '分割する'
Push (ById $dialog 'SplitButton')
$box = Find-MessageBox $app 30000
Check '終わったと知らせる' ($null -ne $box)
if ($box) {
    Check '件数' ($box.Text -match '^4 個に分割しました。') $box.Text
    Check '保存先' ($box.Text -match [regex]::Escape("保存先: $output"))
    Check '戻し方' ($box.Text -match 'すべての分割ファイルを同じフォルダーに置いて「資料\.bin\.exe」を実行してください。')
    Close-MessageBox $box 'OK'
}
Check 'ステータスバー' ((Get-Status $app) -eq '4 個に分割しました') (Get-Status $app)
Stop-Expzip $app

$files = @(Get-ChildItem $output | ForEach-Object { $_.Name } | Sort-Object)
Check '分割ファイルと連結プログラムができる' ($files.Count -eq 5 -and $files -contains '資料.bin.exe') ($files -join ', ')
Check '元のファイルは残る' ((Get-FileHash $source -Algorithm SHA256).Hash -eq $hash)

Section '連結プログラム'
$joiner = Start-Process (Join-Path $output '資料.bin.exe') -WorkingDirectory $output -PassThru
$message = Read-NativeMessage $joiner
Check '知らせが出る' ($null -ne $message)
if ($message) {
    Check '見出し' ($message.Title -eq 'ファイルの連結') $message.Title
    Check '文言' ($message.Text -eq '連結完了し、元のファイルと一致することを確認しました。') $message.Text
}
Check '連結プログラムが終わる' ($joiner.WaitForExit(10000))
$joined = Join-Path $output '資料.bin'
Check '元と同じファイルができる' ((Test-Path $joined) -and (Get-FileHash $joined -Algorithm SHA256).Hash -eq $hash)

Section 'もう一度実行したとき'
$joiner = Start-Process (Join-Path $output '資料.bin.exe') -WorkingDirectory $output -PassThru
$message = Read-NativeMessage $joiner
Check '同じ名前があると言う' ($message -and $message.Text -eq 'ファイルを連結できませんでした。既に同じ名前のファイルが存在する可能性があります。') $(if ($message) { $message.Text })
$joiner.WaitForExit(10000) | Out-Null
if (-not $joiner.HasExited) { $joiner.Kill() }
Check '元のファイルを壊さない' ((Get-FileHash $joined -Algorithm SHA256).Hash -eq $hash)

Complete-Suite
