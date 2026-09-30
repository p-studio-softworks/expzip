# MSI が入った exe を開く (#182)。exe の後ろに付け足された MSI を並べ、その中まで辿れる
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'MsiExe'
Set-Settings

$source = Join-Path $script:Work 'src'
New-Item -ItemType Directory -Force -Path $source | Out-Null
[System.IO.File]::WriteAllText((Join-Path $source 'f1'), 'readme')

# CAB の中の名前は、File 表の鍵
$lines = @('.Set CabinetNameTemplate=test.cab', ".Set DiskDirectoryTemplate=$($script:Work)", '.Set Cabinet=on', '.Set Compress=on',
    "`"$(Join-Path $source 'f1')`" `"f1`"")
$ddf = Join-Path $script:Work 'test.ddf'
[System.IO.File]::WriteAllLines($ddf, $lines, [System.Text.Encoding]::Default)
Push-Location $script:Work
try { & makecab.exe /F $ddf | Out-Null } finally { Pop-Location }

# MSI を作る。表は読むのに要る列だけにする
$msi = Join-Path $script:Work 'inner.msi'
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
Sql "INSERT INTO ``Directory`` (``Directory``, ``Directory_Parent``, ``DefaultDir``) VALUES ('APPDIR', 'TARGETDIR', 'App')"
Sql "INSERT INTO ``Component`` (``Component``, ``Directory_``) VALUES ('App', 'APPDIR')"
Sql "INSERT INTO ``File`` (``File``, ``Component_``, ``FileName``, ``FileSize``, ``Attributes``, ``Sequence``) VALUES ('f1', 'App', 'readme.txt', 6, 0, 1)"
Sql "INSERT INTO ``Media`` (``DiskId``, ``LastSequence``, ``Cabinet``) VALUES (1, 1, '#test.cab')"
$record = $installer.CreateRecord(2)
$record.StringData(1) = 'test.cab'
$record.SetStream(2, (Join-Path $script:Work 'test.cab'))
$view = $database.OpenView('INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)')
$view.Execute($record)
$view.Close()
$summary = $database.SummaryInformation(4)
$summary.Property(15) = 2
$summary.Persist()
$database.Commit()
foreach ($held in @($summary, $view, $record, $database, $installer)) {
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($held) | Out-Null
}
$summary = $view = $record = $database = $installer = $null
[GC]::Collect()
[GC]::WaitForPendingFinalizers()

# Windows に付いている小さな exe の写しの後ろに、MSI を付け足す (動かすためではなく、読むためのもの)
$setup = Join-Path $script:Work 'setup.exe'
$image = [System.IO.File]::ReadAllBytes((Join-Path $env:SystemRoot 'System32\find.exe'))
$inner = [System.IO.File]::ReadAllBytes($msi)
$stream = [System.IO.File]::Create($setup)
$stream.Write($image, 0, $image.Length)
$stream.Write((New-Object byte[] 123), 0, 123)
$stream.Write($inner, 0, $inner.Length)
$stream.Write((New-Object byte[] 77), 0, 77)
$stream.Dispose()
Check '見本ができる' ((Test-Path $setup) -and $inner.Length -gt 0)

$app = Start-Expzip @($setup)

Section '開く'
Check 'exe の名前で MSI が並ぶ' (((Get-RowNames $app) -join ', ') -eq 'setup.msi') ((Get-RowNames $app) -join ', ')
Check '読み取りのみと添える' ((Texts $app.Window) -match 'MSI が入った exe は読み取りのみに対応')

Section '取り出す'
$out = Join-Path $script:Work 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $out | Out-Null
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
$extracted = Join-Path $out 'setup.msi'
Check '付け足した MSI とそっくり同じ' ((Test-Path $extracted) -and (Get-FileHash $extracted).Hash -eq (Get-FileHash $msi).Hash)

Section 'MSI の中まで辿る'
Select-Row $app 'setup.msi' | Out-Null
Send-Keys $app '{ENTER}'
# MSI は実行されうる種類だが、Expzip が自分で開くので「実行するか」は尋ねない
$box = Find-MessageBox $app 3000
Check '実行するかを尋ねない' ($null -eq $box) $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'キャンセル' }
Wait-Idle $app
Check '中の MSI を開ける' (((Get-RowNames $app) -join ', ') -eq 'App') ((Get-RowNames $app) -join ', ')
Stop-Expzip $app

Section '書庫の中の exe は今までどおり尋ねる'
$zip = New-TestZip (Join-Path $script:Work 'tools.zip') ([ordered]@{ 'tool.exe' = [byte[]]$image })
$app = Start-Expzip @($zip)
Select-Row $app 'tool.exe' | Out-Null
Send-Keys $app '{ENTER}'
$box = Find-MessageBox $app 10000
Check '実行するかを尋ねる' ($box -and $box.Text -match 'プログラムとして実行されます') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'キャンセル' }
Stop-Expzip $app

Complete-Suite
