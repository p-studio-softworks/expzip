# 失敗しても、元のファイルは残る (#320)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'KeepFiles'
Set-Settings

$fixtures = Join-Path $PSScriptRoot '..\fixtures'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText($Path) }
function Write-Text([string]$Path, [string]$Text) { [System.IO.File]::WriteAllText($Path, $Text) }
function Get-Leftovers([string]$Folder) { @(Get-ChildItem $Folder -Force -Filter '~*.tmp' | ForEach-Object { $_.Name }) }

function New-Folder([string]$Name) {
    $path = Join-Path $script:Work $Name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

# 展開ボタンから、上書きを「はい」で答えて展開する。結果の知らせの文を返す
function Invoke-Overwrite($App, [string]$Destination) {
    Focus-App $App
    Push (ById $App.Window 'ExtractButton')
    Complete-FileDialog $App '書庫全体の展開先を選択' $Destination | Out-Null
    $box = Find-MessageBox $App 30000
    if ($box -and $box.Text -match '上書きしますか') {
        Close-MessageBox $box 'はい(Y)'
        $box = Find-MessageBox $App 30000
    }
    if ($null -eq $box) { return $null }
    $text = $box.Text
    Close-MessageBox $box 'OK'
    return $text
}

# ファイルのダイアログの上書きの確認に「はい」で答える
function Confirm-FileDialogOverwrite($App, [string]$Title) {
    # 確認はファイルのダイアログの子として見える。ボタンのクラス名は Button ではない
    $yes = Wait-Until -TimeoutMs 5000 {
        Get-AppWindows $App | Where-Object { $_.Current.ClassName -eq '#32770' } | ForEach-Object {
            Find-All $_ ([System.Windows.Automation.Condition]::TrueCondition)
        } | Where-Object { $_.Current.Name -match '^はい' -and $_.Current.NativeWindowHandle -ne 0 } | Select-Object -First 1
    }
    if ($null -eq $yes) { return $false }
    [ExpzipUi.Native]::PostMessage([IntPtr]$yes.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 800
    return $true
}

Section '壊れた項目で上書きを失敗させる'
$bad = Join-Path $script:Work 'bad.zip'
Copy-Item (Join-Path $fixtures 'keep_bad_crc.zip') $bad
$out = New-Folder 'bad_out'
Write-Text (Join-Path $out 'a.txt') 'ORIGINAL-A'
Write-Text (Join-Path $out 'b.txt') 'ORIGINAL-B'
$app = Start-Expzip @($bad)
$text = Invoke-Overwrite $app $out
Check '失敗を知らせる' ($text -match '書庫のデータが壊れています') $text
Check '読めなかったファイルは元のまま' ((Read-Text (Join-Path $out 'a.txt')) -eq 'ORIGINAL-A') (Read-Text (Join-Path $out 'a.txt'))
Check '読めたファイルは置き換わる' ((Read-Text (Join-Path $out 'b.txt')) -eq 'NEW-CONTENT-OF-B') (Read-Text (Join-Path $out 'b.txt'))
Check '更新日時は書庫に書かれたもの' ((Get-Item (Join-Path $out 'b.txt')).LastWriteTime.Year -eq 2001) (Get-Item (Join-Path $out 'b.txt')).LastWriteTime
Check '一時ファイルが残らない' ((Get-Leftovers $out).Count -eq 0) ((Get-Leftovers $out) -join ', ')
Stop-Expzip $app

Section '途中で切れた書庫で上書きを失敗させる'
# 読み手が黙って終わるので、切れた中身が成功として既存のファイルを置き換えていた
foreach ($name in 'keep_cut.tar', 'keep_cut.tar.gz', 'keep_cut.gz') {
    $cut = Join-Path $script:Work $name
    Copy-Item (Join-Path $fixtures $name) $cut
    $out = New-Folder "out_$name"
    Write-Text (Join-Path $out 'a.txt') 'ORIGINAL-A'
    $app = Start-Expzip
    Focus-App $app
    $center = Get-WindowCenter $app
    Invoke-Drop @($cut) $center.X $center.Y | Out-Null
    $opened = Find-MessageBox $app 2500
    if ($opened) {
        # 切れた gz は、開く時点で断る
        Check "開けないと知らせる: $name" ($opened.Text -match '壊れています') $opened.Text
        Close-MessageBox $opened 'OK'
    }
    else {
        $text = Invoke-Overwrite $app $out
        Check "失敗を知らせる: $name" ($text -match '書庫のデータが壊れています') $text
    }
    Check "元のまま: $name" ((Read-Text (Join-Path $out 'a.txt')) -eq 'ORIGINAL-A') (Read-Text (Join-Path $out 'a.txt'))
    Check "一時ファイルが残らない: $name" ((Get-Leftovers $out).Count -eq 0) ((Get-Leftovers $out) -join ', ')
    Stop-Expzip $app
}

Section '置き換えるファイル'
$zip = New-TestZip (Join-Path $script:Work 'ok.zip') ([ordered]@{
    'plain.txt' = 'NEW'; 'linked.txt' = 'NEW'; 'locked.txt' = 'NEW'; 'readonly.txt' = 'NEW'; 'hidden.txt' = 'NEW'; 'system.txt' = 'NEW'; 'denied.txt' = 'NEW'
})
Set-Content -LiteralPath $zip -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$out = New-Folder 'ok_out'
foreach ($name in 'plain', 'linked', 'locked', 'readonly', 'hidden', 'system', 'denied') { Write-Text (Join-Path $out "$name.txt") 'ORIGINAL' }

# ハードリンクの相手を書き換えない
$other = Join-Path $out 'other_name.dat'
New-Item -ItemType HardLink -Path $other -Target (Join-Path $out 'linked.txt') | Out-Null

# 個別に閉じたアクセス権を引き継ぐ
$locked = Join-Path $out 'locked.txt'
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $locked /inheritance:r /grant "*${me}:F" | Out-Null

# アクセス権で書き込みを禁じたファイル。名前の変更では置き換えられてしまう
icacls (Join-Path $out 'denied.txt') /deny "*${me}:(WD,AD)" | Out-Null

Set-ItemProperty (Join-Path $out 'readonly.txt') IsReadOnly $true
(Get-Item (Join-Path $out 'hidden.txt')).Attributes = 'Hidden'
(Get-Item (Join-Path $out 'system.txt')).Attributes = 'System'

$app = Start-Expzip @($zip)
$text = Invoke-Overwrite $app $out
Check 'ふつうのファイルは置き換わる' ((Read-Text (Join-Path $out 'plain.txt')) -eq 'NEW')
Check '置き換えたファイルに出所の印が付く'((Get-Content -LiteralPath (Join-Path $out 'plain.txt') -Stream Zone.Identifier -ErrorAction SilentlyContinue) -match 'ZoneId=3')
Check 'ハードリンクの相手は変わらない' ((Read-Text $other) -eq 'ORIGINAL') (Read-Text $other)
Check 'リンクの名前のほうは置き換わる' ((Read-Text (Join-Path $out 'linked.txt')) -eq 'NEW')
Check '閉じたアクセス権が残る' ((Get-Acl $locked).AreAccessRulesProtected) (Get-Acl $locked).Sddl
Check '書き込みを禁じたファイルは置き換えない' ((Read-Text (Join-Path $out 'denied.txt')) -eq 'ORIGINAL')
# 後始末で消せるように戻す
icacls (Join-Path $out 'denied.txt') /reset | Out-Null
Check '読み取り専用は置き換えない'((Read-Text (Join-Path $out 'readonly.txt')) -eq 'ORIGINAL')
Check '隠しファイルは置き換えない' ((Read-Text (Join-Path $out 'hidden.txt')) -eq 'ORIGINAL')
Check 'システムファイルは置き換えない' ((Read-Text (Join-Path $out 'system.txt')) -eq 'ORIGINAL')
Check '置き換えなかったファイルを知らせる' ($text -match 'readonly\.txt' -and $text -match 'hidden\.txt') $text
Check '一時ファイルが残らない' ((Get-Leftovers $out).Count -eq 0) ((Get-Leftovers $out) -join ', ')

Section '編集の作業用ファイル'
# 決まった名前を使っていたころは、同じ名前の他人のファイルを空にして消していた
$neighbor = "$zip.expzip-tmp"
Write-Text $neighbor 'NEIGHBOR'
$extra = Join-Path $script:Work 'extra.txt'
Write-Text $extra 'extra'
Focus-App $app
$center = Get-WindowCenter $app
Invoke-Drop @($extra) $center.X $center.Y | Out-Null
Wait-Idle $app
Check '書庫に入る' ((Get-ZipNames $zip) -contains 'extra.txt') ((Get-ZipNames $zip) -join ', ')
Check '同じ名前のファイルは変わらない' ((Test-Path $neighbor) -and (Read-Text $neighbor) -eq 'NEIGHBOR')
Check '書庫の印は残る' ((Get-Content -LiteralPath $zip -Stream Zone.Identifier -ErrorAction SilentlyContinue) -match 'ZoneId=3')
Stop-Expzip $app

Section '分割'
$source = Join-Path $script:Work 'split.bin'
$bytes = New-Object byte[] (2MB + 5)
(New-Object System.Random 3).NextBytes($bytes)
[System.IO.File]::WriteAllBytes($source, $bytes)
$parts = New-Folder 'parts'
$joiner = Join-Path $parts 'split.bin.exe'
Write-Text $joiner 'UNRELATED'

function Start-Split($App) {
    Push (ById $App.Window 'SplitButton')
    $dialog = Find-Window $App 'ファイルの分割'
    Set-Text (ById $dialog 'SourceBox') $source
    Set-Text (ById $dialog 'DestinationBox') $parts
    $unit = ById $dialog 'UnitCombo'
    Expand-Element $unit
    Select-Element (ByName $unit 'MB')
    Set-Text (ById $dialog 'SizeBox') '1'
    Push (ById $dialog 'SplitButton')
    return Find-MessageBox $App 30000
}

$app = Start-Expzip
$box = Start-Split $app
Check '同じ名前があれば尋ねる' ($box -and $box.Text -match '^保存先に同じ名前のファイルが 1 個あります。置き換えますか\?' -and $box.Text -match 'split\.bin\.exe') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'いいえ(N)' }
Wait-Idle $app
Check 'いいえなら作らない' ((Read-Text $joiner) -eq 'UNRELATED' -and -not (Test-Path (Join-Path $parts 'split.bin.001')))
$box = Start-Split $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
$done = Find-MessageBox $app 30000
Check 'はいなら分割する' ($done -and $done.Text -match '^3 個に分割しました。') $(if ($done) { $done.Text })
if ($done) { Close-MessageBox $done 'OK' }
Check '結合用のプログラムに置き換わる' ((Get-Item $joiner).Length -gt 1000)
Check '一時ファイルが残らない' ((Get-Leftovers $parts).Count -eq 0) ((Get-Leftovers $parts) -join ', ')
Stop-Expzip $app

Section '既にある書庫を、落としたもので置き換える'
$folder = New-Folder '落とす'
Write-Text (Join-Path $folder 'ok.txt') 'ok'
$busy = Join-Path $folder 'busy.txt'
Write-Text $busy 'busy'
$target = Join-Path $script:Work '落とす.zip'
$before = New-TestZip $target ([ordered]@{ 'old.txt' = 'old' })
$title = '新しい書庫を作成'

function Start-DropOverwrite($App, [string[]]$Paths) {
    Focus-App $App
    $center = Get-WindowCenter $App
    Invoke-Drop $Paths $center.X $center.Y | Out-Null
    $box = Find-MessageBox $App
    if ($box) { Close-MessageBox $box 'はい(Y)' }
    Complete-FileDialog $App $title $target | Out-Null
    return Confirm-FileDialogOverwrite $App $title
}

# 読めないファイルを作っておく
$holder = [System.IO.File]::Open($busy, 'Open', 'Read', 'None')
$app = Start-Expzip

Check '上書きを確かめる' (Start-DropOverwrite $app @($folder))
$box = Find-MessageBox $app 30000
Check '一部を追加できなければ尋ねる' ($box -and $box.Text -match '1 個のファイルを追加できませんでした' -and $box.Text -match '落とす\.zip を置き換えますか') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'いいえ(N)' }
Wait-Idle $app
Check 'いいえなら元の書庫のまま' ((Get-ZipNames $target) -contains 'old.txt') ((Get-ZipNames $target) -join ', ')
Check 'タブは開かない' ((Get-Tabs $app).Count -eq 0) (Get-Tabs $app).Count

Start-DropOverwrite $app @($busy) | Out-Null
$box = Find-MessageBox $app 30000
Check '全部を追加できなければ知らせる' ($box -and $box.Text -match '追加したファイル: 0 個') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'OK' }
Check '元の書庫のまま' ((Get-ZipNames $target) -contains 'old.txt') ((Get-ZipNames $target) -join ', ')

Start-DropOverwrite $app @($folder) | Out-Null
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'はい(Y)' }
Wait-Idle $app
$names = Get-ZipNames $target
Check 'はいなら置き換わる' ($names -contains '落とす/ok.txt' -and $names -notcontains 'old.txt') ($names -join ', ')
Check 'タブで開く' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Check '一時ファイルが残らない' ((Get-Leftovers $script:Work).Count -eq 0) ((Get-Leftovers $script:Work) -join ', ')
$holder.Dispose()
Stop-Expzip $app

# 保存先が追加するフォルダーの中でも、作っている書庫そのものは入らない
Section '保存先が追加するフォルダーの中'
$app = Start-Expzip
$inside = Join-Path $folder '中.zip'
Focus-App $app
$center = Get-WindowCenter $app
Invoke-Drop @($folder) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
Complete-FileDialog $app $title $inside | Out-Null
Wait-Idle $app
$names = Get-ZipNames $inside
Check '中身が入る' ($names -contains '落とす/ok.txt') ($names -join ', ')
Check '作っている書庫は入らない' (-not ($names | Where-Object { $_ -match '\.tmp$|中\.zip$' })) ($names -join ', ')
Stop-Expzip $app

Section '固まって閉じる'
# 取り出しを始めると、書庫を開く処理が止まったまま戻らない。中断も効かない
$hang = New-TestZip (Join-Path $script:Work 'hang.zip') ([ordered]@{ 'a.txt' = 'NEW' })
$out = New-Folder 'hang_out'
Write-Text (Join-Path $out 'a.txt') 'ORIGINAL-A'
$app = Start-Expzip @($hang)
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $out | Out-Null
$box = Find-MessageBox $app 30000
$stall = Stop-FileReads $hang
if ($box) { Close-MessageBox $box 'はい(Y)' }
Start-Sleep -Milliseconds 1500
Check '取り出しが止まっている' (-not $app.Process.HasExited -and $null -eq (Find-MessageBox $app 500))
$app.Process.CloseMainWindow() | Out-Null
Check '閉じる操作で終了する' ($app.Process.WaitForExit(20000))
if (-not $app.Process.HasExited) { $app.Process.Kill() }
Resume-FileReads $stall
Check '元のファイルは残る' ((Read-Text (Join-Path $out 'a.txt')) -eq 'ORIGINAL-A') (Read-Text (Join-Path $out 'a.txt'))
Check '一時ファイルが残らない' ((Get-Leftovers $out).Count -eq 0) ((Get-Leftovers $out) -join ', ')

Section '設定の保存'
$settings = Get-ChildItem $script:Work -Force -Filter 'Expzip.settings.json' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($settings) {
    Check '設定のフォルダーに一時ファイルが残らない' ((Get-Leftovers $settings.DirectoryName).Count -eq 0) ((Get-Leftovers $settings.DirectoryName) -join ', ')
}

Complete-Suite
