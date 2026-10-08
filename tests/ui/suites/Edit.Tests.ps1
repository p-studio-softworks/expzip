# 書庫の中の編集: 名前の変更 (#15)、新しいフォルダー (#50)、削除、失敗したときの理由 (#106)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Edit'
Set-Settings
$archive = New-TestZip (Join-Path $script:Work 'edit.zip') ([ordered]@{
    'docs/'          = ''
    'docs/memo.txt'  = 'memo'
    'report.txt'     = 'report'
    'photo.jpg'      = 'jpg'
    'notes.txt'      = 'notes'
})

$app = Start-Expzip @($archive)
Check '一覧に並ぶ' ((Get-RowNames $app) -join ',' -match 'docs' -and (Get-RowNames $app) -contains 'report.txt') ((Get-RowNames $app) -join ', ')

Section '名前の変更'
Check '入力欄が開く' (Rename-Row $app 'report.txt' 'summary.txt')
Wait-Idle $app
Check '一覧の名前が変わる' ((Get-RowNames $app) -contains 'summary.txt') ((Get-RowNames $app) -join ', ')
Check '書庫の中の名前も変わる' ((Get-ZipNames $archive) -contains 'summary.txt')
Check 'ステータスバーに結果' ((Get-Status $app) -match '名前を変更しました') (Get-Status $app)

Section '使えない文字'
Rename-Row $app 'notes.txt' 'no*tes.txt' | Out-Null
$box = Find-MessageBox $app
Check '知らせが出る' ($null -ne $box)
if ($box) {
    Check '文言' ($box.Text -eq '名前に使用できない文字 (*) が含まれています。') $box.Text
    Close-MessageBox $box 'OK'
}
Check '書庫は変わらない' ((Get-ZipNames $archive) -contains 'notes.txt')

Section '同じ名前'
Rename-Row $app 'notes.txt' 'photo.jpg' | Out-Null
$box = Find-MessageBox $app
Check '知らせが出る' ($null -ne $box)
if ($box) {
    Check '文言' ($box.Text -eq 'このフォルダーには既に「photo.jpg」が存在します。') $box.Text
    Close-MessageBox $box 'OK'
}

Section '新しいフォルダー'
Select-Row $app 'photo.jpg' | Out-Null
Send-Keys $app '^+n'
Wait-Idle $app
Check 'フォルダーができる' ((Get-ZipNames $archive) -contains '新しいフォルダー/') ((Get-ZipNames $archive) -join ', ')
Check 'ステータスバーに結果' ((Get-Status $app) -match '新しいフォルダー') (Get-Status $app)
# 作った直後は名前を打ち替えられる状態になっている
$box = Wait-Until { ByType (ById $app.Window 'EntryList') $script:ControlType::Edit | Select-Object -First 1 }
Check 'そのまま名前を変えられる' ($null -ne $box)
if ($box) {
    Set-Text $box '資料'
    $box.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
    Wait-Idle $app
    Check '日本語の名前になる' ((Get-ZipNames $archive) -contains '資料/') ((Get-ZipNames $archive) -join ', ')
}

Section '削除'
Select-Row $app 'photo.jpg' | Out-Null
Send-Keys $app '{DEL}'
$box = Find-MessageBox $app
Check '確認が出る' ($null -ne $box)
if ($box) {
    Check '件数を言う' ($box.Text -match '^選択した 1 個の項目を書庫から削除します。') $box.Text
    Check '名前を並べる' ($box.Text -match 'photo\.jpg')
    Check '取り消せないと言う' ($box.Text -match 'この操作は取り消せません。削除しますか\?')
    Check '口は はい / いいえ' (($box.Buttons -contains 'はい(Y)') -and ($box.Buttons -contains 'いいえ(N)')) ($box.Buttons -join ', ')
    Close-MessageBox $box 'いいえ(N)'
    Check 'いいえなら消えない' ((Get-ZipNames $archive) -contains 'photo.jpg')
}

Select-Row $app 'docs' | Out-Null
Send-Keys $app '{DEL}'
$box = Find-MessageBox $app
if ($box) {
    Check 'フォルダーなら中身の数も言う' ($box.Text -match 'フォルダーの中身を含めて 1 個のファイルが削除されます。') $box.Text
    Close-MessageBox $box 'はい(Y)'
    Wait-Idle $app
    $names = Get-ZipNames $archive
    Check 'はいなら消える' (-not ($names | Where-Object { $_ -like 'docs/*' })) ($names -join ', ')
}
else { Check 'フォルダーの削除の確認が出る' $false }

Section '書庫がほかのプログラムに使われているとき'
# 読むのは許し、書くのは許さない。Expzip からは開けるが書き戻せない
$lock = [System.IO.File]::Open($archive, 'Open', 'Read', 'Read')
try {
    Rename-Row $app 'notes.txt' 'renamed.txt' | Out-Null
    $box = Find-MessageBox $app
    Check '失敗を知らせる' ($null -ne $box)
    if ($box) {
        Check '何が失敗したか' ($box.Text -match '^名前を変更できませんでした。') $box.Text
        # 英語の例外文ではなく、日本語の理由が出る
        Check '理由が日本語' ($box.Text -match '使用中' -and $box.Text -notmatch 'process') $box.Text
        Close-MessageBox $box 'OK'
    }
}
finally { $lock.Dispose() }
Wait-Idle $app
Check '書庫は壊れていない' ((Get-ZipNames $archive) -contains 'notes.txt')

Stop-Expzip $app

# 削除や移動でフォルダーの中身が無くなっても、フォルダーを空のまま残す (#203)。
# 書庫はフォルダーのエントリを持たない形で作る。ファイルの名前からフォルダーを組み立てる書庫
$secret = 'Kagi-2026'
function New-LooseZip([string]$Name) {
    return New-TestZip (Join-Path $script:Work $Name) ([ordered]@{
        'docs/a.txt'     = 'a'
        'docs/c.txt'     = 'c'
        'docs/sub/b.txt' = 'b'
        'top.txt'        = 'top'
    })
}

# パスワード付きの書庫では、書き換える前に尋ねられることがある。尋ねられたら答える
function Answer-Password($App) {
    $dialog = Find-Window $App 'パスワード' 3000
    if ($null -eq $dialog) { return }
    $box = ByType $dialog $script:ControlType::Edit | Select-Object -First 1
    $box.SetFocus()
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait($secret)
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
}

function Set-ZipPassword([string]$Archive) {
    $app = Start-Expzip @($Archive)
    Push (ById $app.Window 'PasswordButton')
    Answer-Password $app
    Wait-Idle $app
    Check 'パスワードを設定する' ((Get-Status $app) -match 'パスワードを設定しました') (Get-Status $app)
    Stop-Expzip $app
}

# いま選んでいる行を削除する
function Remove-Selected($App) {
    Send-Keys $App '{DEL}'
    $box = Find-MessageBox $App
    if ($box) { Close-MessageBox $box 'はい(Y)' }
    Answer-Password $App
    Wait-Idle $App
}

function Remove-Row($App, [string]$Name) {
    Select-Row $App $Name | Out-Null
    Remove-Selected $App
}

function Enter-Folder($App, [string]$Name) {
    Select-Row $App $Name | Out-Null
    Send-Keys $App '{ENTER}'
    Wait-Idle $App
}

# ツリーの項目。自分の名前 (最初の字) で探す。子の名前まで見ると親の項目が当たる
function Find-TreeItem($App, [string]$Name) {
    $tree = ById $App.Window 'FolderTree'
    return ByType $tree $script:ControlType::TreeItem | Where-Object {
        $text = ByType $_ $script:ControlType::Text | Select-Object -First 1
        $text -and $text.Current.Name -eq $Name
    } | Select-Object -First 1
}

# ツリーでそのフォルダーへ移る
function Open-TreeFolder($App, [string]$Name) {
    $item = Find-TreeItem $App $Name
    if ($null -eq $item) { throw "ツリーにありません: $Name" }
    Select-Element $item
    Start-Sleep -Milliseconds 600
    Wait-Idle $App
}

# 一覧の行をつかんで、ツリーのフォルダーへ運ぶ。選んでいる行が全部移る
function Move-RowToTree($App, [string]$Row, [string]$Folder) {
    # ツリーのルートを開き直す (Expand) と、一覧がルートへ移ってしまう。ルートは初めから開いている
    Focus-App $App
    $target = Find-TreeItem $App $Folder
    if ($null -eq $target) { Check "ツリーに $Folder がある" $false; return }
    $from = (ByName (Find-Row $App $Row) $Row).Current.BoundingRectangle
    $to = (ByName $target $Folder).Current.BoundingRectangle
    Invoke-MouseDrag ([int]($from.X + $from.Width / 2)) ([int]($from.Y + $from.Height / 2)) `
        ([int]($to.X + $to.Width / 2)) ([int]($to.Y + $to.Height / 2))
    Answer-Password $App
    Wait-Idle $App
}

function Folder-Entries([string]$Archive) {
    return @(Get-ZipNames $Archive | Where-Object { $_.EndsWith('/') } | Sort-Object)
}

Section '削除で中身が無くなったフォルダーを残す (#203)'
$loose = New-LooseZip 'loose.zip'
$app = Start-Expzip @($loose)
Enter-Folder $app 'docs'
Enter-Folder $app 'sub'
Remove-Row $app 'b.txt'
$names = Get-ZipNames $loose
Check '奥のファイルだけを消すと、そのフォルダーを書き足す' (
    ($names -contains 'docs/sub/') -and -not ($names -contains 'docs/sub/b.txt')) ($names -join ', ')
Check 'ほかはそのまま' (($names -contains 'docs/a.txt') -and ($names -contains 'docs/c.txt') -and ($names -contains 'top.txt')) ($names -join ', ')
Check '中身が残る親は書き足さない' (-not ($names -contains 'docs/')) ($names -join ', ')

# 一覧が空になっても、Backspace で上へ戻れる (#212)
Send-Keys $app '{BACKSPACE}'
Wait-Idle $app
Check '空になったフォルダーから Backspace で docs へ戻る' (
    ((Get-RowNames $app | Sort-Object) -join ',') -eq 'a.txt,c.txt,sub') ((Get-RowNames $app) -join ', ')
Remove-Row $app 'a.txt'
$names = Get-ZipNames $loose
Check 'ほかの中身が残るなら何も書き足さない' (((Folder-Entries $loose) -join ',') -eq 'docs/sub/') ((Folder-Entries $loose) -join ', ')

Remove-Row $app 'c.txt'
$names = Get-ZipNames $loose
Check 'docs のファイルを全部消すと docs/sub/ が残る' (
    (($names | Sort-Object) -join ',') -eq 'docs/sub/,top.txt') ($names -join ', ')
Check '一覧に空の sub が残る' ((Get-RowNames $app) -contains 'sub') ((Get-RowNames $app) -join ', ')
Open-TreeFolder $app 'loose.zip'
Check '一覧に空の docs が残る' ((Get-RowNames $app) -contains 'docs') ((Get-RowNames $app) -join ', ')
Stop-Expzip $app

Section 'フォルダーそのものを削除する (#203)'
$loose = New-LooseZip 'loose-folder.zip'
$app = Start-Expzip @($loose)
Enter-Folder $app 'docs'
Remove-Row $app 'sub'
$names = Get-ZipNames $loose
Check 'sub は中身ごと消える' (-not ($names | Where-Object { $_ -like 'docs/sub*' })) ($names -join ', ')
Check 'docs に中身が残るので何も書き足さない' ((Folder-Entries $loose).Count -eq 0) ((Folder-Entries $loose) -join ', ')
Open-TreeFolder $app 'loose-folder.zip'
Remove-Row $app 'docs'
$names = Get-ZipNames $loose
Check 'docs ごと消える' (($names -join ',') -eq 'top.txt') ($names -join ', ')
Stop-Expzip $app

Section 'パスワード付きの書庫の削除 (#203)'
$locked = New-LooseZip 'loose-locked.zip'
Set-ZipPassword $locked
$app = Start-Expzip @($locked)
Enter-Folder $app 'docs'
Enter-Folder $app 'sub'
Remove-Row $app 'b.txt'
Open-TreeFolder $app 'docs'
Select-Row $app 'a.txt' | Out-Null
Send-Keys $app '+{DOWN}'
Remove-Selected $app
$names = Get-ZipNames $locked
Check 'docs の中を全部消すと docs/sub/ が残る' ((($names | Sort-Object) -join ',') -eq 'docs/sub/,top.txt') ($names -join ', ')
Stop-Expzip $app

Section '移動で中身が無くなったフォルダーを残す (#203)'
$loose = New-LooseZip 'loose-move.zip'
$app = Start-Expzip @($loose)
Enter-Folder $app 'docs'
Enter-Folder $app 'sub'
Move-RowToTree $app 'b.txt' 'docs'
$names = Get-ZipNames $loose
Check 'b.txt が docs へ移る' ($names -contains 'docs/b.txt') ($names -join ', ')
Check '移す元の docs/sub/ が残る' ($names -contains 'docs/sub/') ($names -join ', ')

$root = ByType (ById $app.Window 'FolderTree') $script:ControlType::TreeItem | Select-Object -First 1
Select-Element $root
Start-Sleep -Milliseconds 600
Enter-Folder $app 'docs'
Send-Keys $app '^a'
Move-RowToTree $app 'a.txt' 'loose-move.zip'
$names = Get-ZipNames $loose
Check 'docs の中身がいちばん上へ移る' (
    ($names -contains 'a.txt') -and ($names -contains 'b.txt') -and ($names -contains 'c.txt') -and ($names -contains 'sub/')) ($names -join ', ')
Check '移す元の docs/ が残る' ($names -contains 'docs/') ($names -join ', ')
Check 'docs の中には何も残らない' (-not ($names | Where-Object { $_ -like 'docs/?*' })) ($names -join ', ')
Stop-Expzip $app

Section '名前の変更では書き足さない (#203)'
$loose = New-LooseZip 'loose-rename.zip'
$app = Start-Expzip @($loose)
Rename-Row $app 'top.txt' 'top2.txt' | Out-Null
Wait-Idle $app
$names = Get-ZipNames $loose
Check 'ファイルの名前が変わる' ($names -contains 'top2.txt') ($names -join ', ')
Check 'ファイルの名前の変更では何も書き足さない' ((Folder-Entries $loose).Count -eq 0) ((Folder-Entries $loose) -join ', ')
Enter-Folder $app 'docs'
Rename-Row $app 'sub' 'sub2' | Out-Null
Wait-Idle $app
$names = Get-ZipNames $loose
Check 'フォルダーの名前が変わる' ($names -contains 'docs/sub2/b.txt') ($names -join ', ')
Check 'フォルダーの名前の変更では何も書き足さない' ((Folder-Entries $loose).Count -eq 0) ((Folder-Entries $loose) -join ', ')
Stop-Expzip $app

Section 'パスワード付きの書庫の移動 (#203)'
$locked = New-LooseZip 'loose-move-locked.zip'
Set-ZipPassword $locked
$app = Start-Expzip @($locked)
Enter-Folder $app 'docs'
Enter-Folder $app 'sub'
Move-RowToTree $app 'b.txt' 'loose-move-locked.zip'
$names = Get-ZipNames $locked
Check 'b.txt がいちばん上へ移る' ($names -contains 'b.txt') ($names -join ', ')
Check '移す元の docs/sub/ が残る' ($names -contains 'docs/sub/') ($names -join ', ')
Stop-Expzip $app

Complete-Suite
