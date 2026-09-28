<#
.SYNOPSIS
    Expzip をビルドして、配布できる Expzip.exe を publish\win-x64 に作る。

.DESCRIPTION
    リポジトリの直下にある build.cmd から呼ばれる。直接動かしてもよい。
    トップには入口の build.cmd だけを置き、中身はこのフォルダーに入れてある (#129)。

    必要なもの: .NET 10 SDK (https://dotnet.microsoft.com/download)
    ほかに用意するものは無い。圧縮の処理も含め、必要なものはすべて exe の中に入る。

.PARAMETER Test
    ビルドしたあとに tests\ui のテストも走らせる。
    テストの間は Expzip の窓が前に出るので、マウスとキーボードに触らないこと。

.PARAMETER Debug
    配布用ではなく、開発用にビルドするだけにする (発行しない)。

.PARAMETER Msix
    配布用の Expzip.exe を作ったあと、Microsoft Store に出す MSIX も publish\msix に作る (#176)。
    Windows SDK (makeappx.exe) が要る。署名はしない。Store に出すと Microsoft が署名する。
    手元で試すときは、publish\msix\layout を開発者モードで登録する。

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Test
    .\build\build.ps1 -Msix
#>
param(
    [switch]$Test,
    [switch]$Debug,
    [switch]$Msix
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# このファイルは build\ の中にあるので、リポジトリはその 1 つ上
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo 'publish\win-x64'
$exe = Join-Path $publish 'Expzip.exe'

# .NET SDK があるか
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host '.NET SDK が見つかりません。https://dotnet.microsoft.com/download から .NET 10 SDK を入れてください。'
    exit 1
}

$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Host '.NET 10 SDK が見つかりません。入っている SDK:'
    $sdks | ForEach-Object { Write-Host "  $_" }
    exit 1
}

# 動いている Expzip があると、作った exe を置き換えられない
$running = @(Get-Process Expzip -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) })
if ($running.Count -gt 0) {
    Write-Host "Expzip が動いています。閉じてからもう一度実行してください ($exe)"
    exit 1
}

if ($Debug) {
    Write-Host '開発用にビルドしています…'
    & dotnet build (Join-Path $repo 'Expzip.slnx') -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    Write-Host '配布用の Expzip.exe を作っています。数分かかることがあります…'
    & dotnet publish (Join-Path $repo 'src\Expzip') -p:PublishProfile=win-x64 -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $built = Get-Item $exe
    Write-Host ''
    Write-Host ("できました: {0}" -f $built.FullName)
    Write-Host ("大きさ: {0:N0} バイト" -f $built.Length)
    Write-Host 'この 1 つのファイルをコピーするだけで動きます。'
}

if ($Msix -and -not $Debug) {
    # makeappx は Windows SDK に入っている。いちばん新しいものを使う
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $makeappx = Get-ChildItem $kits -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -ErrorAction SilentlyContinue |
        Select-Object -Last 1
    if (-not $makeappx) {
        Write-Host 'Windows SDK の makeappx.exe が見つかりません。https://developer.microsoft.com/windows/downloads/windows-sdk/ から入れてください。'
        exit 1
    }

    # バージョンは exe と同じもの。4 つ目は Store が使うので 0 にする
    $version = (Get-Item $exe).VersionInfo
    $packageVersion = '{0}.{1}.{2}.0' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart

    $msixDir = Join-Path $repo 'publish\msix'
    $layout = Join-Path $msixDir 'layout'
    if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
    New-Item -ItemType Directory $layout | Out-Null

    Copy-Item $exe $layout
    Copy-Item (Join-Path $PSScriptRoot 'msix\Assets') $layout -Recurse
    $manifest = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'msix\AppxManifest.xml'))
    [System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'),
        $manifest.Replace('{VERSION}', $packageVersion), (New-Object System.Text.UTF8Encoding $false))

    $package = Join-Path $msixDir ("Expzip_{0}_x64.msix" -f $packageVersion)
    Write-Host ''
    Write-Host 'MSIX を作っています…'
    & $makeappx.FullName pack /o /h SHA256 /d $layout /p $package | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'MSIX を作れませんでした。makeappx の出力を確かめてください。'
        & $makeappx.FullName pack /o /h SHA256 /d $layout /p $package
        exit 1
    }

    Write-Host ("できました: {0}" -f $package)
    Write-Host 'Store に出すものです。署名していないので、このままでは手元に入れられません。'
}

if ($Test) {
    Write-Host ''
    Write-Host 'テストを走らせます。終わるまでマウスとキーボードに触らないでください…'
    & (Join-Path $repo 'tests\ui\Run-UiTests.ps1')
    exit $LASTEXITCODE
}
