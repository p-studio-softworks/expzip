# 一覧で選んだものから新しい書庫を作る (#207)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'NewFromSelection'
Set-Settings

$dialogTitle = '新しい書庫を作成'
$menuName = '選択した項目から新しい書庫を作成'

$inner = New-TestZip (Join-Path $script:Work 'inner-src.zip') ([ordered]@{
    'x/'      = ''
    'x/y.txt' = 'y'
})
$source = New-TestZip (Join-Path $script:Work '元.zip') ([ordered]@{
    '資料/'           = ''
    '資料/画像/'      = ''
    '資料/画像/a.png' = 'png'
    '資料/画像/空/'   = ''
    '資料/メモ.txt'   = 'memo'
    '資料/写真.jpg'   = 'jpg'
    'readme.txt'      = 'readme'
    '中.zip'          = [System.IO.File]::ReadAllBytes($inner)
})
# インターネットから来た印を付けておく。新しい書庫にも引き継ぐ
Set-Content -Path $source -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"

function Tab-Order($App) { return (@(Get-Tabs $App | ForEach-Object { $_.Current.Name }) -join ', ') }
function Select-Tab($App, [int]$Index) {
    Select-Element (Get-Tabs $App)[$Index]
    Wait-Idle $App
}

$app = Start-Expzip @($source)

Section 'メニュー'
$menu = Open-RowMenu $app 'readme.txt'
$names = @(if ($menu) { ByType $menu $script:ControlType::MenuItem | ForEach-Object { $_.Current.Name } })
Check '「新しいタブで開く」のすぐ下' (($names -join ', ') -match "^開く, 新しいタブで開く, $menuName, ") ($names -join ', ')
$item = if ($menu) { ByName $menu $menuName }
Check 'ファイルで押せる' ($item -and $item.Current.IsEnabled)
Close-DropDown $app
$menu = Open-RowMenu $app '資料'
$item = if ($menu) { ByName $menu $menuName }
Check 'フォルダーで押せる' ($item -and $item.Current.IsEnabled)
Close-DropDown $app

Section 'いちばん上で複数を選ぶ'
# 名前の案は書庫の名前。元の書庫と重なるので、置き換えを選ばせないよう (2) を付ける
Select-Row $app 'readme.txt' | Out-Null
Send-Keys $app '^a'
Check 'メニューから作る' (Open-NewArchiveFromSelection $app)
$name = Get-FileDialogName $app $dialogTitle
Check '名前の案は書庫の名前に (2) を付けたもの' ($name -eq '元 (2).zip') $name
Check '取りやめられる' (Cancel-FileDialog $app $dialogTitle)
Wait-Idle $app
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 1) (Tab-Order $app)
Check '書庫はできない' (-not (Test-Path (Join-Path $script:Work '元 (2).zip')))

Section '開いている書庫は置き換えない'
Select-Row $app 'readme.txt' | Out-Null
Open-NewArchiveFromSelection $app | Out-Null
Complete-FileDialog $app $dialogTitle '元.zip' | Out-Null
# Windows の「置き換えますか?」には「はい」で答える。保存する窓と違い、確定の口 (1) が無い
$prompt = Wait-Until {
    Get-AppWindows $app | Where-Object {
        $_.Current.ClassName -eq '#32770' -and $_.Current.Name -eq $dialogTitle -and $null -eq (ById $_ '1')
    } | Select-Object -First 1
}
Check 'Windows が置き換えを確かめる' ($null -ne $prompt)
$yes = if ($prompt) { ByName $prompt 'はい(Y)' }
if ($yes) {
    [ExpzipUi.Native]::PostMessage([IntPtr]$yes.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 900
}
$box = Find-MessageBox $app
Check '断る' ($null -ne $box)
if ($box) {
    Check '文言' ($box.Text -eq '元.zip は開いているため、置き換えられません。別の名前を入力してください。') $box.Text
    Close-MessageBox $box 'OK'
}
Check '保存先を尋ね直す' (Cancel-FileDialog $app $dialogTitle)
Wait-Idle $app
$entries = @(Get-ZipNames $source)
Check '元の書庫はそのまま' ($entries.Count -eq 8) ($entries -join ', ')
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 1) (Tab-Order $app)

Section '1 個だけ選ぶ'
Select-Row $app '資料' | Out-Null
Send-Keys $app '{ENTER}'
Wait-Idle $app
Select-Row $app '写真.jpg' | Out-Null
Open-NewArchiveFromSelection $app | Out-Null
$name = Get-FileDialogName $app $dialogTitle
Check '名前の案は拡張子を除いたファイルの名前' ($name -eq '写真.zip') $name
Cancel-FileDialog $app $dialogTitle | Out-Null
Wait-Idle $app

Section 'フォルダーの中で複数を選ぶ'
Select-Row $app '画像' | Out-Null
Send-Keys $app '^a'
Open-NewArchiveFromSelection $app | Out-Null
$name = Get-FileDialogName $app $dialogTitle
Check '名前の案はいまのフォルダーの名前' ($name -eq '資料.zip') $name
# 名前だけを入れて確定する。保存先の案が元の書庫の隣でなければ、別の場所にできる
Check '保存先を決める' (Complete-FileDialog $app $dialogTitle '資料.zip')
Wait-Idle $app
$made = Join-Path $script:Work '資料.zip'
Check '元の書庫の隣にできる' (Test-Path $made) $made
if (Test-Path $made) {
    $entries = @(Get-ZipNames $made | Sort-Object)
    # いまのフォルダーから下だけが入る。中身の無いフォルダーも残る
    $expected = @('画像/a.png', '画像/空/', 'メモ.txt', '写真.jpg') | Sort-Object
    Check '中身はいまのフォルダーから下' (($entries -join ', ') -eq ($expected -join ', ')) ($entries -join ', ')
    $zone = (Get-Content -Path $made -Stream Zone.Identifier -ErrorAction SilentlyContinue) -join "`n"
    Check '出所の印を引き継ぐ' ($zone -match 'ZoneId=3') $zone
}
Check '元のタブのすぐ右に開く' ((Tab-Order $app) -eq '元.zip, 資料.zip') (Tab-Order $app)
Check '開いたタブを選ぶ' ((Get-Selected (ById $app.Window 'ArchiveTabs')) -eq '資料.zip') (Get-Selected (ById $app.Window 'ArchiveTabs'))
Check '中身が並ぶ' (((Get-RowNames $app) -join ', ') -eq '画像, メモ.txt, 写真.jpg') ((Get-RowNames $app) -join ', ')
Stop-Expzip $app

Section '中の書庫から作る'
$app = Start-Expzip @($source)
# 元と関係のないタブを右に置いておく。右の端に足していたら、新しいタブがこれより右に出る
Push (ById $app.Window 'NewTabButton')
Check '+ で別の書庫を作る' (Complete-FileDialog $app $dialogTitle (Join-Path $script:Work '別.zip'))
Wait-Idle $app
Select-Tab $app 0
Select-Row $app '中.zip' | Out-Null
Send-Keys $app '{ENTER}'
Wait-Idle $app
Check '中の書庫を開く' ((Tab-Order $app) -eq '元.zip, 中.zip, 別.zip') (Tab-Order $app)
Select-Row $app 'x' | Out-Null
Open-NewArchiveFromSelection $app | Out-Null
$name = Get-FileDialogName $app $dialogTitle
Check '名前の案はフォルダーの名前' ($name -eq 'x.zip') $name
# 中の書庫は一時ファイルの置き場にある。保存先の案は、いちばん外の書庫の隣
Check '保存先を決める' (Complete-FileDialog $app $dialogTitle 'x.zip')
Wait-Idle $app
$made = Join-Path $script:Work 'x.zip'
Check 'いちばん外の書庫の隣にできる' (Test-Path $made) $made
if (Test-Path $made) {
    $entries = @(Get-ZipNames $made | Sort-Object)
    Check 'フォルダーは中身ごと入る' (($entries -join ', ') -eq 'x/y.txt') ($entries -join ', ')
}
Check '中の書庫のタブのすぐ右に開く' ((Tab-Order $app) -eq '元.zip, 中.zip, x.zip, 別.zip') (Tab-Order $app)
Stop-Expzip $app

Complete-Suite
