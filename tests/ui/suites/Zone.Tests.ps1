# 出所の印の読み方 (#325)。取り出し、結合用のプログラム、自己解凍書庫で同じ結果になること
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Zone'
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


Section '読めない印と正しい印 (#325)'
# 取り出し、結合用のプログラム、自己解凍書庫のそれぞれで、同じ結果になること。印は決まったバイト列で書く
$sample = "[ZoneTransfer]`r`nZoneId=3`r`n"
$browser = "[ZoneTransfer]`r`nZoneId=3`r`nReferrerUrl=https://example.com/`r`nHostUrl=https://example.com/a.zip`r`nHostIpAddress=192.0.2.1`r`n"
$exact = "[ZoneTransfer]`r`nZoneId=1`r`nHostUrl=`r`n"
$exact = $exact.Replace("HostUrl=", "HostUrl=" + ('a' * (4096 - $exact.Length)))
$latin = [System.Text.Encoding]::GetEncoding(28591)
function Ascii([string]$Text) { return [byte[]]$latin.GetBytes($Text) }

# 名前、印のバイト列、読めるか (読めなければ見本になる)
$marks = @(
    [pscustomobject]@{ N = 'ブラウザーの印'; B = [byte[]]((Ascii $browser)); R = $true }
    [pscustomobject]@{ N = '区分 0'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=0`r`n")); R = $true }
    [pscustomobject]@{ N = '区分 1'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=1`r`n")); R = $true }
    [pscustomobject]@{ N = '区分 2'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=2`r`n")); R = $true }
    [pscustomobject]@{ N = '区分 4'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=4`r`n")); R = $true }
    [pscustomobject]@{ N = 'LF だけ'; B = [byte[]]((Ascii "[ZoneTransfer]`nZoneId=1`n")); R = $true }
    [pscustomobject]@{ N = '前の行の URL'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nHostUrl=https://e/?ZoneId=0`r`nZoneId=3`r`n")); R = $true }
    [pscustomobject]@{ N = '前に別の節'; B = [byte[]]((Ascii "[Other]`r`nZoneId=0`r`n[ZoneTransfer]`r`nZoneId=3`r`n")); R = $true }
    [pscustomobject]@{ N = 'AppZoneId の行'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nAppZoneId=0`r`nZoneId=3`r`n")); R = $true }
    [pscustomobject]@{ N = 'ASCII でない URL'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=") + [byte[]](0xE3, 0x81, 0x82, 0xFF) + [byte[]](Ascii "`r`n"))); R = $true }
    # エクスプローラーが ZIP から取り出したファイルに付ける印。パスは ANSI コードページのままで、最後に NUL が付く
    [pscustomobject]@{ N = 'エクスプローラーの印 区分 3'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nZoneId=3`r`nReferrerUrl=C:\") + [byte[]](0x93, 0xFA, 0x96, 0x7B) + [byte[]](Ascii "\a.zip") + [byte[]](0))); R = $true }
    [pscustomobject]@{ N = 'エクスプローラーの印 区分 4'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nZoneId=4`r`nReferrerUrl=C:\a.zip") + [byte[]](0))); R = $true }
    [pscustomobject]@{ N = '番号の後の値が 2 バイト文字の途中で終わる'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=") + [byte[]](0x81) + [byte[]](Ascii "`r`nA=1`r`n"))); R = $true }
    [pscustomobject]@{ N = '番号の前の日本語'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nHostUrl=") + [byte[]](0x82, 0xA0, 0x81) + [byte[]](Ascii "`r`nZoneId=4`r`n"))); R = $false }
    [pscustomobject]@{ N = '名前の日本語'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nZoneId=3`r`n") + [byte[]](0x82, 0xA0) + [byte[]](Ascii "=1`r`n"))); R = $false }
    [pscustomobject]@{ N = '名前が空の行'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=3`r`n=`r`n")); R = $false }
    [pscustomobject]@{ N = '名前=値 でない行'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nnoequal`r`nZoneId=4`r`n")); R = $false }
    [pscustomobject]@{ N = '閉じていない節の行'; B = [byte[]]((Ascii "[ZoneTransfer]`r`n[Open`r`nZoneId=4`r`n")); R = $false }
    [pscustomobject]@{ N = '節の行の後ろの空白'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=0`r`n[ZoneTransfer] `r`nZoneId=4`r`n")); R = $false }
    [pscustomobject]@{ N = '節の名前の内側の空白'; B = [byte[]]((Ascii "[ ZoneTransfer ]`r`nZoneId=0`r`n[ZoneTransfer]`r`nZoneId=4`r`n")); R = $false }
    [pscustomobject]@{ N = '節の行頭の空白';B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=0`r`n [ZoneTransfer]`r`nZoneId=4`r`n")); R = $false }
    [pscustomobject]@{ N = '上限ちょうど'; B = [byte[]]((Ascii $exact)); R = $true }
    [pscustomobject]@{ N = '上限より 1 バイト大きい'; B = [byte[]]((Ascii ($exact + 'c'))); R = $false }
    [pscustomobject]@{ N = '小文字'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nzoneid=1`r`n")); R = $false }
    [pscustomobject]@{ N = '節が小文字'; B = [byte[]]((Ascii "[zonetransfer]`r`nZoneId=1`r`n")); R = $false }
    [pscustomobject]@{ N = '= の前後の空白'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId = 1`r`n")); R = $false }
    [pscustomobject]@{ N = '行頭の空白'; B = [byte[]]((Ascii "[ZoneTransfer]`r`n ZoneId=1`r`n")); R = $false }
    [pscustomobject]@{ N = 'BOM'; B = [byte[]](([byte[]](0xEF, 0xBB, 0xBF) + [byte[]](Ascii "[ZoneTransfer]`r`nZoneId=1`r`n"))); R = $false }
    [pscustomobject]@{ N = 'UTF-16'; B = [byte[]](([byte[]](0xFF, 0xFE) + [System.Text.Encoding]::Unicode.GetBytes("[ZoneTransfer]`r`nZoneId=1`r`n"))); R = $false }
    [pscustomobject]@{ N = 'NUL'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nZoneId=1") + [byte[]](0) + [byte[]](Ascii "`r`n"))); R = $false }
    [pscustomobject]@{ N = 'CR だけ'; B = [byte[]]((Ascii "[ZoneTransfer]`rZoneId=1`r")); R = $false }
    [pscustomobject]@{ N = '全角の数字'; B = [byte[]](([byte[]](Ascii "[ZoneTransfer]`r`nZoneId=") + [byte[]](0xEF, 0xBC, 0x91) + [byte[]](Ascii "`r`n"))); R = $false }
    [pscustomobject]@{ N = '番号 5'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=5`r`n")); R = $false }
    [pscustomobject]@{ N = '番号 1000'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=1000`r`n")); R = $false }
    [pscustomobject]@{ N = '桁あふれ'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=99999999999`r`n")); R = $false }
    [pscustomobject]@{ N = '数字の後に文字'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=1abc`r`n")); R = $false }
    [pscustomobject]@{ N = '2 行'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=1`r`nZoneId=3`r`n")); R = $false }
    [pscustomobject]@{ N = '節が 2 つ'; B = [byte[]]((Ascii "[ZoneTransfer]`r`nZoneId=1`r`n[ZoneTransfer]`r`nZoneId=3`r`n")); R = $false }
    [pscustomobject]@{ N = '節の外だけ'; B = [byte[]]((Ascii "ZoneId=1`r`n")); R = $false }
    [pscustomobject]@{ N = '別の節だけ'; B = [byte[]]((Ascii "[Other]`r`nZoneId=1`r`n")); R = $false }
    [pscustomobject]@{ N = '空'; B = [byte[]]([byte[]]@()); R = $false }
    [pscustomobject]@{ N = '空白だけ'; B = [byte[]]((Ascii "  `r`n")); R = $false }
)

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

$stub = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '..\..\..\src\Expzip.Core\Resources\sfx-x86.exe'))
$plainZip = New-TestZip (Join-Path $script:Work 'plain_for_mark.zip') ([ordered]@{ 'one.txt' = 'NEW' })
$plainBytes = [System.IO.File]::ReadAllBytes($plainZip)

# 分割して結合用のプログラムを作っておく
$source = Join-Path $script:Work 'joinmark.bin'
[System.IO.File]::WriteAllBytes($source, (New-Object byte[] (2MB + 3)))
$parts = New-Folder 'joinmark_parts'
$app = Start-Expzip
$done = Start-Split $app
if ($done) { Close-MessageBox $done 'OK' }
Stop-Expzip $app
$joinerExe = Join-Path $parts 'joinmark.bin.exe'
$joined = Join-Path $parts 'joinmark.bin'

$app = Start-Expzip @($plainZip)
$index = 0
foreach ($item in $marks) {
    $index++
    $name = $item.N; $bytes = [byte[]]$item.B
    [byte[]]$want = if ($item.R) { $bytes } else { $sampleBytes }

    # 取り出し
    Set-Mark $plainZip $bytes
    $out = New-Folder "mark_out_$index"
    Focus-App $app
    $text = Invoke-Overwrite $app $out
    Check "取り出し: $name" (Test-Same (Get-MarkBytes (Join-Path $out 'one.txt')) $want) ((Get-MarkBytes (Join-Path $out 'one.txt')).Length)

    # 結合用のプログラム。分割ファイルの印を読む
    Set-Mark (Join-Path $parts 'joinmark.bin.001') $bytes
    $process = Start-Process $joinerExe -ArgumentList '-q' -WorkingDirectory $parts -PassThru
    $process.WaitForExit(30000) | Out-Null
    Check "結合: $name" ($process.ExitCode -eq 0 -and (Test-Same (Get-MarkBytes $joined) $want)) ((Get-MarkBytes $joined).Length)
    [System.IO.File]::Delete($joined)

    # 自己解凍書庫。自分の印を読む。印を付けた exe はシェルから起動すると警告で止まるので、直接起動する
    $sfxName = "marksfx$index-" + [guid]::NewGuid().ToString('N').Substring(0, 6)
    $sfxExe = Join-Path $script:Work "$sfxName.exe"
    $combined = New-Object byte[] ($stub.Length + $plainBytes.Length)
    [Array]::Copy($stub, $combined, $stub.Length)
    [Array]::Copy($plainBytes, 0, $combined, $stub.Length, $plainBytes.Length)
    [System.IO.File]::WriteAllBytes($sfxExe, $combined)
    Set-Mark $sfxExe $bytes
    $info = New-Object System.Diagnostics.ProcessStartInfo $sfxExe, '-q'
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $script:Work
    $process = [System.Diagnostics.Process]::Start($info)
    $process.WaitForExit(30000) | Out-Null
    $got = Join-Path $script:Work "$sfxName\one.txt"
    Check "自己解凍: $name" ($process.ExitCode -eq 0 -and (Test-Same (Get-MarkBytes $got) $want)) ((Get-MarkBytes $got).Length)
}
Stop-Expzip $app

Complete-Suite
