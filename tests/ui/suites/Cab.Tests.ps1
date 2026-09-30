# CAB を開く (#182)。見本は Windows の makecab で作る
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Cab'
Set-Settings

$source = Join-Path $script:Work 'src'
New-Item -ItemType Directory -Force -Path (Join-Path $source 'sub') | Out-Null
Set-Content -LiteralPath (Join-Path $source 'メモ.txt') -Value ('日本語のメモ' * 100) -Encoding UTF8
Set-Content -LiteralPath (Join-Path $source 'sub\text.txt') -Value ('hello cab' * 20000) -Encoding ASCII
$random = New-Object byte[] 300000
(New-Object System.Random 1).NextBytes($random)
[System.IO.File]::WriteAllBytes((Join-Path $source 'sub\data.bin'), $random)

# makecab の指示書。名前は Windows の ANSI の文字コードで書く (makecab がそう読む)
function New-Cab([string]$Name, [string]$Compression, [int]$MaxDiskSize = 0) {
    $lines = @(
        ".Set CabinetNameTemplate=$Name",
        '.Set DiskDirectoryTemplate=out',
        ".Set CompressionType=$Compression",
        ".Set MaxDiskSize=$MaxDiskSize",
        '.Set Cabinet=on',
        '.Set Compress=on',
        '"src\メモ.txt" "メモ.txt"',
        '.Set DestinationDir=sub',
        '"src\sub\data.bin" "data.bin"',
        '"src\sub\text.txt" "text.txt"'
    )
    $ddf = Join-Path $script:Work "$Compression.ddf"
    [System.IO.File]::WriteAllLines($ddf, $lines, [System.Text.Encoding]::Default)
    Push-Location $script:Work
    try { & makecab.exe /F $ddf | Out-Null } finally { Pop-Location }
}

function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

New-Cab 'lzx.cab' 'LZX'
New-Cab 'split*.cab' 'MSZIP' 122880
$cab = Join-Path $script:Work 'out\lzx.cab'
$split = Join-Path $script:Work 'out\split1.cab'
Check '見本ができる' ((Test-Path $cab) -and (Test-Path $split) -and (Test-Path (Join-Path $script:Work 'out\split3.cab')))

$app = Start-Expzip @($cab)

Section '開く'
$names = (@(Get-RowNames $app) | Sort-Object) -join ', '
Check '中身が並ぶ' ($names -eq ((@('sub', 'メモ.txt') | Sort-Object) -join ', ')) $names
Check '読み取りのみと添える' ((Texts $app.Window) -match 'CAB は読み取りのみに対応')
Check '追加できない' (-not (ById $app.Window 'AddButton').Current.IsEnabled)

Section '書庫全体を展開する'
$destination = Join-Path $script:Work 'all'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '書庫全体の展開先を選択' $destination)
$box = Find-MessageBox $app 30000
if ($box) {
    Check '件数' ($box.Text -match '展開したファイル: 3 個') $box.Text
    Close-MessageBox $box 'OK'
}
$same = @('メモ.txt', 'sub\data.bin', 'sub\text.txt') | Where-Object {
    (Test-Path -LiteralPath (Join-Path $destination $_)) -and ((Hash (Join-Path $destination $_)) -eq (Hash (Join-Path $source $_)))
}
Check '元のファイルと同じ中身で出る' ($same.Count -eq 3) ($same -join ', ')

Section '選択した項目を展開する'
Select-Row $app 'sub' | Out-Null
Send-Keys $app '{ENTER}'
Select-Row $app 'text.txt' | Out-Null
$picked = Join-Path $script:Work 'picked'
New-Item -ItemType Directory -Force -Path $picked | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '選択した項目の展開先を選択' $picked)
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
Check '選んだものだけが出る' (@(Get-ChildItem $picked -Recurse -File).Count -eq 1 -and (Test-Path (Join-Path $picked 'text.txt'))) ((Get-ChildItem $picked -Recurse | ForEach-Object { $_.Name }) -join ', ')
Send-Keys $app '{BACKSPACE}'

Section '検査'
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - lzx.cab' 30000
Check '窓が開く' ($null -ne $window)
if ($window) {
    Check '問題は無い' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '分割された CAB'
$app = Start-Expzip @($split)
$names = (@(Get-RowNames $app) | Sort-Object) -join ', '
Check '続きの CAB に始まるファイルも並ぶ' ($names -eq ((@('sub', 'メモ.txt') | Sort-Object) -join ', ')) $names
Check '件数' ((Texts $app.Window) -match '3 個のファイル')
$destination = Join-Path $script:Work 'split'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $destination | Out-Null
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
$same = @('メモ.txt', 'sub\data.bin', 'sub\text.txt') | Where-Object {
    (Test-Path -LiteralPath (Join-Path $destination $_)) -and ((Hash (Join-Path $destination $_)) -eq (Hash (Join-Path $source $_)))
}
Check 'CAB をまたぐファイルも同じ中身で出る' ($same.Count -eq 3) ($same -join ', ')
Stop-Expzip $app

Section '続きの CAB が欠けているとき'
Remove-Item (Join-Path $script:Work 'out\split3.cab')
$app = Start-Expzip @($split)
$box = Find-MessageBox $app 15000
Check '欠けた CAB の名前を言う' ($box -and $box.Text -match 'split3\.cab が見つかりません') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'OK' }
Stop-Expzip $app

Complete-Suite
