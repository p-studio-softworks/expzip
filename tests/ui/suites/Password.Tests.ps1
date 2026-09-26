# パスワードの設定・入力・削除 (#20, #63)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Password'
Set-Settings
$archive = New-TestZip (Join-Path $script:Work '資料.zip') ([ordered]@{
    'report.txt' = 'report'
    'notes.txt'  = 'notes'
})
$secret = 'Kagi-2026'

# 先頭のエントリの暗号化の印と圧縮方式 (AES なら 99) を読む
function Get-FirstEntryFlags([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return [pscustomobject]@{
        Encrypted = ($bytes[6] -band 1) -eq 1
        Method    = [BitConverter]::ToUInt16($bytes, 8)
    }
}

# パスワードの窓に打ち込んで確定する。伏せ字の欄は値を直接入れられないのでキーで打つ
function Enter-Password($App, [string]$Text) {
    $dialog = Find-Window $App 'パスワード'
    if ($null -eq $dialog) { return $null }
    $prompt = (ByType $dialog $script:ControlType::Text | ForEach-Object { $_.Current.Name }) -join "`n"
    $box = ByType $dialog $script:ControlType::Edit | Select-Object -First 1
    # ボタンの高さを決め打ちしていないか。26 に固定していて、字の下が切れていた。
    # Fluent のボタンと入力欄は、決め打ちしなければ同じ高さになる。画面の倍率によらず比べられる
    $script:ShortButtons = @(ByType $dialog $script:ControlType::Button |
        Where-Object { $_.Current.BoundingRectangle.Height -lt $box.Current.BoundingRectangle.Height - 2 } |
        ForEach-Object { '{0} {1:0} / 入力欄 {2:0}' -f $_.Current.Name, $_.Current.BoundingRectangle.Height, $box.Current.BoundingRectangle.Height })
    $box.SetFocus()
    Start-Sleep -Milliseconds 200
    if ($Text.Length -gt 0) { [System.Windows.Forms.SendKeys]::SendWait($Text) }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
    return $prompt.Replace("`r`n", "`n")
}

# 見えている錠前の印 (#128)
function Get-LockMarks($Scope) { return Get-Marks $Scope 'LockMark' }

Section 'パスワードを設定する'
$app = Start-Expzip @($archive)
Check '保護されていなければ錠前は無い' ((Get-LockMarks (Find-Row $app 'report.txt')).Count -eq 0)
$plainLeft = Get-NameLeft $app 'report.txt'
Push (ById $app.Window 'PasswordButton')
$prompt = Enter-Password $app $secret
Check '窓が開く' ($null -ne $prompt)
Check 'ボタンの字が切れない高さ' ($script:ShortButtons.Count -eq 0) ($script:ShortButtons -join ', ')
Check '尋ね方' ($prompt -match '^「資料\.zip」に設定するパスワードを入力してください。') $prompt
# 忘れたときに何が起きるかは、消さずに伝える
Check '忘れたときの注意' ($prompt -match 'パスワードを忘れると、中身を展開できなくなります。')
Wait-Idle $app
Check 'ステータスバーに結果' ((Get-Status $app) -eq 'パスワードを設定しました (AES-256)') (Get-Status $app)
$flags = Get-FirstEntryFlags $archive
Check '中身が暗号化される' ($flags.Encrypted -and $flags.Method -eq 99) "印 $($flags.Encrypted) / 方式 $($flags.Method)"
Stop-Expzip $app

Section '開き直して書き換えるとき'
$app = Start-Expzip @($archive)
# 保護されていることは緑の文字と錠前の絵で出している。読み上げには
# どちらも届かないので、行の名前にも入れる (#119)
$rowNames = @(Get-Rows $app | ForEach-Object { $_.Current.Name })
Check '保護された項目は行の名前にも出る' (
    ($rowNames -match '^report\.txt、パスワードで保護されています$') -and
    ($rowNames -match '^notes\.txt、パスワードで保護されています$')) ($rowNames -join ' / ')
# 色だけでなく錠前の印でも示す (#128)
Check '保護された行に錠前が付く' (
    (Get-LockMarks (Find-Row $app 'report.txt')).Count -eq 1 -and
    (Get-LockMarks (Find-Row $app 'notes.txt')).Count -eq 1)
Check 'ツリーにも錠前が付く' ((Get-LockMarks (ById $app.Window 'FolderTree')).Count -ge 1)
# 印を名前の前に並べると、名前が右へずれる (#88)。絵に重ねるので、ずれない
$lockedLeft = Get-NameLeft $app 'report.txt'
Check '錠前が付いても名前の位置は変わらない' ([Math]::Abs($lockedLeft - $plainLeft) -lt 1) "$plainLeft → $lockedLeft"
Rename-Row $app 'notes.txt' 'memo.txt' | Out-Null
$prompt = Enter-Password $app 'machigai'
Check '尋ねる' ($prompt -match '^「資料\.zip」はパスワードで保護されています。') $prompt
$prompt = Enter-Password $app $secret
Check '違えば尋ね直す' ($prompt -match '^パスワードが違います。') $prompt
Wait-Idle $app
$names = Get-ZipNames $archive
Check '合えば書き換わる' ($names -contains 'memo.txt') ($names -join ', ')
$flags = Get-FirstEntryFlags $archive
Check '書き換えた後も暗号化されている' ($flags.Encrypted)

Section 'パスワードを削除する'
Push (ById $app.Window 'PasswordButton')
$prompt = Enter-Password $app ''
Check '尋ね方' ($prompt -match '^「資料\.zip」の新しいパスワードを入力してください。') $prompt
Check '空欄の意味を伝える' ($prompt -match '空欄のままにすると、パスワードを削除します。')
Wait-Idle $app
Check 'ステータスバーに結果' ((Get-Status $app) -eq 'パスワードを削除しました') (Get-Status $app)
$flags = Get-FirstEntryFlags $archive
Check '暗号化が外れる' (-not $flags.Encrypted) "印 $($flags.Encrypted) / 方式 $($flags.Method)"

Stop-Expzip $app
Complete-Suite
