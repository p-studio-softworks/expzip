# CAB が入った exe を開く (#182)。IExpress で作ったものと、後ろに CAB を付け足したもの
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'CabExe'
Set-Settings

function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

$source = Join-Path $script:Work 'src'
New-Item -ItemType Directory -Force -Path $source | Out-Null
Set-Content -LiteralPath (Join-Path $source 'readme.txt') -Value ('hello cab' * 2000) -Encoding ASCII
$random = New-Object byte[] 100000
(New-Object System.Random 1).NextBytes($random)
[System.IO.File]::WriteAllBytes((Join-Path $source 'data.bin'), $random)

# IExpress (Windows に付いている) で、取り出すだけの exe を作る
$iexpress = Join-Path $script:Work 'packed.exe'
$sed = Join-Path $script:Work 'packed.sed'
$lines = @(
    '[Version]', 'Class=IEXPRESS', 'SEDVersion=3',
    '[Options]', 'PackagePurpose=ExtractOnly', 'ShowInstallProgramWindow=0', 'HideExtractAnimation=1',
    'UseLongFileName=1', 'InsideCompressed=0', 'CAB_FixedSize=0', 'CAB_ResvCodeSigning=0', 'RebootMode=N',
    'InstallPrompt=%InstallPrompt%', 'DisplayLicense=%DisplayLicense%', 'FinishMessage=%FinishMessage%',
    'TargetName=%TargetName%', 'FriendlyName=%FriendlyName%', 'AppLaunched=%AppLaunched%',
    'PostInstallCmd=%PostInstallCmd%', 'AdminQuietInstCmd=%AdminQuietInstCmd%', 'UserQuietInstCmd=%UserQuietInstCmd%',
    'SourceFiles=SourceFiles',
    '[Strings]', 'InstallPrompt=', 'DisplayLicense=', 'FinishMessage=', "TargetName=$iexpress", 'FriendlyName=test',
    'AppLaunched=', 'PostInstallCmd=<None>', 'AdminQuietInstCmd=', 'UserQuietInstCmd=',
    'FILE0="readme.txt"', 'FILE1="data.bin"',
    '[SourceFiles]', "SourceFiles0=$source\",
    '[SourceFiles0]', '%FILE0%=', '%FILE1%='
)
[System.IO.File]::WriteAllLines($sed, $lines, [System.Text.Encoding]::Default)
# IExpress は、引用符で囲んだパスを受け付けない
Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\iexpress.exe') -ArgumentList '/N', '/Q', $sed -Wait

# makecab で CAB を作り、Windows に付いている小さな exe の写しの後ろに付け足す (動かすためではなく、読むためのもの)
function New-Cab([string]$Name) {
    $lines = @(
        ".Set CabinetNameTemplate=$Name", ".Set DiskDirectoryTemplate=$($script:Work)", '.Set Cabinet=on', '.Set Compress=on',
        "`"$(Join-Path $source 'readme.txt')`" `"readme.txt`"",
        '.Set DestinationDir=docs',
        "`"$(Join-Path $source 'data.bin')`" `"data.bin`""
    )
    $ddf = Join-Path $script:Work "$Name.ddf"
    [System.IO.File]::WriteAllLines($ddf, $lines, [System.Text.Encoding]::Default)
    Push-Location $script:Work
    try { & makecab.exe /F $ddf | Out-Null } finally { Pop-Location }
}

function New-Appended([string]$Path, [int]$Gap) {
    $image = [System.IO.File]::ReadAllBytes((Join-Path $env:SystemRoot 'System32\find.exe'))
    $cab = [System.IO.File]::ReadAllBytes((Join-Path $script:Work 'test.cab'))
    $stream = [System.IO.File]::Create($Path)
    $stream.Write($image, 0, $image.Length)
    $stream.Write((New-Object byte[] $Gap), 0, $Gap)
    $stream.Write($cab, 0, $cab.Length)
    $stream.Dispose()
}

New-Cab 'test.cab'
$appended = Join-Path $script:Work 'setup.exe'
New-Appended $appended 300
# 付け足したデータの奥 (64 KB より先) にある CAB は、たまたま入っているものとして扱わない
$far = Join-Path $script:Work 'far.exe'
New-Appended $far 70000
Check '見本ができる' ((Test-Path $iexpress) -and (Test-Path $appended) -and (Test-Path $far))

Section 'IExpress で作った exe'
$app = Start-Expzip @($iexpress)
$names = (@(Get-RowNames $app) | Sort-Object) -join ', '
Check '中の CAB のファイルが並ぶ' ($names -eq 'data.bin, readme.txt') $names
Check '読み取りのみと添える' ((Texts $app.Window) -match 'CAB が入った exe / 読み取りのみ')
$destination = Join-Path $script:Work 'packed-out'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $destination | Out-Null
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
$same = @('readme.txt', 'data.bin') | Where-Object {
    (Test-Path -LiteralPath (Join-Path $destination $_)) -and ((Hash (Join-Path $destination $_)) -eq (Hash (Join-Path $source $_)))
}
Check '元のファイルと同じ中身で出る' ($same.Count -eq 2) ($same -join ', ')
Stop-Expzip $app

Section '後ろに CAB を付け足した exe'
$app = Start-Expzip @($appended)
$names = (@(Get-RowNames $app) | Sort-Object) -join ', '
Check '中の CAB のファイルが並ぶ' ($names -eq 'docs, readme.txt') $names
$destination = Join-Path $script:Work 'appended-out'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $destination | Out-Null
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
$same = @(@('readme.txt', 'readme.txt'), @('docs\data.bin', 'data.bin')) | Where-Object {
    (Test-Path -LiteralPath (Join-Path $destination $_[0])) -and ((Hash (Join-Path $destination $_[0])) -eq (Hash (Join-Path $source $_[1])))
}
Check 'フォルダーも含めて同じ中身で出る' (@($same).Count -eq 2) (@($same | ForEach-Object { $_[0] }) -join ', ')

Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - setup.exe' 30000
Check '検査できる' ($null -ne $window)
if ($window) {
    Check '問題は無い' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '奥にある CAB'
$app = Start-Expzip @($far)
Check 'exe / dll として開く' ((Texts $app.Window) -match 'exe / dll / 読み取りのみ')
Stop-Expzip $app

Section '書庫の中のものを新しいタブで開く (#206)'
$zip = New-TestZip (Join-Path $script:Work 'packed.zip') ([ordered]@{
    'packed.exe'   = [System.IO.File]::ReadAllBytes($iexpress)
    'appended.exe' = [System.IO.File]::ReadAllBytes($appended)
})
$app = Start-Expzip @($zip)
$expected = [ordered]@{ 'packed.exe' = 'data.bin, readme.txt'; 'appended.exe' = 'docs, readme.txt' }
foreach ($name in $expected.Keys) {
    Check "右クリックから開ける ($name)" (Open-InNewTab $app $name)
    $names = (@(Get-RowNames $app) | Sort-Object) -join ', '
    Check "外から開いたときと同じに並ぶ ($name)" ($names -eq $expected[$name]) $names
    Select-Element (Get-Tabs $app)[0]
    Wait-Idle $app
}
Stop-Expzip $app

Complete-Suite
