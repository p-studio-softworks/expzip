<#
.SYNOPSIS
    テスト用の PC で、UI テストの実行指示を待機して走らせる「見張り」。

.DESCRIPTION
    テスト用の PC にログオンしている人が、この PC の画面で開いたままにしておく。
    Invoke-RemoteUiTests.ps1 が SSH で run\queue.txt を置くと、それを見つけて Run-UiTests.ps1 を走らせる。
    出力は run\output.log に、戻り値は run\done.txt に書く。

    SSH の中には画面が無いのでテストを走らせられず、管理者でない人はタスク スケジューラーも使えない。
    そのため、画面にログオンしている人のプロセスとして待っておく。止めるときは Ctrl+C か、ウィンドウを閉じる。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\ExpzipTestPc\repo\tests\ui\remote\Watch-UiTests.ps1"
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$run = Join-Path $HOME 'ExpzipTestPc\run'
New-Item -ItemType Directory -Force -Path $run | Out-Null
$queueFile = Join-Path $run 'queue.txt'
$output = Join-Path $run 'output.log'
$done = Join-Path $run 'done.txt'
$running = Join-Path $run 'running.txt'
$watching = Join-Path $run 'watching.txt'

# 見張っている印。Invoke-RemoteUiTests.ps1 が、見張りが動いているかを確かめるのに使う
Set-Content $watching $PID -Encoding UTF8
Write-Host 'UI テストの実行指示を待機しています。止めるときは Ctrl+C を押すか、このウィンドウを閉じてください'

try {
    while ($true) {
        if (-not (Test-Path $queueFile)) {
            Start-Sleep -Seconds 2
            continue
        }

        $queue = Get-Content $queueFile -Raw -Encoding UTF8
        Remove-Item $queueFile
        $suites = @("$queue".Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() } |
            Where-Object { $_ })
        Write-Host ("{0:HH:mm:ss} 走らせます: {1}" -f (Get-Date), $(if ($suites) { $suites -join ' ' } else { '全部' }))

        # 走らせるスクリプトは毎回ソースの場所から取る。送られたソースに切り替わっているため
        $runner = Join-Path $HOME 'ExpzipTestPc\repo\tests\ui\Run-UiTests.ps1'
        $code = 1
        try {
            $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$runner`"") + $suites
            $process = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList $arguments `
                -RedirectStandardOutput $output -RedirectStandardError "$output.err"
            $null = $process.Handle
            Set-Content $running $process.Id -Encoding UTF8
            $process.WaitForExit()
            $code = $process.ExitCode
            # Run-UiTests.ps1 は UTF-8 に切り替えてから動くが、切り替える前に止まるとコンソールの文字コードで出る
            if (Test-Path "$output.err") {
                $bytes = [System.IO.File]::ReadAllBytes("$output.err")
                $errors = try { (New-Object System.Text.UTF8Encoding $false, $true).GetString($bytes) }
                          catch { [System.Text.Encoding]::Default.GetString($bytes) }
                if ($errors.Trim()) { Add-Content $output $errors.TrimEnd() -Encoding UTF8 }
            }
        } catch {
            Add-Content $output "走らせられませんでした: $_" -Encoding UTF8
        } finally {
            Set-Content $done $code -Encoding UTF8
            Remove-Item $running -ErrorAction SilentlyContinue
        }
        Write-Host ("{0:HH:mm:ss} 終わりました (戻り値 {1})" -f (Get-Date), $code)
    }
} finally {
    Remove-Item $watching -ErrorAction SilentlyContinue
}
