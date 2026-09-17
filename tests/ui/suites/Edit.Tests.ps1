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
Complete-Suite
