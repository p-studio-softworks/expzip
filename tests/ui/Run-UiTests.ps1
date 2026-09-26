<#
.SYNOPSIS
    Expzip を起動して画面を操作するテストを、まとめて走らせる。

.DESCRIPTION
    Expzip をビルドして一時フォルダーに置き、suites\*.Tests.ps1 を 1 本ずつ別のプロセスで走らせる。
    同じプロセスに詰めると Add-Type がぶつかり、前のウィンドウが前面を奪うため。

    - 走っている間はマウスとキーボードに触らないでください。ウィンドウを前に出し、キーを送って操作します
    - Windows の表示言語が日本語であることを前提にしています (MessageBox の「はい(Y)」など)
    - 1 本ずつの記録は %TEMP%\ExpzipUiTests\results に残ります
    - 落ちた記録と、最初に落ちたときの画面は %TEMP%\ExpzipUiTests\failed にも残ります。走らせ直しても消えません (#169)
    - 1 本が 300 秒を超えたら止めて次へ進みます (-TimeoutSeconds で変えられます)
    - Expzip は %TEMP%\ExpzipUiTests\app にビルドします。普段使っている Expzip や publish\ には触りません
    - AI の接続先は、この PC の中に立てたテスト用のものを使います。外へは何も送りません

    テストのスクリプトを書くときは、ファイルを BOM 付き UTF-8 にしてください (PowerShell 5.1 は BOM が無いと日本語を読み違えます)。
    共通のツールは Common.ps1 にあり、1 項目の結果は Check '名前' (条件) 詳細 で出します。

.EXAMPLE
    .\tests\ui\Run-UiTests.ps1
    .\tests\ui\Run-UiTests.ps1 About Toolbar
    .\tests\ui\Run-UiTests.ps1 -NoBuild Browse
#>
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Suite = @(),
    [switch]$NoBuild,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$root = Join-Path $env:TEMP 'ExpzipUiTests'
$app = Join-Path $root 'app'
$results = Join-Path $root 'results'
$failed = Join-Path $root 'failed'
New-Item -ItemType Directory -Force -Path $failed | Out-Null

$running = @(Get-Process Expzip -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) })
foreach ($p in $running) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }

if (-not $NoBuild) {
    Write-Host 'ビルドしています…'
    Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
    & dotnet build (Join-Path $repo 'src\Expzip\Expzip.csproj') -c Release -o $app -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました' }
}

$all = @(Get-ChildItem (Join-Path $PSScriptRoot 'suites') -Filter '*.Tests.ps1' | Sort-Object Name)
$chosen = if ($Suite.Count -eq 0) { $all } else {
    foreach ($name in $Suite) {
        $match = $all | Where-Object { $_.BaseName -ieq "$name.Tests" }
        if (-not $match) { throw "テストがありません: $name" }
        $match
    }
}

Remove-Item $results -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $results | Out-Null

$totalFailed = 0
foreach ($file in $chosen) {
    $name = $file.BaseName -replace '\.Tests$', ''
    $log = Join-Path $results "$name.log"
    Write-Host ("{0,-14} " -f $name) -NoNewline

    $env:EXPZIP_UI_APP = $app
    $started = Get-Date
    $process = Start-Process powershell -PassThru -WindowStyle Hidden `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($file.FullName)`"" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"

    # 手綱を先に握っておく。握らないまま終わると戻り値が読めない
    $null = $process.Handle

    # 固まったテストで全体が止まらないようにする
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        $process.Kill()
        $process.WaitForExit()
        Get-Process Expzip -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { $_.Kill() }
        Add-Content "$log.err" "$TimeoutSeconds 秒で終わらなかったため止めました" -Encoding UTF8
    }
    $seconds = [int]((Get-Date) - $started).TotalSeconds

    $lines = @(Get-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue)
    # 止まったときの英語や日本語のエラーは、コンソールの文字コードで出てくる
    $errors = @(Get-Content "$log.err" -Encoding Default -ErrorAction SilentlyContinue | Where-Object { $_ })
    $ok = @($lines | Where-Object { $_ -match '^\s+OK\s' }).Count
    $ng = @($lines | Where-Object { $_ -match '^\s+NG\s' }).Count
    $crashed = ($process.ExitCode -ne $ng) -or ($errors.Count -gt 0)
    if ($crashed -and $ng -eq 0) { $ng = 1 }
    $totalFailed += $ng

    $mark = if ($ng -eq 0) { '通過' } else { '失敗' }
    Write-Host ("{0}  OK {1,3} / NG {2,3}  {3,3} 秒" -f $mark, $ok, $ng, $seconds)
    $lines | Where-Object { $_ -match '^\s+NG\s' } | ForEach-Object { Write-Host "      $_" }
    if ($crashed) {
        Write-Host "      途中で止まりました (戻り値 $($process.ExitCode))"
        $errors | Select-Object -First 8 | ForEach-Object { Write-Host "      $_" }
    }

    # 落ちた記録は別の場所にも取っておく (#169)。results は走らせるたびに消すので、
    # たまにしか落ちないテストを走らせ直すと、何が起きたのかが残らなかった
    if ($ng -gt 0) {
        $kept = Join-Path $failed ('{0:yyyyMMdd-HHmmss}-{1}' -f $started, $name)
        Copy-Item $log "$kept.log" -ErrorAction SilentlyContinue
        Copy-Item "$log.err" "$kept.log.err" -ErrorAction SilentlyContinue
        Write-Host "      記録: $kept.log"
    }
}

# 古いものは消す。落ちるたびに増え続けないように、新しい 60 個だけ残す
Get-ChildItem $failed -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending |
    Select-Object -Skip 60 | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "記録: $results"
if ($totalFailed -eq 0) { Write-Host 'すべて通りました' } else { Write-Host "$totalFailed 件が落ちました" }
exit $totalFailed
