# MSI を開く (#182)。見本は Windows の MSI の仕組み (COM) と makecab でその場で作る
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Msi'
Set-Settings

$source = Join-Path $script:Work 'src'
New-Item -ItemType Directory -Force -Path $source | Out-Null
$contents = [ordered]@{ f1 = 'readme'; f2 = 'guide'; f3 = 'loose'; f4 = 'external' }
foreach ($key in $contents.Keys) {
    [System.IO.File]::WriteAllText((Join-Path $source $key), $contents[$key])
}

$out = Join-Path $script:Work 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

# CAB の中の名前は、File 表の鍵 (f1 など)
function New-Cab([string]$Name, [string[]]$Keys) {
    $lines = @(".Set CabinetNameTemplate=$Name", ".Set DiskDirectoryTemplate=$out", '.Set Cabinet=on', '.Set Compress=on') +
        @($Keys | ForEach-Object { "`"$(Join-Path $source $_)`" `"$_`"" })
    $ddf = Join-Path $script:Work "$Name.ddf"
    [System.IO.File]::WriteAllLines($ddf, $lines, [System.Text.Encoding]::Default)
    Push-Location $script:Work
    try { & makecab.exe /F $ddf | Out-Null } finally { Pop-Location }
}
New-Cab 'test.cab' @('f1', 'f2')
New-Cab 'ext.cab' @('f4')

# MSI を作る。表は読むのに要る列だけにする (入れられる MSI である必要は無い)
$msi = Join-Path $out '見本.msi'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($msi, 3)
function Sql([string]$Text) {
    $view = $database.OpenView($Text)
    $view.Execute()
    $view.Close()
}
Sql 'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL PRIMARY KEY `Directory`)'
Sql 'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL PRIMARY KEY `Component`)'
Sql 'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `FileSize` LONG NOT NULL, `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)'
Sql 'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `Cabinet` CHAR(255) PRIMARY KEY `DiskId`)'
Sql "INSERT INTO ``Directory`` (``Directory``, ``Directory_Parent``, ``DefaultDir``) VALUES ('TARGETDIR', '', 'SourceDir')"
Sql "INSERT INTO ``Directory`` (``Directory``, ``Directory_Parent``, ``DefaultDir``) VALUES ('ProgramFilesFolder', 'TARGETDIR', 'PFiles')"
Sql "INSERT INTO ``Directory`` (``Directory``, ``Directory_Parent``, ``DefaultDir``) VALUES ('APPDIR', 'ProgramFilesFolder', 'MYAPP|My App')"
# 置き先と元の置き場所で名前が違うフォルダー。管理用インストールと同じく、元の置き場所の名前を使う
Sql "INSERT INTO ``Directory`` (``Directory``, ``Directory_Parent``, ``DefaultDir``) VALUES ('DOCS', 'APPDIR', 'TDOCS|Target Docs:SDOCS|Source Docs')"
Sql "INSERT INTO ``Component`` (``Component``, ``Directory_``) VALUES ('App', 'APPDIR')"
Sql "INSERT INTO ``Component`` (``Component``, ``Directory_``) VALUES ('Docs', 'DOCS')"
Sql "INSERT INTO ``File`` (``File``, ``Component_``, ``FileName``, ``FileSize``, ``Attributes``, ``Sequence``) VALUES ('f1', 'App', 'README.TXT|readme.txt', 6, 0, 1)"
Sql "INSERT INTO ``File`` (``File``, ``Component_``, ``FileName``, ``FileSize``, ``Attributes``, ``Sequence``) VALUES ('f2', 'Docs', 'guide.txt', 5, 0, 2)"
# 0x2000 は「圧縮しない」。MSI の隣の、元の置き場所に置く
Sql "INSERT INTO ``File`` (``File``, ``Component_``, ``FileName``, ``FileSize``, ``Attributes``, ``Sequence``) VALUES ('f3', 'App', 'loose.txt', 5, 8192, 3)"
Sql "INSERT INTO ``File`` (``File``, ``Component_``, ``FileName``, ``FileSize``, ``Attributes``, ``Sequence``) VALUES ('f4', 'App', 'ext.txt', 8, 0, 4)"
Sql "INSERT INTO ``Media`` (``DiskId``, ``LastSequence``, ``Cabinet``) VALUES (1, 3, '#test.cab')"
Sql "INSERT INTO ``Media`` (``DiskId``, ``LastSequence``, ``Cabinet``) VALUES (2, 4, 'ext.cab')"

# 埋め込む CAB
$record = $installer.CreateRecord(2)
$record.StringData(1) = 'test.cab'
$record.SetStream(2, (Join-Path $out 'test.cab'))
$view = $database.OpenView('INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)')
$view.Execute($record)
$view.Close()

# 要約情報の語数 2 は「既定で圧縮、長い名前」
$summary = $database.SummaryInformation(4)
$summary.Property(15) = 2
$summary.Persist()
$database.Commit()

# COM の持ち物を残すと MSI を開いたままになり、Expzip から開けない。すべて手放す
foreach ($held in @($summary, $view, $record, $database, $installer)) {
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($held) | Out-Null
}
$summary = $view = $record = $database = $installer = $null
[GC]::Collect()
[GC]::WaitForPendingFinalizers()
Remove-Item (Join-Path $out 'test.cab')

# 圧縮しないファイルは、MSI の隣の元の置き場所に置く
New-Item -ItemType Directory -Force -Path (Join-Path $out 'PFiles\My App') | Out-Null
Copy-Item (Join-Path $source 'f3') (Join-Path $out 'PFiles\My App\loose.txt')

Check '見本ができる' ((Test-Path $msi) -and (Test-Path (Join-Path $out 'ext.cab')))

function Read-Text([string]$Path) {
    if (Test-Path -LiteralPath $Path) { [System.IO.File]::ReadAllText($Path) } else { '' }
}

$app = Start-Expzip @($msi)

Section '開く'
Check '元の置き場所の名前で並ぶ' (((Get-RowNames $app) -join ', ') -eq 'PFiles') ((Get-RowNames $app) -join ', ')
Check '読み取りのみと添える' ((Texts $app.Window) -match 'MSI / 読み取りのみ')
Check '追加できない' (-not (ById $app.Window 'AddButton').Current.IsEnabled)
Check '件数' ((Texts $app.Window) -match '4 個のファイル')
Select-Row $app 'PFiles' | Out-Null
Send-Keys $app '{ENTER}'
Select-Row $app 'My App' | Out-Null
Send-Keys $app '{ENTER}'
$names = (@(Get-RowNames $app) | Sort-Object) -join ', '
Check '長い名前で並ぶ' ($names -eq ((@('Source Docs', 'ext.txt', 'loose.txt', 'readme.txt') | Sort-Object) -join ', ')) $names
Send-Keys $app '{BACKSPACE}'
Send-Keys $app '{BACKSPACE}'

Section '書庫全体を展開する'
$all = Join-Path $script:Work 'all'
New-Item -ItemType Directory -Force -Path $all | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '書庫全体の展開先を選択' $all)
$box = Find-MessageBox $app 30000
if ($box) {
    Check '件数' ($box.Text -match '展開したファイル: 4 個') $box.Text
    Close-MessageBox $box 'OK'
}
$app_ = Join-Path $all 'PFiles\My App'
Check '埋め込みの CAB から出る' ((Read-Text (Join-Path $app_ 'readme.txt')) -eq 'readme' -and (Read-Text (Join-Path $app_ 'Source Docs\guide.txt')) -eq 'guide')
Check '外付けの CAB から出る' ((Read-Text (Join-Path $app_ 'ext.txt')) -eq 'external')
Check 'MSI の隣から出る' ((Read-Text (Join-Path $app_ 'loose.txt')) -eq 'loose')

Section '検査'
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - 見本.msi' 30000
Check '窓が開く' ($null -ne $window)
if ($window) {
    Check '問題は無い' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '外付けの CAB が無いとき'
Remove-Item (Join-Path $out 'ext.cab')
$app = Start-Expzip @($msi)
$missing = Join-Path $script:Work 'missing'
New-Item -ItemType Directory -Force -Path $missing | Out-Null
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $missing | Out-Null
$box = Find-MessageBox $app 30000
Check '欠けた CAB の名前を言う' ($box -and $box.Text -match 'ext\.cab が見つかりません') $(if ($box) { $box.Text })
Check 'ほかは出る' ($box -and $box.Text -match '展開したファイル: 3 個') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'OK' }
Stop-Expzip $app

Complete-Suite
