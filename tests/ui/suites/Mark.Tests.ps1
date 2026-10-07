# 書庫を書き換えても、書庫に付いていた出所の印 (#12) が残る (#211)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Mark'
Set-Settings

$inner = New-TestZip (Join-Path $script:Work 'inner-src.zip') ([ordered]@{
    '内側メモ.txt' = 'memo'
    '内側表.csv'   = 'a,b'
})
$archive = New-TestZip (Join-Path $script:Work '落とした.zip') ([ordered]@{
    'docs/'         = ''
    'docs/memo.txt' = 'memo'
    'report.txt'    = 'report'
    'notes.txt'     = 'notes'
    'photo.jpg'     = 'jpg'
    '内側.zip'      = [System.IO.File]::ReadAllBytes($inner)
})
$extra = Join-Path $script:Work '足す.txt'
Set-Content -Path $extra -Value 'extra' -Encoding UTF8

# ブラウザーが付けるのと同じ形の印 (URL は ASCII に直してある)。書き換えたあとも、中身ごとそのまま残っているかを見る
$mark = "[ZoneTransfer]`r`nZoneId=3`r`nReferrerUrl=https://example.com/`r`nHostUrl=https://example.com/files/archive.zip"
Set-Content -Path $archive -Stream Zone.Identifier -Value $mark -Encoding Ascii
$expected = (Get-Content -Path $archive -Stream Zone.Identifier) -join "`n"

function Get-Zone {
    return (Get-Content -Path $archive -Stream Zone.Identifier -ErrorAction SilentlyContinue) -join "`n"
}

# 書き換えのあとに確かめること。印の付け直しで更新日時が変わっても、外での書き換え (#64) と取り違えない
function Check-Mark($App, [string]$Label, $Done) {
    Check $Label $Done
    $zone = Get-Zone
    Check '印が残る' ($zone -eq $expected) $zone
    Start-Sleep -Milliseconds 2500
    Check '外で変更されたと取り違えない' ((Get-Status $App) -notmatch 'ほかのプログラムで変更された') (Get-Status $App)
}

# パスワードの窓に打ち込んで確定する。伏せ字の欄は値を直接入れられないのでキーで打つ
function Enter-Password($App, [string]$Text) {
    $dialog = Find-Window $App 'パスワード'
    if ($null -eq $dialog) { return $false }
    $box = ByType $dialog $script:ControlType::Edit | Select-Object -First 1
    $box.SetFocus()
    Start-Sleep -Milliseconds 200
    if ($Text.Length -gt 0) { [System.Windows.Forms.SendKeys]::SendWait($Text) }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
    return $true
}

Check '試す書庫に印が付いている' ($expected -match 'ZoneId=3') $expected

$app = Start-Expzip @($archive)

Section '追加'
$center = Get-WindowCenter $app
Invoke-Drop @($extra) $center.X $center.Y | Out-Null
Wait-Idle $app
Check-Mark $app '追加される' (((Get-ZipNames $archive) -contains '足す.txt'))

Section '名前の変更'
Rename-Row $app 'notes.txt' 'memo2.txt' | Out-Null
Wait-Idle $app
Check-Mark $app '名前が変わる' (((Get-ZipNames $archive) -contains 'memo2.txt'))

Section '新しいフォルダー'
Select-Row $app 'photo.jpg' | Out-Null
Send-Keys $app '^+n'
Wait-Idle $app
$box = Wait-Until { ByType (ById $app.Window 'EntryList') $script:ControlType::Edit | Select-Object -First 1 }
if ($box) {
    $box.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
    Wait-Idle $app
}
Check-Mark $app 'フォルダーができる' (((Get-ZipNames $archive) -contains '新しいフォルダー/'))

Section '削除'
Select-Row $app 'photo.jpg' | Out-Null
Send-Keys $app '{DEL}'
$box = Find-MessageBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
Wait-Idle $app
Check-Mark $app '消える' (-not ((Get-ZipNames $archive) -contains 'photo.jpg'))

Section '移動'
# 一覧の中で、ファイルをフォルダーの上へ運ぶ
Focus-App $app
$from = (ByName (Find-Row $app 'report.txt') 'report.txt').Current.BoundingRectangle
$to = (ByName (Find-Row $app 'docs') 'docs').Current.BoundingRectangle
Invoke-MouseDrag ([int]($from.X + $from.Width / 2)) ([int]($from.Y + $from.Height / 2)) `
    ([int]($to.X + $to.Width / 2)) ([int]($to.Y + $to.Height / 2))
Wait-Idle $app
Check-Mark $app '移る' (((Get-ZipNames $archive) -contains 'docs/report.txt'))
# 移した先を見せるので、ツリーでいちばん上へ戻る
$root = ByType (ById $app.Window 'FolderTree') $script:ControlType::TreeItem | Select-Object -First 1
Select-Element $root
Start-Sleep -Milliseconds 600

Section '中の書庫の保存'
Select-Row $app '内側.zip' | Out-Null
Send-Keys $app '{ENTER}'
Wait-Idle $app
Select-Row $app '内側メモ.txt' | Out-Null
Send-Keys $app '{DEL}'
$box = Find-MessageBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
Wait-Idle $app
Send-Keys $app '^s'
Wait-Idle $app
Check-Mark $app '親に反映される' (-not ((Get-InnerZipNames $archive '内側.zip') -contains '内側メモ.txt'))
Send-Keys $app '^w'
Wait-Idle $app

Section 'パスワードの設定'
$secret = 'Kagi-2026'
Push (ById $app.Window 'PasswordButton')
Enter-Password $app $secret | Out-Null
Wait-Idle $app
Check-Mark $app '設定される' ((Get-Status $app) -eq 'パスワードを設定しました (AES-256)')

Section 'パスワード付きの書庫の書き換え'
Rename-Row $app 'memo2.txt' 'memo3.txt' | Out-Null
# 設定したばかりの合言葉は覚えているので、尋ねられないこともある
if (Find-Window $app 'パスワード' 3000) { Enter-Password $app $secret | Out-Null }
Wait-Idle $app
Check-Mark $app '名前が変わる' (((Get-ZipNames $archive) -contains 'memo3.txt'))

Section 'パスワードの削除'
Push (ById $app.Window 'PasswordButton')
Enter-Password $app '' | Out-Null
Wait-Idle $app
Check-Mark $app '削除される' ((Get-Status $app) -eq 'パスワードを削除しました')

Stop-Expzip $app

Section '印の無い書庫'
# 自分で作った書庫には印が無い。書き換えても付けない
$plain = New-TestZip (Join-Path $script:Work '作った.zip') ([ordered]@{ 'a.txt' = 'a'; 'b.txt' = 'b' })
$app = Start-Expzip @($plain)
Rename-Row $app 'a.txt' 'c.txt' | Out-Null
Wait-Idle $app
Check '名前が変わる' ((Get-ZipNames $plain) -contains 'c.txt')
$zone = (Get-Content -Path $plain -Stream Zone.Identifier -ErrorAction SilentlyContinue) -join "`n"
Check '印は付かない' ([string]::IsNullOrEmpty($zone)) $zone
Stop-Expzip $app

Complete-Suite
