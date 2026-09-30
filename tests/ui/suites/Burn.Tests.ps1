# WiX Burn でまとめた exe を開く (#182)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Burn'
Set-Settings

# 見本は WiX が無いと作れない。Windows に付いている小さな exe の写しに、.wixburn 区画と
# 入れ物 (CAB) を書き足して組み立てる。動かすためのものではなく、読むためのもの
$source = Join-Path $script:Work 'src'
New-Item -ItemType Directory -Force -Path $source | Out-Null
$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<BurnManifest xmlns="http://schemas.microsoft.com/wix/2008/Burn">
  <UX>
    <Payload Id="theme" FilePath="theme.xml" SourcePath="u0" Packaging="embedded" />
  </UX>
  <Container Id="WixAttachedContainer" AttachedIndex="1" Attached="yes" />
  <Payload Id="core" FilePath="packages\core.txt" FileSize="4" Packaging="embedded" SourcePath="a0" Container="WixAttachedContainer" />
  <Payload Id="core_again" FilePath="packages\core.txt" FileSize="4" Packaging="embedded" SourcePath="a0" Container="WixAttachedContainer" />
  <Payload Id="download" FilePath="download.msi" Packaging="external" SourcePath="download.msi" />
</BurnManifest>
'@
[System.IO.File]::WriteAllText((Join-Path $source '0'), $manifest)
[System.IO.File]::WriteAllText((Join-Path $source 'u0'), '<Theme />')
[System.IO.File]::WriteAllText((Join-Path $source 'a0'), 'core')

function New-Cab([string]$Name, [string[]]$Keys) {
    $lines = @(".Set CabinetNameTemplate=$Name", ".Set DiskDirectoryTemplate=$($script:Work)", '.Set Cabinet=on', '.Set Compress=on') +
        @($Keys | ForEach-Object { "`"$(Join-Path $source $_)`" `"$_`"" })
    $ddf = Join-Path $script:Work "$Name.ddf"
    [System.IO.File]::WriteAllLines($ddf, $lines, [System.Text.Encoding]::Default)
    Push-Location $script:Work
    try { & makecab.exe /F $ddf | Out-Null } finally { Pop-Location }
    return [System.IO.File]::ReadAllBytes((Join-Path $script:Work $Name))
}
$ux = New-Cab 'ux.cab' @('0', 'u0')
$attached = New-Cab 'attached.cab' @('a0')

function U32([byte[]]$Bytes, [int]$At) { [BitConverter]::ToUInt32($Bytes, $At) }
function Put32([byte[]]$Bytes, [int]$At, [uint32]$Value) { [BitConverter]::GetBytes($Value).CopyTo($Bytes, $At) }

$image = [System.IO.File]::ReadAllBytes((Join-Path $env:SystemRoot 'System32\find.exe'))
$pe = [BitConverter]::ToInt32($image, 0x3C)
$count = [BitConverter]::ToUInt16($image, $pe + 6)
$optionalSize = [BitConverter]::ToUInt16($image, $pe + 20)
$fileAlignment = U32 $image ($pe + 24 + 36)
$table = $pe + 24 + $optionalSize
$newHeader = $table + $count * 40
$firstRaw = U32 $image ($table + 20)
Check '区画の表に空きがある' ($newHeader + 40 -le $firstRaw)

# 区画の中身は元の exe の後ろ (境界に揃える)。入れ物はその後ろに続ける
$sectionAt = [int]([Math]::Ceiling($image.Length / $fileAlignment) * $fileAlignment)
$sectionSize = [int]$fileAlignment
$stub = $sectionAt + $sectionSize
$section = New-Object byte[] $sectionSize
Put32 $section 0 0x00F14300
Put32 $section 4 2
Put32 $section 24 $stub
Put32 $section 40 1
Put32 $section 44 2
Put32 $section 48 $ux.Length
Put32 $section 52 $attached.Length

$header = New-Object byte[] 40
[System.Text.Encoding]::ASCII.GetBytes('.wixburn').CopyTo($header, 0)
Put32 $header 8 $sectionSize
Put32 $header 12 0x7FFF0000
Put32 $header 16 $sectionSize
Put32 $header 20 $sectionAt
$header.CopyTo($image, $newHeader)
[BitConverter]::GetBytes([uint16]($count + 1)).CopyTo($image, $pe + 6)

$bundle = Join-Path $script:Work 'setup.exe'
$stream = [System.IO.File]::Create($bundle)
$stream.Write($image, 0, $image.Length)
$stream.Write((New-Object byte[] ($sectionAt - $image.Length)), 0, $sectionAt - $image.Length)
$stream.Write($section, 0, $section.Length)
$stream.Write($ux, 0, $ux.Length)
$stream.Write($attached, 0, $attached.Length)
$stream.Dispose()

# 2 つ目の入れ物を抜いたもの (インストール後に Windows が控えた形)
$stripped = Join-Path $script:Work 'cached.exe'
$bytes = [System.IO.File]::ReadAllBytes($bundle)
[System.IO.File]::WriteAllBytes($stripped, $bytes[0..($bytes.Length - $attached.Length - 1)])

function Read-Text([string]$Path) {
    if (Test-Path -LiteralPath $Path) { [System.IO.File]::ReadAllText($Path) } else { '' }
}

$app = Start-Expzip @($bundle)

Section '開く'
$names = (@(Get-RowNames $app) | Sort-Object) -join ', '
Check '画面の部品と本体が並ぶ' ($names -eq ((@('UX', 'packages') | Sort-Object) -join ', ')) $names
Check 'ダウンロードするものは並ばない' ((Get-RowNames $app) -notcontains 'download.msi')
Check '同じ中身は 1 つにまとめる' ((Texts $app.Window) -match '3 個のファイル')
Check '読み取りのみと添える' ((Texts $app.Window) -match 'WiX Burn は読み取りのみに対応')

Section '書庫全体を展開する'
$all = Join-Path $script:Work 'all'
New-Item -ItemType Directory -Force -Path $all | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '書庫全体の展開先を選択' $all)
$box = Find-MessageBox $app 30000
if ($box) {
    Check '件数' ($box.Text -match '展開したファイル: 3 個') $box.Text
    Close-MessageBox $box 'OK'
}
Check '本体が本当の名前で出る' ((Read-Text (Join-Path $all 'packages\core.txt')) -eq 'core')
Check '画面の部品が出る' ((Read-Text (Join-Path $all 'UX\theme.xml')) -eq '<Theme />')
Check '目録が出る' ((Read-Text (Join-Path $all 'UX\BurnManifest.xml')) -match '<BurnManifest')

Section '検査'
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - setup.exe' 30000
Check '窓が開く' ($null -ne $window)
if ($window) {
    Check '問題は無い' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '本体が抜かれているとき'
$app = Start-Expzip @($stripped)
Check '一覧は出る' ((Texts $app.Window) -match '3 個のファイル')
$out = Join-Path $script:Work 'stripped'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $out | Out-Null
$box = Find-MessageBox $app 30000
Check '壊れているとは言わず、中身が無いと言う' ($box -and $box.Text -match '中身が入っていません' -and $box.Text -notmatch '壊れて') $(if ($box) { $box.Text })
Check '画面の部品は出る' ($box -and $box.Text -match '展開したファイル: 2 個') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'OK' }
Stop-Expzip $app

Complete-Suite
