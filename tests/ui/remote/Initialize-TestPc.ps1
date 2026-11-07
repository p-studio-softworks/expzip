<#
.SYNOPSIS
    テスト用の PC で、テストの途中に画面が止まらないようにし、走らせられる状態かを確かめる。

.DESCRIPTION
    Invoke-RemoteUiTests.ps1 -Setup から SSH で呼ばれる。テスト用の PC で直接走らせてもよい。
    何度走らせても同じ状態になる。

    - 電源につないでいるときのスリープ、休止、画面の消灯を切る。スクリーン セーバーを切る
    - エクスプローラーでファイル名拡張子を表示する
    - 表示言語と、ログオンしているかと、見張り (Watch-UiTests.ps1) が動いているかを確かめて知らせる
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# テストの途中で画面が止まると、キーもマウスも届かなくなる
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /change monitor-timeout-ac 0
if ($LASTEXITCODE -ne 0) {
    Write-Output '注意: スリープと画面の消灯を切れませんでした。設定の「電源」で「なし」にしてください'
} else {
    Write-Output '電源につないでいるときのスリープ、休止、画面の消灯を切りました'
}
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ScreenSaveActive -Value '0'
Write-Output 'スクリーン セーバーを切りました'

# テストは、名前の候補が「資料.zip」のように拡張子まで見えることを前提にしている。
# 開いたままのエクスプローラーには、サインインし直すまで効かない
$advanced = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
if ((Get-ItemProperty $advanced -Name HideFileExt -ErrorAction SilentlyContinue).HideFileExt -ne 0) {
    Set-ItemProperty $advanced -Name HideFileExt -Value 0 -Type DWord
    Write-Output 'ファイル名拡張子を表示するようにしました。エクスプローラーに効かせるには、サインインし直してください'
}

$language = (Get-WinUserLanguageList)[0].LanguageTag
if ($language -notlike 'ja*') {
    Write-Output "注意: 表示言語が $language です。UI テストは日本語の表示を前提にしています"
}

# 自分のエクスプローラーなら場所が読める。ほかの人のものは読めない
$mine = @(Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 -and $_.Path })
if ($mine.Count -eq 0) {
    Write-Output "注意: $env:USERNAME がこの PC にログオンしていません。ログオンしてからテストを走らせてください"
}

$watching = Join-Path $HOME 'ExpzipTestPc\run\watching.txt'
$watcher = if (Test-Path $watching) { Get-Process -Id ([int](Get-Content $watching -Raw).Trim()) -ErrorAction SilentlyContinue }
if (-not $watcher) {
    Write-Output 'まだ見張りが動いていません。テスト用の PC で、ふつうの PowerShell を開いて次を走らせ、開いたままにしてください:'
    Write-Output "  powershell -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'Watch-UiTests.ps1')`""
}
