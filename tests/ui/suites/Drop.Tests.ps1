# ウィンドウへのドロップ: 書庫を開いていないときに書庫ではないものを落とすと、新しい書庫を作るか尋ねる (#156)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Drop'
Set-Settings

$question = '書庫を開いていません。新しい書庫を作成して、ドロップした項目を追加しますか?'
$dialogTitle = '新しい書庫を作成'

$folder = Join-Path $script:Work '資料'
New-Item -ItemType Directory -Force -Path (Join-Path $folder 'sub') | Out-Null
Set-Content -Path (Join-Path $folder 'a.txt') -Value 'a' -Encoding UTF8
Set-Content -Path (Join-Path $folder 'sub\b.txt') -Value 'b' -Encoding UTF8

$bundle = Join-Path $script:Work '束'
New-Item -ItemType Directory -Force -Path $bundle | Out-Null
$x = Join-Path $bundle 'x.txt'
$y = Join-Path $bundle 'y.txt'
$memo = Join-Path $bundle 'memo.v2.txt'
Set-Content -Path $x -Value 'x' -Encoding UTF8
Set-Content -Path $y -Value 'y' -Encoding UTF8
Set-Content -Path $memo -Value 'memo' -Encoding UTF8

$made = Join-Path $script:Work '資料.zip'

# 保存する窓を取りやめる。取りやめの口は ID 2
function Cancel-FileDialog($App, [string]$Title) {
    $dialog = Find-Window $App $Title 15000
    if ($null -eq $dialog) { return $false }
    $cancel = Find-All $dialog ([System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ClassName -eq 'Button' -and $_.Current.AutomationId -eq '2' } |
        Select-Object -First 1
    if ($null -eq $cancel) { return $false }
    [ExpzipUi.Native]::PostMessage([IntPtr]$cancel.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    return (Test-WindowGone $App $Title)
}

$app = Start-Expzip
$center = Get-WindowCenter $app

Section 'いいえ'
Check '落とせる' (Invoke-Drop @($folder) $center.X $center.Y)
$box = Find-MessageBox $app
Check '尋ねる' ($null -ne $box)
if ($box) {
    Check '文言' ($box.Text -eq $question) $box.Text
    Check 'はい・いいえで答える' (($box.Buttons -join ',') -eq 'はい(Y),いいえ(N)') ($box.Buttons -join ', ')
    Close-MessageBox $box 'いいえ(N)'
}
Check '保存先を尋ねない' ($null -eq (Find-Window $app $dialogTitle 2000))
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 0) (Get-Tabs $app).Count
Check '書庫はできない' (-not (Test-Path $made))

Section 'はい'
Focus-App $app
Invoke-Drop @($folder) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
Check '尋ねる' ($null -ne $box)
if ($box) { Close-MessageBox $box 'はい(Y)' }
$name = Get-FileDialogName $app $dialogTitle
Check '名前の初期値はフォルダーの名前' ($name -eq '資料.zip') $name
# 名前だけを入れて確定する。保存先の初期値が、落としたフォルダーの隣でなければ別の場所にできる
Check '保存先を決める' (Complete-FileDialog $app $dialogTitle '資料.zip')
Wait-Idle $app
Check '落としたフォルダーの隣にできる' (Test-Path $made) $made
if (Test-Path $made) {
    $names = Get-ZipNames $made
    Check '中身が入る' ($names -contains '資料/a.txt' -and $names -contains '資料/sub/b.txt') ($names -join ', ')
}
Check 'タブで開く' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Check '一覧に並ぶ' ((Get-RowNames $app) -contains '資料') ((Get-RowNames $app) -join ', ')

Section '書庫を開いているときは今どおり追加する'
Focus-App $app
Invoke-Drop @($x) $center.X $center.Y | Out-Null
Wait-Idle $app
Check '尋ねない' ($null -eq (Find-MessageBox $app 2000))
Check '開いている書庫に入る' ((Get-ZipNames $made) -contains 'x.txt') ((Get-ZipNames $made) -join ', ')
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Stop-Expzip $app

$app = Start-Expzip
$center = Get-WindowCenter $app

Section '複数を落とす'
Invoke-Drop @($x, $y) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
Check '尋ねる' ($null -ne $box)
if ($box) { Close-MessageBox $box 'はい(Y)' }
$name = Get-FileDialogName $app $dialogTitle
Check '名前の初期値は入っていたフォルダーの名前' ($name -eq '束.zip') $name
Check '取りやめられる' (Cancel-FileDialog $app $dialogTitle)
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 0) (Get-Tabs $app).Count
Check '書庫はできない' (-not (Test-Path (Join-Path $script:Work '束.zip')))

Section 'ファイルを 1 個落とす'
Focus-App $app
Invoke-Drop @($memo) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
$name = Get-FileDialogName $app $dialogTitle
Check '名前の初期値は拡張子を除いたファイルの名前' ($name -eq 'memo.v2.zip') $name
Cancel-FileDialog $app $dialogTitle | Out-Null

Section '書庫は尋ねずに開く'
Focus-App $app
Invoke-Drop @($made) $center.X $center.Y | Out-Null
Wait-Idle $app
Check '尋ねない' ($null -eq (Find-MessageBox $app 2000))
Check 'タブで開く' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Check '一覧に並ぶ' ((Get-RowNames $app) -contains '資料') ((Get-RowNames $app) -join ', ')
Stop-Expzip $app

Complete-Suite
