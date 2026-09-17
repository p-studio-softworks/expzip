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
    $box.SetFocus()
    Start-Sleep -Milliseconds 200
    if ($Text.Length -gt 0) { [System.Windows.Forms.SendKeys]::SendWait($Text) }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
    return $prompt.Replace("`r`n", "`n")
}

Section 'パスワードを設定する'
$app = Start-Expzip @($archive)
Push (ById $app.Window 'PasswordButton')
$prompt = Enter-Password $app $secret
Check '窓が開く' ($null -ne $prompt)
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
