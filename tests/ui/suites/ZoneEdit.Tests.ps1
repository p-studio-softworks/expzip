# 出所の印の読み方 (#325) の続き。開いたままの印、分割された書庫、編集と元に戻す。1 つのスイートの制限時間に収めるため Zone から分けた
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'ZoneEdit'
Set-Settings

$fixtures = Join-Path $PSScriptRoot '..\fixtures'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText($Path) }
function Write-Text([string]$Path, [string]$Text) { [System.IO.File]::WriteAllText($Path, $Text) }

function New-Folder([string]$Name) {
    $path = Join-Path $script:Work $Name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Invoke-Overwrite($App, [string]$Destination) {
    Focus-App $App
    Wait-Until { ById $App.Window 'ExtractButton' } | Out-Null
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

# 印のストリームを共有なしで開く。読み取りを失敗させる
if (-not ('ExpzipUi.ZoneLock' -as [type])) {
    Add-Type -Namespace ExpzipUi -Name ZoneLock -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
public static IntPtr Open(string path) {
    IntPtr h = CreateFileW(path + ":Zone.Identifier", 0x80000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
    if (h == new IntPtr(-1)) { throw new System.IO.IOException("open " + Marshal.GetLastWin32Error()); }
    return h;
}
'@
}


# 印は決まったバイト列で書く
$sample = "[ZoneTransfer]`r`nZoneId=3`r`n"
$browser = "[ZoneTransfer]`r`nZoneId=3`r`nReferrerUrl=https://example.com/`r`nHostUrl=https://example.com/a.zip`r`nHostIpAddress=192.0.2.1`r`n"
$exact = "[ZoneTransfer]`r`nZoneId=1`r`nHostUrl=`r`n"
$exact = $exact.Replace("HostUrl=", "HostUrl=" + ('a' * (4096 - $exact.Length)))
$latin = [System.Text.Encoding]::GetEncoding(28591)
function Ascii([string]$Text) { return [byte[]]$latin.GetBytes($Text) }

function Set-Mark([string]$Path, [byte[]]$Bytes) {
    # 空の印も作れるように、Set-Content ではなくストリームを直接書く
    if ($Bytes.Length -eq 0) { Set-Content -LiteralPath $Path -Stream Zone.Identifier -Value ([byte[]]@()) -Encoding Byte } else { Set-Content -LiteralPath $Path -Stream Zone.Identifier -Value $Bytes -Encoding Byte }
}
function Get-MarkBytes([string]$Path) {
    $read = Get-Content -LiteralPath $Path -Stream Zone.Identifier -Encoding Byte -Raw -ErrorAction SilentlyContinue
    if ($null -eq $read) { return ,[byte[]]@() }
    return ,([byte[]]$read)
}
function Test-Same([byte[]]$A, [byte[]]$B) {
    if ($A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}
[byte[]]$sampleBytes = Ascii $sample

$plainZip = New-TestZip (Join-Path $script:Work 'plain_for_mark.zip') ([ordered]@{ 'one.txt' = 'NEW' })

Section '印を別のプログラムが開いている間 (#325)'
$held = New-TestZip (Join-Path $script:Work 'held.zip') ([ordered]@{ 'one.txt' = 'NEW' })
$out = New-Folder 'held_out'
Set-Mark $held (Ascii "[ZoneTransfer]`r`nZoneId=3`r`n")
$app = Start-Expzip @($held)
Wait-Idle $app
$lock = New-Object System.IO.FileStream -ArgumentList (New-Object Microsoft.Win32.SafeHandles.SafeFileHandle ([ExpzipUi.ZoneLock]::Open($held), $true)), 'Read'
try {
    $text = Invoke-Overwrite $app $out
    Check '読めなくてもインターネットの印が付く' (Test-Same (Get-MarkBytes (Join-Path $out 'one.txt')) $sampleBytes) ((Get-MarkBytes (Join-Path $out 'one.txt')).Length)
}
finally { $lock.Dispose() }
Stop-Expzip $app

Section '分割された書庫の印の組み合わせ (#325)'
$bin = New-Object byte[] 4000
$pairs = @(
    [pscustomobject]@{ N = '読めない印と読める 3'; A = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=9`r`n")); B = [byte[]]((Ascii $browser)); W = [byte[]]((Ascii $browser)) }
    [pscustomobject]@{ N = '読める 3 と読めない印'; A = [byte[]]((Ascii $browser)); B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=9`r`n")); W = [byte[]]((Ascii $browser)) }
    [pscustomobject]@{ N = '読めない印と 1'; A = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=9`r`n")); B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=1`r`n")); W = [byte[]]($sampleBytes) }
    [pscustomobject]@{ N = '1 と読めない印'; A = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=1`r`n")); B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=9`r`n")); W = [byte[]]($sampleBytes) }
    [pscustomobject]@{ N = '細工した 4 と本物の 3'; A = [byte[]]((Ascii "[ZoneTransfer]`r`nnoequal`r`nZoneId=4`r`n")); B = [byte[]]((Ascii $browser)); W = [byte[]]((Ascii $browser)) }
    [pscustomobject]@{ N = '本物の 3 と 0 が隠れた 4'; A = [byte[]]((Ascii $browser)); B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=0`r`n [ZoneTransfer]`r`nZoneId=4`r`n")); W = [byte[]]((Ascii $browser)) }
    [pscustomobject]@{ N = '節の内側の空白で 0 が隠れた 4 と本物の 3'; A = [byte[]]((Ascii "[ ZoneTransfer]`r`nZoneId=0`r`n[ZoneTransfer]`r`nZoneId=4`r`n")); B = [byte[]]((Ascii $browser)); W = [byte[]]((Ascii $browser)) }
    [pscustomobject]@{ N = '2 と 3'; A = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=2`r`n")); B = [byte[]]((Ascii $browser)); W = [byte[]]((Ascii $browser)) }
)
$whole = [System.IO.File]::ReadAllBytes($plainZip)
$cut = [int]($whole.Length / 2)
$index = 0
foreach ($pair in $pairs) {
    $index++
    $folder = New-Folder "split_mark_$index"
    $first = Join-Path $folder 'a.zip.001'
    $second = Join-Path $folder 'a.zip.002'
    [System.IO.File]::WriteAllBytes($first, ([byte[]]$whole[0..($cut - 1)]))
    [System.IO.File]::WriteAllBytes($second, ([byte[]]$whole[$cut..($whole.Length - 1)]))
    Set-Mark $first $pair.A
    Set-Mark $second $pair.B
    $out = New-Folder "split_mark_out_$index"
    $app = Start-Expzip @($first)
    $text = Invoke-Overwrite $app $out
    Check "組み合わせ: $($pair.N)" (Test-Same (Get-MarkBytes (Join-Path $out 'one.txt')) $pair.W) ((Get-MarkBytes (Join-Path $out 'one.txt')).Length)
    Stop-Expzip $app
}

Section '分割された ZIP からは自己解凍書庫を作らない (#325)'
$folder = New-Folder 'split_sfx'
$first = Join-Path $folder 'a.zip.001'
[System.IO.File]::WriteAllBytes($first, ([byte[]]$whole[0..($cut - 1)]))
[System.IO.File]::WriteAllBytes((Join-Path $folder 'a.zip.002'), ([byte[]]$whole[$cut..($whole.Length - 1)]))
$app = Start-Expzip @($first)
Wait-Idle $app
Push (ById $app.Window 'SfxButton')
$box = Find-MessageBox $app 8000
Check '理由を出す' ($box -and $box.Text -match '分割された書庫は自己解凍書庫にできません。') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'OK' }
Check '保存先を尋ねない' ($null -eq (Find-Window $app '自己解凍書庫として保存' 1500))
Stop-Expzip $app

Section '編集で書庫の読めない印を書き換える (#325)'
$edit = New-TestZip (Join-Path $script:Work 'editmark.zip') ([ordered]@{ 'one.txt' = 'NEW' })
Set-Mark $edit (Ascii "[ZoneTransfer]`r`nzoneid=1`r`n")
$oldTime = [datetime]::new(2020, 1, 2, 3, 4, 6, [DateTimeKind]::Utc)
[System.IO.File]::SetLastWriteTimeUtc($edit, $oldTime)
$extra = Join-Path $script:Work 'editmark_add.txt'
Write-Text $extra 'add'
$app = Start-Expzip @($edit)
Wait-Idle $app
$center = Get-WindowCenter $app
Invoke-Drop @($extra) $center.X $center.Y | Out-Null
Wait-Idle $app
Check '読めない印がインターネットの印になる' (Test-Same (Get-MarkBytes $edit) $sampleBytes) ((Get-MarkBytes $edit).Length)

# 元に戻しても読めない印は戻らず、書庫の更新日時は元のまま
Focus-App $app
(ById $app.Window 'EntryList').SetFocus()
Send-Keys $app '^z'
Wait-Idle $app
Wait-Until { [System.IO.File]::GetLastWriteTimeUtc($edit) -eq $oldTime } 5000 | Out-Null
Check '元に戻しても読めない印は戻らない' (Test-Same (Get-MarkBytes $edit) $sampleBytes) ((Get-MarkBytes $edit).Length)
Check '元に戻すと更新日時も元のまま' ([System.IO.File]::GetLastWriteTimeUtc($edit) -eq $oldTime) ([System.IO.File]::GetLastWriteTimeUtc($edit).ToString('o'))
Stop-Expzip $app

# エクスプローラーの印は読める印なので、編集しても 1 バイトも変わらない
$edit2 = New-TestZip (Join-Path $script:Work 'editmark2.zip') ([ordered]@{ 'one.txt' = 'NEW' })
[byte[]]$explorerMark = [byte[]](Ascii "[ZoneTransfer]`r`nZoneId=4`r`nReferrerUrl=C:\") + [byte[]](0x93, 0xFA) + [byte[]](Ascii "\a.zip") + [byte[]](0)
Set-Mark $edit2 $explorerMark
$app = Start-Expzip @($edit2)
Wait-Idle $app
$center = Get-WindowCenter $app
Invoke-Drop @($extra) $center.X $center.Y | Out-Null
Wait-Idle $app
Check 'エクスプローラーの印を編集で残す' (Test-Same (Get-MarkBytes $edit2) $explorerMark) ((Get-MarkBytes $edit2).Length)
Stop-Expzip $app

Complete-Suite
