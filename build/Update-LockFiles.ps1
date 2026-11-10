<#
.SYNOPSIS
    パッケージの lock ファイル (packages.lock.json) を作り直す (#323)。

.DESCRIPTION
    取り込むパッケージの中身は lock ファイルの SHA512 で固定してあり、ふだんの復元は lock ファイルと違えば止まる
    (Directory.Build.props の RestoreLockedMode)。パッケージを足したり、バージョンを上げたりしたときだけ、これで作り直す。
    .NET の SDK を上げたときも作り直す。配布用の lock ファイルにある Microsoft.NET.ILLink.Tasks は SDK のバージョンに連動していて、
    上げると配布用のビルドが NU1004 で止まる。

    配布用のビルドは win-x64 用の部品と発行のときだけ使う部品を加えて復元するので、lock ファイルを分けてある
    (packages.win-x64.lock.json)。開発用、配布用、テスト、ツールのすべてを作り直す。

    作り直したら、git diff で、変わったのが上げたパッケージだけかを確かめてからコミットする。

.EXAMPLE
    .\build\Update-LockFiles.ps1
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repo = Split-Path $PSScriptRoot -Parent

$runs = @(
    @{ Path = 'Expzip.slnx'; Extra = @() },
    @{ Path = 'src\Expzip\Expzip.csproj'; Extra = @('-p:PublishProfile=win-x64') },
    @{ Path = 'tests\core\Expzip.Core.Tests\Expzip.Core.Tests.csproj'; Extra = @() },
    @{ Path = 'tools\stringdump\stringdump.csproj'; Extra = @() },
    @{ Path = 'tools\icon\icon.csproj'; Extra = @() },
    @{ Path = 'tools\uifixtures\uifixtures.csproj'; Extra = @() }
)

foreach ($run in $runs) {
    Write-Host "作り直しています: $($run.Path) $($run.Extra -join ' ')"
    & dotnet restore (Join-Path $repo $run.Path) --force-evaluate -nologo -v q @($run.Extra)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host '作り直しました。git diff で、変わったのが上げたパッケージだけかを確かめてください。'
