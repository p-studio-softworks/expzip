<#
.SYNOPSIS
    画面を通さずに、Core の処理を直接確かめるテストを走らせる (#332)。

.DESCRIPTION
    tests\core\Expzip.Core.Tests をビルドして走らせる。画面を使わないので、走っている間も PC を使ってよい。
    数十秒で終わる。

    xUnit v3 のテストは、それ自体が実行できるプログラムになるので、dotnet test ではなく dotnet run で走らせる。
    この PC で作れないもの (シンボリックリンクなど) が要るテストは飛ばし、結果に「Skipped」と出る。

    戻り値は、落ちたら 0 以外。

.EXAMPLE
    .\tests\core\Run-CoreTests.ps1
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$project = Join-Path $PSScriptRoot 'Expzip.Core.Tests\Expzip.Core.Tests.csproj'
& dotnet run --project $project -c Release
exit $LASTEXITCODE
