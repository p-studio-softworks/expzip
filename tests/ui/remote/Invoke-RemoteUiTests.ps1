<#
.SYNOPSIS
    UI テストを別の PC (テスト用の PC) で走らせ、記録をこの PC に持ってくる。

.DESCRIPTION
    UI テストは本物のキーとマウスで操作するので、走っている間はその PC に触れない。
    テスト用の PC で走らせれば、この PC はその間も使える。

    1. この PC の作業中のソース (コミットしていない変更と、新しいファイルを含む) を 1 つのコミットにまとめ、
       git bundle にしてテスト用の PC へ送る。この PC のブランチや、ステージした内容は変えない
    2. テスト用の PC で、そのソースに切り替える
    3. テスト用の PC の画面で開いてある見張り (Watch-UiTests.ps1) に頼み、Run-UiTests.ps1 を走らせる。
       ログオンしている人として、その PC の画面で動く (SSH の中では画面が無く、キーもマウスも送れないため)
    4. 終わるまで進み具合を出し、終わったら記録を %TEMP%\ExpzipUiTests\remote\<日時> に持ってくる

    テスト用の PC は、ログオンしたまま画面がロックされない状態にしておく。
    テスト用の PC には、SSH のサーバー、Git、.NET 10 の SDK を入れ、この PC の鍵 ($HOME\.ssh\expzip_testpc) で
    つなげるようにしておく。最初に 1 回だけ -Setup を付けて走らせ、そのあと、テスト用の PC の画面で
    Watch-UiTests.ps1 を開いたままにしておく。

.EXAMPLE
    .\tests\ui\remote\Invoke-RemoteUiTests.ps1 -HostName user@testpc -Setup
    .\tests\ui\remote\Invoke-RemoteUiTests.ps1 Browse
    .\tests\ui\remote\Invoke-RemoteUiTests.ps1
    .\tests\ui\remote\Invoke-RemoteUiTests.ps1 -Attach
#>
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Suite = @(),
    # SSH の接続先。省くと環境変数 EXPZIP_TEST_PC を使う (例: user@testpc)
    [string]$HostName = $env:EXPZIP_TEST_PC,
    # 初めてのときに、Git と .NET の SDK を入れ、スリープを切るなどの用意をする
    [switch]$Setup,
    # 走らせ始めたあとで切れたときに、走っているテストにつなぎ直して、終わりを待って記録を持ってくる
    [switch]$Attach,
    [int]$PollSeconds = 20
)

# git と ssh は注意を標準エラーに出す。出力をファイルに回したときに、それで止まらないよう 'Stop' にしない。
# 失敗は戻り値で確かめる
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not $HostName) {
    # 設定したばかりの環境変数は、前から開いている PowerShell には届いていない
    $HostName = [Environment]::GetEnvironmentVariable('EXPZIP_TEST_PC', 'User')
}
if (-not $HostName) {
    throw '接続先がありません。-HostName を付けるか、環境変数 EXPZIP_TEST_PC に入れてください'
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$key = Join-Path $HOME '.ssh\expzip_testpc'
$sshOptions = @('-i', $key, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15')

# テスト用の PC で PowerShell のスクリプトを走らせる。引用符の付け方で壊れないよう、エンコードして渡す
function Invoke-Remote([string]$Script) {
    # 進み具合の表示は、SSH では読めない形 (CLIXML) で混ざるので出さない
    $prefix = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; `$ErrorActionPreference = 'Stop'; `$ProgressPreference = 'SilentlyContinue'; " +
        "`$base = Join-Path `$HOME 'ExpzipTestPc'; " +
        "`$git = if (Get-Command git -ErrorAction SilentlyContinue) { 'git' } else { Join-Path `$env:ProgramFiles 'Git\cmd\git.exe' }; "
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($prefix + $Script))
    $output = & ssh @sshOptions $HostName "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    if ($LASTEXITCODE -ne 0) {
        $output | Out-Host
        throw "テスト用の PC での処理に失敗しました (戻り値 $LASTEXITCODE)"
    }
    $output
}

# 見張りと走っているテストは、それぞれが書く印 (プロセス番号) で見る
$alive = "function Test-Alive([string]`$Name) { `$f = Join-Path `$base ('run\' + `$Name); if (-not (Test-Path `$f)) { return `$false }; " +
    "[bool](Get-Process -Id ([int](Get-Content `$f -Raw).Trim()) -ErrorAction SilentlyContinue) }; "

if ($Attach) {
    # 始めた時刻は、テスト用の PC に残した印から読む。この時刻より新しい落ちた記録を持ってくる
    $since = Invoke-Remote @"
`$file = Join-Path `$base 'run\started.txt'
if (-not (Test-Path `$file)) { throw 'つなぎ直せるテストがありません' }
(Get-Content `$file -Raw).Trim()
"@
    $started = [DateTime]::Parse("$since".Trim(), $null, [Globalization.DateTimeStyles]::RoundtripKind).ToLocalTime()
    Write-Host ("{0:HH:mm:ss} に始めたテストにつなぎ直しました" -f $started)
} else {
# --- 1. 作業中のソースを bundle にする -------------------------------------------
# 一時の索引を使うので、この PC のステージした内容は変わらない
$work = Join-Path $env:TEMP 'ExpzipUiTests\remote-send'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$index = Join-Path $work 'index'
$bundle = Join-Path $work 'snapshot.bundle'
$ref = 'refs/remote-test/snapshot'

Copy-Item (Join-Path $repo '.git\index') $index -Force
$env:GIT_INDEX_FILE = $index
try {
    git -C $repo add -A 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'git add に失敗しました' }
    $tree = git -C $repo write-tree
} finally {
    Remove-Item Env:GIT_INDEX_FILE
}
$head = git -C $repo rev-parse HEAD
$commit = git -C $repo commit-tree $tree -p $head -m 'remote test snapshot'
git -C $repo update-ref $ref $commit
try {
    git -C $repo bundle create -q $bundle $ref
    if ($LASTEXITCODE -ne 0) { throw 'git bundle に失敗しました' }
} finally {
    git -C $repo update-ref -d $ref
}
git -C $repo diff --quiet $head $commit
$changed = $LASTEXITCODE -ne 0
Write-Host ("送るソース: {0}{1}" -f $head.Substring(0, 7), $(if ($changed) { ' + コミットしていない変更' } else { '' }))

# --- 2. 初めてのときの用意 --------------------------------------------------------
if ($Setup) {
    Write-Host 'Git と .NET の SDK を確かめています…'
    # SSH の中では winget のパッケージの一覧を開けない (0x8A15000F) ので、配布元から直接入れる
    Invoke-Remote @'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
function Install-From([string]$Name, [string]$Url, [string[]]$Arguments) {
    Write-Output "$Name を入れています…"
    $installer = Join-Path $env:TEMP ([IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
    Invoke-WebRequest $Url -OutFile $installer -UseBasicParsing
    $process = Start-Process $installer -ArgumentList $Arguments -Wait -PassThru
    Remove-Item $installer -ErrorAction SilentlyContinue
    if ($process.ExitCode -notin 0, 3010) { throw "$Name を入れられませんでした (戻り値 $($process.ExitCode))" }
}
if (-not (Test-Path (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'))) {
    $release = Invoke-RestMethod 'https://api.github.com/repos/git-for-windows/git/releases/latest' -UseBasicParsing
    $asset = $release.assets | Where-Object { $_.name -match '^Git-[\d.]+-64-bit\.exe$' } | Select-Object -First 1
    Install-From 'Git' $asset.browser_download_url '/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES'
}
if (-not (Test-Path (Join-Path $env:ProgramFiles 'dotnet\sdk\10.*'))) {
    Install-From '.NET 10 の SDK' 'https://aka.ms/dotnet/10.0/dotnet-sdk-win-x64.exe' '/install', '/quiet', '/norestart'
}
New-Item -ItemType Directory -Force -Path (Join-Path $base 'inbox'), (Join-Path $base 'run') | Out-Null
'@ | Out-Host
}

# --- 3. 送って切り替える ----------------------------------------------------------
Write-Host 'ソースを送っています…'
& scp @sshOptions -q $bundle "${HostName}:ExpzipTestPc/inbox/snapshot.bundle"
if ($LASTEXITCODE -ne 0) { throw "送れませんでした。-Setup を付けて走らせたか確かめてください (戻り値 $LASTEXITCODE)" }

Invoke-Remote @"
`$repo = Join-Path `$base 'repo'
if (-not (Test-Path (Join-Path `$repo '.git'))) { & `$git init -q `$repo }
& `$git -C `$repo fetch -q (Join-Path `$base 'inbox\snapshot.bundle') '+${ref}:${ref}'
& `$git -C `$repo checkout -q --force --detach $commit
& `$git -C `$repo clean -q -fd
if (`$LASTEXITCODE -ne 0) { throw 'ソースを切り替えられませんでした' }
"@ | Out-Host

if ($Setup) {
    Write-Host 'テスト用の PC を確かめています…'
    Invoke-Remote "& (Join-Path `$base 'repo\tests\ui\remote\Initialize-TestPc.ps1')" | Out-Host
    Write-Host '用意ができました。次からは -Setup を付けずに走らせます'
    return
}

# --- 4. 走らせて、終わるまで待つ --------------------------------------------------
$started = Get-Date
$queue = ($Suite | ForEach-Object { $_ -replace '[^\w-]', '' }) -join ' '
# テストは、テスト用の PC の画面で開いてある見張り (Watch-UiTests.ps1) が走らせる。
Invoke-Remote ($alive + @"
`$run = Join-Path `$base 'run'
if (-not (Test-Alive 'watching.txt')) { throw 'テスト用の PC で見張り (Watch-UiTests.ps1) が動いていません。テスト用の PC の画面で開いてください' }
if ((Test-Alive 'running.txt') -or (Test-Path (Join-Path `$run 'queue.txt'))) { throw 'テスト用の PC では、前のテストがまだ走っています' }
Remove-Item (Join-Path `$run 'done.txt'), (Join-Path `$run 'output.log') -ErrorAction SilentlyContinue
Set-Content (Join-Path `$run 'started.txt') '$($started.ToUniversalTime().ToString('o'))' -Encoding UTF8
# 見張りが書きかけを読まないよう、別の名前で書いてから置く
Set-Content (Join-Path `$run 'queue.tmp') '$queue' -Encoding UTF8
Move-Item (Join-Path `$run 'queue.tmp') (Join-Path `$run 'queue.txt') -Force
foreach (`$i in 1..30) {
    if ((Test-Path (Join-Path `$run 'running.txt')) -or (Test-Path (Join-Path `$run 'done.txt'))) { return }
    Start-Sleep -Seconds 1
}
throw 'テストが始まりませんでした。テスト用の PC の見張りのウィンドウを確かめてください'
"@) | Out-Host
Write-Host "テスト用の PC で走らせ始めました。その PC には触らないでください"
}

$shown = 0
$misses = 0
while ($true) {
    Start-Sleep -Seconds $PollSeconds
    # 進み具合を読むだけなので、SSH が一度切れても止めない。テストはテスト用の PC で続いている
    try {
    $reply = @(Invoke-Remote ($alive + @"
`$run = Join-Path `$base 'run'
`$done = Join-Path `$run 'done.txt'
`$finished = Test-Path `$done
# 書き終わった行だけを出す。書きかけの行を 1 行と数えると、続きを読み飛ばす
`$raw = [string](Get-Content (Join-Path `$run 'output.log') -Raw -Encoding UTF8 -ErrorAction SilentlyContinue)
`$lines = `$raw -split '\r?\n'
if (-not `$finished) { `$lines = `$lines | Select-Object -SkipLast 1 }
`$lines | Select-Object -Skip $shown
if (`$finished) { '##DONE ' + (Get-Content `$done -Raw).Trim() }
elseif (-not (Test-Alive 'running.txt')) { '##DONE stopped' }
"@))
    } catch {
        $misses++
        if ($misses -ge 15) { throw "テスト用の PC に $misses 回続けてつながりませんでした。つながるようになったら -Attach を付けて走らせてください" }
        Write-Host "  (テスト用の PC につながりませんでした。$PollSeconds 秒後にもう一度読みます)"
        continue
    }
    $misses = 0
    $end = $reply | Where-Object { $_ -like '##DONE *' } | Select-Object -First 1
    $lines = @($reply | Where-Object { $_ -notlike '##DONE *' })
    $lines | ForEach-Object { Write-Host $_ }
    $shown += $lines.Count
    if ($end) { break }
}
$code = $end.Substring(7)

# --- 5. 記録を持ってくる ----------------------------------------------------------
$local = Join-Path $env:TEMP ('ExpzipUiTests\remote\{0:yyyyMMdd-HHmmss}' -f $started)
New-Item -ItemType Directory -Force -Path $local | Out-Null
$since = $started.ToUniversalTime().ToString('o')
Invoke-Remote @"
`$root = Join-Path `$env:TEMP 'ExpzipUiTests'
`$pack = Join-Path `$base 'run\records'
Remove-Item `$pack -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path `$pack 'failed') | Out-Null
Copy-Item (Join-Path `$root 'results') `$pack -Recurse -ErrorAction SilentlyContinue
Copy-Item (Join-Path `$base 'run\output.log') `$pack -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path `$root 'failed') -File -ErrorAction SilentlyContinue |
    Where-Object { `$_.LastWriteTimeUtc -ge [DateTime]::Parse('$since').ToUniversalTime() } |
    Copy-Item -Destination (Join-Path `$pack 'failed')
Compress-Archive (Join-Path `$pack '*') (Join-Path `$base 'run\records.zip') -Force
"@ | Out-Null
& scp @sshOptions -q "${HostName}:ExpzipTestPc/run/records.zip" (Join-Path $local 'records.zip')
if ($LASTEXITCODE -eq 0) {
    Expand-Archive (Join-Path $local 'records.zip') $local -Force
    Remove-Item (Join-Path $local 'records.zip')
    Write-Host "記録: $local"
} else {
    Write-Host '記録を持ってこられませんでした'
}

if ($code -eq 'stopped') {
    Write-Host 'テストが途中で止まりました。テスト用の PC の画面と output.log を確かめてください'
    exit 1
}
exit [int]$code
