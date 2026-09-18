<#
.SYNOPSIS
    Expzip を建てて、配布できる Expzip.exe を publish\win-x64 に作る。

.DESCRIPTION
    build.cmd から呼ばれる。直接動かしてもよい。

    必要なもの: .NET 10 SDK (https://dotnet.microsoft.com/download)
    ほかに用意するものは無い。圧縮の処理も含め、必要なものはすべて exe の中に入る。

.PARAMETER Test
    建てたあとに、画面越しの確認 (tests\ui) も走らせる。
    確認の間は Expzip の窓が前に出るので、マウスとキーボードに触らないこと。

.PARAMETER Debug
    配布用ではなく、開発用に建てるだけにする (発行しない)。

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Test
#>
param(
    [switch]$Test,
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repo = $PSScriptRoot
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
    Write-Host '開発用に建てています…'
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

if ($Test) {
    Write-Host ''
    Write-Host '画面越しの確認を走らせます。終わるまでマウスとキーボードに触らないでください…'
    & (Join-Path $repo 'tests\ui\Run-UiTests.ps1')
    exit $LASTEXITCODE
}
