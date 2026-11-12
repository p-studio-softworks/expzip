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
    # 上書きできないもの (ファイルとフォルダーが逆) しか無いときは「残りを展開しますか?」と尋ねる (#372)
    if ($box -and $box.Text -match '上書きしますか|残りを展開しますか') {
        $script:LastConfirm = $box.Text
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
# 7z は、CRC をフォルダー側にだけ持つ形でも見つける (#321)
foreach ($name in 'keep_cut.tar', 'keep_cut.tar.gz', 'keep_cut.gz', 'keep_folder_crc_bad.7z') {
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
Check '置き換えたファイルに MotW が付く'((Get-Content -LiteralPath (Join-Path $out 'plain.txt') -Stream Zone.Identifier -ErrorAction SilentlyContinue) -match 'ZoneId=3')
Check 'ハードリンクの相手は変わらない' ((Read-Text $other) -eq 'ORIGINAL') (Read-Text $other)
Check 'リンクの名前のほうは置き換わる' ((Read-Text (Join-Path $out 'linked.txt')) -eq 'NEW')
Check '閉じたアクセス権が残る' ((Get-Acl $locked).AreAccessRulesProtected) (Get-Acl $locked).Sddl
Check '書き込みを禁じたファイルは置き換えない' ((Read-Text (Join-Path $out 'denied.txt')) -eq 'ORIGINAL')
# 後始末で消せるように戻す
icacls (Join-Path $out 'denied.txt') /reset | Out-Null
# 確認に名前が出るので、それを見て「はい」と答えたなら、読み取り専用・隠し・システムでも置き換える (#372)
Check '読み取り専用も「はい」で置き換わる' ((Read-Text (Join-Path $out 'readonly.txt')) -eq 'NEW')
Check '隠しファイルも「はい」で置き換わる' ((Read-Text (Join-Path $out 'hidden.txt')) -eq 'NEW')
Check 'システムファイルも「はい」で置き換わる' ((Read-Text (Join-Path $out 'system.txt')) -eq 'NEW')
Check '上書きできなかったファイルを知らせる' ($text -match 'denied\.txt') $text
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
Check '書庫の MotW は残る' ((Get-Content -LiteralPath $zip -Stream Zone.Identifier -ErrorAction SilentlyContinue) -match 'ZoneId=3')
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
Check '一部を追加できなければ尋ねる' ($box -and $box.Text -match '1 個の項目を追加できませんでした' -and $box.Text -match '落とす\.zip を置き換えますか') $(if ($box) { $box.Text })
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

Section '1 つ目の塊が大きい tar.bz2 (#321)'
# SharpCompress に任せると形式を見分けたあとで巻き戻せず、正しい書庫なのに「壊れています」と出ていた
$path = Join-Path $script:Work 'ok_big.tar.bz2'
Copy-Item (Join-Path $fixtures 'ok_big.tar.bz2') $path
$out = New-Folder 'out_big_bz2'
$app = Start-Expzip @($path)
$text = Invoke-Overwrite $app $out
Check '取り出せる' ($text -match '展開したファイル: 1 個' -and (Get-Item (Join-Path $out 'a.txt')).Length -eq 3000000) $text
Stop-Expzip $app

Section 'CRC の無い、まとめて Deflate した 7z (#321、#323)'
# SharpCompress 0.50.7 は 2 個目以降に違う中身を返した。CRC が無いので、Windows の tar.exe で取り出したものと比べる
$path = Join-Path $script:Work 'keep_deflate_nocrc.7z'
Copy-Item (Join-Path $fixtures 'keep_deflate_nocrc.7z') $path
$out = New-Folder 'out_deflate'
$expected = New-Folder 'out_deflate_tar'
& "$env:SystemRoot\System32\tar.exe" -xf $path -C $expected
$app = Start-Expzip @($path)
$text = Invoke-Overwrite $app $out
Check '失敗が出ない' ($text -notmatch '対応していません|壊れています|書き込めなかった') $text
$names = @(Get-ChildItem $expected -File | ForEach-Object { $_.Name } | Sort-Object)
$same = @($names | Where-Object { (Test-Path (Join-Path $out $_)) -and (Get-FileHash (Join-Path $out $_)).Hash -eq (Get-FileHash (Join-Path $expected $_)).Hash })
Check 'すべて tar.exe と同じ中身' ($names.Count -eq 4 -and $same.Count -eq $names.Count) "同じ: $($same -join ', ') / 全部: $($names -join ', ')"
Stop-Expzip $app

Section 'フォルダー側の CRC を持つ正しい 7z (#321)'
foreach ($item in @(@('ok_folder_crc.7z', 'a.txt', 'NEW-CONTENT-OF-A'), @('ok_folder_crc_zero.7z', 'b.txt', 'NEW-CONTENT-OF-B-LONGER'))) {
    $path = Join-Path $script:Work $item[0]
    Copy-Item (Join-Path $fixtures $item[0]) $path
    $out = New-Folder "out_$($item[0])"
    $app = Start-Expzip @($path)
    $text = Invoke-Overwrite $app $out
    Check "失敗が出ない: $($item[0])" ($text -notmatch '書き込めなかった') $text
    Check "中身が正しい: $($item[0])" ((Read-Text (Join-Path $out $item[1])) -eq $item[2])
    Stop-Expzip $app
}
$names = @(Get-ChildItem (Join-Path $script:Work 'out_ok_folder_crc.7z') -Recurse -File | ForEach-Object { $_.Name })
Check 'フォルダーの中と空のファイルも出る' ($names -contains 'b.txt' -and $names -contains 'empty.txt') ($names -join ', ')
foreach ($name in 'bad_empty_bits.7z', 'bad_stream_count.7z', 'bad_stream_short.7z') {
    $path = Join-Path $script:Work $name
    Copy-Item (Join-Path $fixtures $name) $path
    $out = New-Folder "out_$name"
    $app = Start-Expzip
    Focus-App $app
    $center = Get-WindowCenter $app
    Invoke-Drop @($path) $center.X $center.Y | Out-Null
    $opened = Find-MessageBox $app 2500
    if ($opened) {
        Check "見出しが矛盾していれば開かない: $name" ($opened.Text -match '壊れています') $opened.Text
        Close-MessageBox $opened 'OK'
    }
    else {
        $text = Invoke-Overwrite $app $out
        Check "見出しが矛盾していれば取り出さない: $name" ($text -match '壊れています') $text
    }
    Stop-Expzip $app
}

Section '分割と結合の MotW (#321)'
$source = Join-Path $script:Work 'marked.bin'
$bytes = New-Object byte[] (2MB + 7)
(New-Object System.Random 11).NextBytes($bytes)
[System.IO.File]::WriteAllBytes($source, $bytes)
$sourceHash = (Get-FileHash $source -Algorithm SHA256).Hash
Set-Content -LiteralPath $source -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$parts = New-Folder 'marked_parts'
$app = Start-Expzip
$done = Start-Split $app
if ($done) { Close-MessageBox $done 'OK' }
Stop-Expzip $app

function Get-Zone([string]$Path) { Get-Content -LiteralPath $Path -Stream Zone.Identifier -ErrorAction SilentlyContinue }
function Invoke-Joiner {
    $process = Start-Process (Join-Path $parts 'marked.bin.exe') -ArgumentList '-q' -WorkingDirectory $parts -PassThru
    $process.WaitForExit(30000) | Out-Null
    return $process.ExitCode
}
$joined = Join-Path $parts 'marked.bin'
Check '分割ファイルに MotW が付く' ((Get-Zone (Join-Path $parts 'marked.bin.001')) -match 'ZoneId=3')
Check '結合用のプログラムには付かない' ($null -eq (Get-Zone (Join-Path $parts 'marked.bin.exe')))
# スマート アプリ コントロールが有効な PC では、署名の無い結合用のプログラムを Windows が止める。
# そのときは結合の項目を確かめずに先へ進み、止められたことを記録に残す
$blocked = $false
try { $first = Invoke-Joiner }
catch {
    if (-not (Test-AppControlBlock $_)) { throw }
    $blocked = $true
    Skip '結合できる' '結合用のプログラムを Windows に止められたため、結合の項目は確かめていません'
}
if (-not $blocked) {
    Check '結合できる' ($first -eq 0)
    Check '中身が同じ' ((Get-FileHash $joined -Algorithm SHA256).Hash -eq $sourceHash)
    Check '結合したファイルに MotW が付く' ((Get-Zone $joined) -match 'ZoneId=3')
    Check '結合の一時ファイルが残らない' ((Get-Leftovers $parts).Count -eq 0) ((Get-Leftovers $parts) -join ', ')

    # 同じ名前があれば結合せず、元のファイルを残す
    Write-Text $joined 'ORIGINAL-JOINED'
    Check '同じ名前があれば失敗する' ((Invoke-Joiner) -ne 0)
    Check '同じ名前のファイルは元のまま' ((Read-Text $joined) -eq 'ORIGINAL-JOINED')
    [System.IO.File]::Delete($joined)

    # 一部の分割ファイルだけに MotW があっても引き継ぐ
    Remove-Item -LiteralPath (Join-Path $parts 'marked.bin.001') -Stream Zone.Identifier
    Remove-Item -LiteralPath (Join-Path $parts 'marked.bin.003') -Stream Zone.Identifier
    Check '1 個目に MotW が無くても結合できる' ((Invoke-Joiner) -eq 0)
    Check '一部にだけ MotW があっても引き継ぐ' ((Get-Zone $joined) -match 'ZoneId=3')
    [System.IO.File]::Delete($joined)

    # 弱い MotW が混ざっていても、いちばん強い MotW を使う
    Set-Content -LiteralPath (Join-Path $parts 'marked.bin.001') -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=2"
    Invoke-Joiner | Out-Null
    Check 'いちばん強い MotW を使う' ((Get-Zone $joined) -match 'ZoneId=3')
    [System.IO.File]::Delete($joined)

    # 壊れていれば本来の名前に何も残らない
    $second = Join-Path $parts 'marked.bin.002'
    $part = [System.IO.File]::ReadAllBytes($second)
    $part[100] = $part[100] -bxor 0xFF
    [System.IO.File]::WriteAllBytes($second, $part)
    Check '壊れていれば失敗する' ((Invoke-Joiner) -ne 0)
    Check '本来の名前に残らない' (-not (Test-Path $joined))
    Check '壊れたときも一時ファイルが残らない' ((Get-Leftovers $parts).Count -eq 0) ((Get-Leftovers $parts) -join ', ')
}

Section 'MotW を保存できない場所 (#321)'
# 下のファイルの代替データストリームだけを書けなくしたフォルダー。exFAT などと同じく、MotW だけが付かない
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Deny-Marks([string]$Folder) { icacls $Folder /deny "*${me}:(OI)(IO)(WD)" | Out-Null }
function Allow-Marks([string]$Folder) { icacls $Folder /remove:d "*$me" | Out-Null }

$zip = New-TestZip (Join-Path $script:Work 'net.zip') ([ordered]@{ 'one.txt' = 'NEW'; 'two.txt' = 'NEW' })
Set-Content -LiteralPath $zip -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$out = New-Folder 'nomark_out'
Deny-Marks $out
$app = Start-Expzip @($zip)
$text = Invoke-Overwrite $app $out
$warning = 'このドライブには、インターネットから入手したことを示す情報 \(Mark of the Web\) を保存できません。'
Check '警告を出す' ($text -match $warning) $text
Check '1 回だけ出す' (([regex]::Matches($text, $warning)).Count -eq 1) $text
Check '取り出しは止めない' ($text -match '展開したファイル: 2 個' -and (Test-Path (Join-Path $out 'two.txt'))) $text
Stop-Expzip $app
Allow-Marks $out

$out = New-Folder 'mark_out'
$weak = New-TestZip (Join-Path $script:Work 'intranet.zip') ([ordered]@{ 'one.txt' = 'NEW' })
Set-Content -LiteralPath $weak -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=1"
$denied = New-Folder 'nomark_weak'
Deny-Marks $denied
$app = Start-Expzip @($weak)
$text = Invoke-Overwrite $app $denied
Check '社内サイトなどの弱い MotW では出さない' ($text -notmatch $warning) $text
Stop-Expzip $app
Allow-Marks $denied

$app = Start-Expzip @($zip)
$text = Invoke-Overwrite $app $out
Check 'ふつうのフォルダーでは出さない' ($text -notmatch $warning) $text
Stop-Expzip $app

# 一時フォルダーに MotW を付けられないとき、実行ファイルを開く前に確かめる
$exeZip = New-TestZip (Join-Path $script:Work 'run.zip') ([ordered]@{ 'run.bat' = 'exit' })
Set-Content -LiteralPath $exeZip -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$temp = New-Folder 'nomark_temp'
Deny-Marks $temp
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$env:TEMP = $temp
$env:TMP = $temp
try {
    $app = Start-Expzip @($exeZip)
    Select-Row $app 'run.bat' | Out-Null
    Send-Keys $app '{ENTER}'
    $box = Find-MessageBox $app 10000
    Check '実行ファイルのふだんの確認' ($box -and $box.Text -match 'プログラムとして実行されます') $(if ($box) { $box.Text })
    if ($box) { Close-MessageBox $box 'OK' }
    $box = Find-MessageBox $app 15000
    Check 'MotW が無いことも確かめる' ($box -and $box.Text -match 'Mark of the Web\) を保存できませんでした') $(if ($box) { $box.Text })
    if ($box) { Close-MessageBox $box 'キャンセル' }
    Wait-Idle $app
    Check 'キャンセルしたら取り出したファイルを残さない' (@(Get-ChildItem $temp -Recurse -File -Filter 'run.bat').Count -eq 0)
    Select-Row $app 'run.bat' | Out-Null
    Send-Keys $app '{ENTER}'
    $box = Find-MessageBox $app 10000
    if ($box) { Close-MessageBox $box 'OK' }
    $box = Find-MessageBox $app 15000
    Check 'もう一度開いても確かめる' ($box -and $box.Text -match 'Mark of the Web\) を保存できませんでした') $(if ($box) { $box.Text })
    if ($box) { Close-MessageBox $box 'キャンセル' }
    Stop-Expzip $app
}
finally {
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    Allow-Marks $temp
}

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

Section '新しく作るファイルは直接書く (#328)'
# 一時的な名前から置き換えると、名前の変更のたびにウイルス対策の検査を待つ。同じ名前が無ければ本来の名前に書く
$fresh = New-TestZip (Join-Path $script:Work 'fresh.zip') ([ordered]@{
    'ok.txt' = 'NEW'; 'sub/deep.txt' = 'DEEP'; 'CON.txt' = 'FILE'; 'COM1' = 'FILE'; 'nul' = 'DEVICE'; 'sub/NUL ' = 'DEVICE'; 'sub/nul.' = 'DEVICE'
})
Set-Content -LiteralPath $fresh -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$out = New-Folder 'fresh_out'
$app = Start-Expzip @($fresh)
$text = Invoke-Overwrite $app $out
Check '中身が書かれる' ((Read-Text (Join-Path $out 'ok.txt')) -eq 'NEW' -and (Read-Text (Join-Path $out 'sub\deep.txt')) -eq 'DEEP')
Check 'MotW が付く' ((Get-Zone (Join-Path $out 'ok.txt')) -match 'ZoneId=3') ((Get-Zone (Join-Path $out 'ok.txt')) -join ' ')
Check '一時ファイルが残らない' ((Get-Leftovers $out).Count -eq 0 -and (Get-Leftovers (Join-Path $out 'sub')).Count -eq 0)
# この Windows で装置を指す名前だけを取り出さない。\\?\ で並べると、装置ではなくファイルとしての名前も見える
$names = @([System.IO.Directory]::GetFiles("\\?\$out", '*', 'AllDirectories') | ForEach-Object { [System.IO.Path]::GetFileName($_) })
Check '装置を指す名前のファイルは作らない' (@($names | Where-Object { $_ -match '^nul' }).Count -eq 0) ($names -join ', ')
# 装置のパス (\\.\nul) に読み替えられるので、展開先の外に書く項目として断られる
Check '装置を指す名前を知らせる' ($text -match '(?m)^  nul$' -and $text -match 'sub/NUL ' -and $text -match 'sub/nul\.') $text
# Windows 11 では装置にならない。古い Windows では CON.txt も装置になり、上の知らせに入る
foreach ($name in 'CON.txt', 'COM1') {
    Check "装置にならない名前はふつうに取り出す: $name" (($names -contains $name) -or $text -match [regex]::Escape($name)) ($names -join ', ')
}
Stop-Expzip $app

Section '新しく作るファイルで失敗させる (#328)'
# 直接書いたものは「閉じたら消す」で消える。名前では消さない
$out = New-Folder 'bad_fresh_out'
$app = Start-Expzip @($bad)
$text = Invoke-Overwrite $app $out
Check '失敗を知らせる' ($text -match '書庫のデータが壊れています') $text
Check '読めなかったファイルは残らない' (-not (Test-Path (Join-Path $out 'a.txt'))) (@([System.IO.Directory]::GetFiles($out)) -join ', ')
Check '読めたファイルはできる' ((Read-Text (Join-Path $out 'b.txt')) -eq 'NEW-CONTENT-OF-B')
Check '更新日時は書庫に書かれたもの' ((Get-Item (Join-Path $out 'b.txt')).LastWriteTime.Year -eq 2001) (Get-Item (Join-Path $out 'b.txt')).LastWriteTime
Check '一時ファイルが残らない' ((Get-Leftovers $out).Count -eq 0) ((Get-Leftovers $out) -join ', ')
Stop-Expzip $app

Section '展開先の同じ名前にジャンクション (#328)'
# ジャンクションは権利が無くても作れる。たどらずに置き換えの道に移り、フォルダーなので置き換えない
$outside = New-Folder 'junction_outside'
Write-Text (Join-Path $outside 'kept.txt') 'OUTSIDE'
$out = New-Folder 'junction_out'
New-Item -ItemType Junction -Path (Join-Path $out 'a.txt') -Target $outside | Out-Null
$junctionZip = New-TestZip (Join-Path $script:Work 'junction.zip') ([ordered]@{ 'a.txt' = 'NEW'; 'b.txt' = 'NEW' })
$app = Start-Expzip @($junctionZip)
$script:LastConfirm = $null
$text = Invoke-Overwrite $app $out
# ジャンクションはフォルダーなので、ファイルの a.txt では上書きできない。確認の中で前もって挙げる (#372)
Check '上書きできないものを確認で挙げる' ($script:LastConfirm -match '上書きできません' -and $script:LastConfirm -match 'a\.txt') $script:LastConfirm
$inside = @(Get-ChildItem $outside -Force | ForEach-Object { $_.Name })
Check 'ジャンクションの先は変わらない' ($inside.Count -eq 1 -and (Read-Text (Join-Path $outside 'kept.txt')) -eq 'OUTSIDE') ($inside -join ', ')
Check 'ジャンクションは残る' ((Get-Item (Join-Path $out 'a.txt') -Force).LinkType -eq 'Junction')
Check 'ほかのファイルはできる' ((Read-Text (Join-Path $out 'b.txt')) -eq 'NEW')
Check '置き換えなかったことを知らせる' ($text -match 'a\.txt') $text
Check '一時ファイルが残らない' ((Get-Leftovers $out).Count -eq 0) ((Get-Leftovers $out) -join ', ')
Stop-Expzip $app
# 後始末でジャンクションの先を消さないように、リンクだけ外す
[System.IO.Directory]::Delete((Join-Path $out 'a.txt'))

Section '展開先に置かれたリンク (#328)'
# 同じ名前にリンクがあれば、たどらずに置き換えの道に移る。リンクの先 (展開先の外) には書かない
$outside = New-Folder 'link_outside'
Write-Text (Join-Path $outside 'kept.txt') 'OUTSIDE'
$out = New-Folder 'link_out'
$linked = $true
try {
    New-Item -ItemType SymbolicLink -Path (Join-Path $out 'a.txt') -Target (Join-Path $outside 'kept.txt') -ErrorAction Stop | Out-Null
    New-Item -ItemType SymbolicLink -Path (Join-Path $out 'b.txt') -Target (Join-Path $outside 'missing.txt') -ErrorAction Stop | Out-Null
}
catch { $linked = $false }
if ($linked) {
    $links = New-TestZip (Join-Path $script:Work 'links.zip') ([ordered]@{ 'a.txt' = 'NEW'; 'b.txt' = 'NEW' })
    $app = Start-Expzip @($links)
    $text = Invoke-Overwrite $app $out
    Check 'リンクの先のファイルは変わらない' ((Read-Text (Join-Path $outside 'kept.txt')) -eq 'OUTSIDE') (Read-Text (Join-Path $outside 'kept.txt'))
    Check '壊れたリンクの先にファイルを作らない' (-not (Test-Path (Join-Path $outside 'missing.txt')))
    Check '一時ファイルが残らない' ((Get-Leftovers $out).Count -eq 0) ((Get-Leftovers $out) -join ', ')
    Stop-Expzip $app
}
else {
    Write-Host '  (シンボリックリンクを作る権利が無いので飛ばす)'
}

Section '強制終了で書きかけが残らない (#328)'
# 書いている間は「閉じたら消す」を予約してある。終了させると Windows が消す
$bigPath = Join-Path $script:Work 'big.zip'
Remove-Item $bigPath -Force -ErrorAction SilentlyContinue
$stream = [System.IO.File]::Open($bigPath, [System.IO.FileMode]::CreateNew)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('big.bin', [System.IO.Compression.CompressionLevel]::Fastest)
        $output = $entry.Open()
        $chunk = New-Object byte[] (1MB)
        for ($i = 0; $i -lt 3072; $i++) { $output.Write($chunk, 0, $chunk.Length) }
        $output.Dispose()
    }
    finally { $zip.Dispose() }
}
finally { $stream.Dispose() }
$out = New-Folder 'kill_out'
$app = Start-Expzip @($bigPath)
Focus-App $app
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $out | Out-Null
$appeared = Wait-Until -TimeoutMs 30000 { [System.IO.Directory]::GetFiles($out).Count -gt 0 }
$app.Process.Kill()
$app.Process.WaitForExit(10000) | Out-Null
Check '書き始めてから終了させた' $appeared
$gone = Wait-Until -TimeoutMs 10000 { [System.IO.Directory]::GetFiles($out).Count -eq 0 }
Check '書きかけが残らない' $gone ([System.IO.Directory]::GetFiles($out) -join ', ')

Section '設定の保存'
$settings = Get-ChildItem $script:Work -Force -Filter 'Expzip.settings.json' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($settings) {
    Check '設定のフォルダーに一時ファイルが残らない' ((Get-Leftovers $settings.DirectoryName).Count -eq 0) ((Get-Leftovers $settings.DirectoryName) -join ', ')
}

Complete-Suite
