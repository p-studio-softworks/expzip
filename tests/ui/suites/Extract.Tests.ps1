# 展開 (#47, #48) と、自己解凍書庫 (#29)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Extract'
Set-Settings

$archive = New-TestZip (Join-Path $script:Work '資料.zip') ([ordered]@{
    '報告/'          = ''
    '報告/本文.txt'  = 'honbun'
    '報告/図.txt'    = 'zu'
    'メモ.txt'       = 'memo'
})
$destination = Join-Path $script:Work 'out'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$app = Start-Expzip @($archive)

Section '書庫全体を展開する'
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '書庫全体の展開先を選択' $destination)
$box = Find-MessageBox $app 30000
Check '結果を知らせる' ($null -ne $box)
if ($box) {
    Check '展開先' ($box.Text -match [regex]::Escape("展開先: $destination")) $box.Text
    Check '件数' ($box.Text -match '展開したファイル: 3 個')
    Close-MessageBox $box 'OK'
}
Check 'ステータスバー' ((Get-Status $app) -eq '3 個のファイルを展開しました') (Get-Status $app)
Check 'フォルダーの形のまま出る' ((Test-Path (Join-Path $destination '報告\本文.txt')) -and (Test-Path (Join-Path $destination 'メモ.txt')))

Section '同じ名前があるとき'
Set-Content -Path (Join-Path $destination 'メモ.txt') -Value 'kaeta' -Encoding UTF8 -NoNewline
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $destination | Out-Null
$box = Find-MessageBox $app 30000
Check '上書きするか尋ねる' ($null -ne $box)
if ($box) {
    Check '件数' ($box.Text -match '^展開先に同じ名前のファイルが 3 個あります。上書きしますか\?') $box.Text
    Check 'いいえの意味' ($box.Text -match '「いいえ」を選ぶと、それらは展開しません。')
    Check '口は はい / いいえ / キャンセル' ($box.Buttons.Count -eq 3) ($box.Buttons -join ', ')
    Close-MessageBox $box 'いいえ(N)'
    $result = Find-MessageBox $app 30000
    if ($result) {
        Check '残したファイルの数を言う' ($result.Text -match '上書きせずそのまま残したファイル: 3 個') $result.Text
        Close-MessageBox $result 'OK'
    }
}
Check 'いいえなら書き換えない' ((Get-Content (Join-Path $destination 'メモ.txt') -Raw -Encoding UTF8) -eq 'kaeta')

Section '選択した項目を展開する'
$picked = Join-Path $script:Work 'picked'
New-Item -ItemType Directory -Force -Path $picked | Out-Null
Select-Row $app '報告' | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '選択した項目の展開先を尋ねる' (Complete-FileDialog $app '選択した項目の展開先を選択' $picked)
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
Check '選んだフォルダーだけが出る' ((Test-Path (Join-Path $picked '報告\図.txt')) -and -not (Test-Path (Join-Path $picked 'メモ.txt'))) ((Get-ChildItem $picked -Recurse | ForEach-Object { $_.Name }) -join ', ')

Section '自己解凍書庫'
$sfx = Join-Path $script:Work 'sfx\資料.exe'
New-Item -ItemType Directory -Force -Path (Split-Path $sfx) | Out-Null
Push (ById $app.Window 'SfxButton')
Check '保存先を尋ねる' (Complete-FileDialog $app '自己解凍書庫として保存' $sfx)
Wait-Idle $app
Check '作ったと知らせる' ((Get-Status $app) -match ('^' + [regex]::Escape($sfx) + ' を作成しました \([\d,]+ バイト\)$')) (Get-Status $app)
Check 'ファイルができる' (Test-Path $sfx)
Stop-Expzip $app

$run = Start-Process $sfx -WorkingDirectory (Split-Path $sfx) -PassThru
$message = Read-NativeMessage $run
Check '展開したと知らせる' ($message -and $message.Title -eq '自己解凍書庫') $(if ($message) { $message.Title })
if ($message) {
    Check '文言' ($message.Text -match '^展開しました。\n\n3 個のファイル\n') $message.Text
}
$run.WaitForExit(10000) | Out-Null
if (-not $run.HasExited) { $run.Kill() }
$unpacked = Join-Path (Split-Path $sfx) '資料'
Check '自分の名前のフォルダーに出る' ((Get-Content (Join-Path $unpacked '報告\本文.txt') -Raw -Encoding UTF8) -eq 'honbun')

Complete-Suite
