# 画面を操作するテストで共通に使うツール。テストのスクリプトそれぞれの先頭で . "$PSScriptRoot\..\Common.ps1" として読み込む。
#
# - 動かす Expzip は Run-UiTests.ps1 がビルドして一時フォルダーに置いたもの。
#   公開用の publish\ や、利用者が使っている Expzip には触らない
# - 設定ファイルは exe の隣に置かれるので、起動のたびに Set-Settings で作り直す
# - 結果は「  OK   名前」「  NG   名前」の行で出す。Run-UiTests.ps1 がこの行を数える

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

if (-not ('ExpzipUi.Native' -as [type])) {
    Add-Type -Namespace ExpzipUi -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int max);
[DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int width, int height, bool repaint);
'@
}

$script:Automation = [System.Windows.Automation.AutomationElement]
$script:Scope = [System.Windows.Automation.TreeScope]
$script:ControlType = [System.Windows.Automation.ControlType]

$script:Passed = 0
$script:Failed = 0

# ------------------------------------------------------------------ テストの始まりと終わり

function Start-Suite([string]$Name) {
    $script:SuiteName = $Name
    $script:AppDir = if ($env:EXPZIP_UI_APP) { $env:EXPZIP_UI_APP } else {
        Join-Path $env:TEMP 'ExpzipUiTests\app'
    }
    $script:Exe = Join-Path $script:AppDir 'Expzip.exe'
    $script:SettingsPath = Join-Path $script:AppDir 'Expzip.settings.json'
    $script:RulesPath = Join-Path $script:AppDir 'Expzip.rules.json'

    if (-not (Test-Path $script:Exe)) {
        throw "Expzip.exe がありません: $script:Exe (Run-UiTests.ps1 から走らせてください)"
    }

    $script:Work = Join-Path $env:TEMP "ExpzipUiTests\work\$Name"
    Stop-TestExpzip
    Remove-Item $script:SettingsPath, $script:RulesPath -Force -ErrorAction SilentlyContinue
    Remove-Item $script:Work -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $script:Work | Out-Null

    Write-Host "# $Name"
}

function Complete-Suite {
    Stop-TestExpzip
    Remove-Item $script:SettingsPath, $script:RulesPath -Force -ErrorAction SilentlyContinue
    Write-Host ''
    Write-Host ("結果: OK {0} / NG {1}" -f $script:Passed, $script:Failed)
    exit $script:Failed
}

function Section([string]$Title) {
    if (-not $script:Clock) { $script:Clock = [Diagnostics.Stopwatch]::StartNew() }
    Write-Host ''
    Write-Host ("== {0} == ({1:0} 秒)" -f $Title, $script:Clock.Elapsed.TotalSeconds)
}

function Check([string]$Label, $Ok, $Detail = '') {
    $suffix = if ("$Detail".Length -gt 0) { " ... $Detail" } else { '' }
    if ($Ok) {
        $script:Passed++
        Write-Host ("  OK   " + $Label + $suffix)
    }
    else {
        $script:Failed++
        Write-Host ("  NG   " + $Label + $suffix)
        Save-FailureShot $Label
    }
}

# 最初の NG のときの画面を撮っておく (#169)。たまにしか落ちないテストは、走らせ直すと
# 通ってしまい、何が起きていたのか分からなかった。前に何のウィンドウがあったかが一番の手掛かりになる
function Save-FailureShot([string]$Label) {
    if ($script:ShotTaken) { return }
    $script:ShotTaken = $true
    try {
        $folder = Join-Path $env:TEMP 'ExpzipUiTests\failed'
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bmp.Size)
        $g.Dispose()
        $path = Join-Path $folder ('{0:yyyyMMdd-HHmmss}-{1}.png' -f (Get-Date), $script:SuiteName)
        $bmp.Save($path)
        $bmp.Dispose()
        Write-Host ("        画面: {0} (前にあったウィンドウ: {1})" -f $path, (Get-ForegroundTitle))
    }
    catch {
        Write-Host "        画面を撮れませんでした: $_"
    }
}

# いま前にあるウィンドウの題名
function Get-ForegroundTitle {
    $text = New-Object System.Text.StringBuilder 256
    [ExpzipUi.Native]::GetWindowText([ExpzipUi.Native]::GetForegroundWindow(), $text, 256) | Out-Null
    return $text.ToString()
}

# ------------------------------------------------------------------ 用意

function Set-Settings([hashtable]$Values = @{}) {
    $settings = [ordered]@{ Language = 'ja' }
    foreach ($key in $Values.Keys) { $settings[$key] = $Values[$key] }
    $json = $settings | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($script:SettingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-Settings {
    if (-not (Test-Path $script:SettingsPath)) { return '' }
    return [System.IO.File]::ReadAllText($script:SettingsPath, [System.Text.Encoding]::UTF8)
}

# 書庫を作る。キーが / で終わるものはフォルダー、それ以外はファイル。
# 値が文字列なら UTF-8 の中身、バイト列ならそのまま (書庫の中の書庫に使う)
# $Stored に挙げた名前は圧縮せずに入れる (格納)
function New-TestZip([string]$Path, [System.Collections.IDictionary]$Entries, [string[]]$Stored = @()) {
    Remove-Item $Path -Force -ErrorAction SilentlyContinue
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $Entries.Keys) {
                $entry = if ($Stored -contains $name) {
                    $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::NoCompression)
                } else { $zip.CreateEntry($name) }
                if ($name.EndsWith('/')) { continue }
                $value = $Entries[$name]
                $bytes = if ($value -is [byte[]]) { $value } else {
                    (New-Object System.Text.UTF8Encoding($false)).GetBytes([string]$value)
                }
                $output = $entry.Open()
                $output.Write($bytes, 0, $bytes.Length)
                $output.Dispose()
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $stream.Dispose() }
    return $Path
}

function Get-ZipNames([string]$Path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($zip.Entries | ForEach-Object { $_.FullName }) }
    finally { $zip.Dispose() }
}

# 書庫の中の書庫に並ぶ名前
function Get-InnerZipNames([string]$Path, [string]$Inner) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq $Inner } | Select-Object -First 1
        if ($null -eq $entry) { return @() }
        $memory = New-Object System.IO.MemoryStream
        $source = $entry.Open()
        try { $source.CopyTo($memory) } finally { $source.Dispose() }
        $memory.Position = 0
        $inside = New-Object System.IO.Compression.ZipArchive($memory)
        try { return @($inside.Entries | ForEach-Object { $_.FullName }) }
        finally { $inside.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Get-Tabs($App) {
    return @(ByType (ById $App.Window 'ArchiveTabs') $script:ControlType::TabItem)
}

# ------------------------------------------------------------------ テスト用の AI 接続先
#
# この PC の中だけで待ち受け、決めておいた答えを順に返す。外へは何も出ない。
# 受け取った本文は ai-requests.txt に 1 行ずつ残す

function Start-AiStub([int]$Port, [object[]]$Answers) {
    $log = Join-Path $script:Work 'ai-requests.txt'
    $answersPath = Join-Path $script:Work 'ai-answers.json'
    $scriptPath = Join-Path $script:Work 'ai-stub.ps1'
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    [System.IO.File]::WriteAllText($answersPath, (ConvertTo-Json @($Answers) -Depth 6), [System.Text.Encoding]::UTF8)

    # ジョブで動かすと、待ち受けの最中は止めるのに数分かかる。別のプロセスで動かし、強制的に終了できるようにする
    $stub = @'
param($port, $answersPath, $log)
# ConvertFrom-Json は配列を 1 つの値として返すので、並べ直して開く
$answers = @(ConvertFrom-Json (Get-Content $answersPath -Raw -Encoding UTF8) | ForEach-Object { $_ })
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$port/")
$listener.Start()
$count = 0
while ($listener.IsListening) {
    $context = $listener.GetContext()
    $reader = New-Object System.IO.StreamReader($context.Request.InputStream, [System.Text.Encoding]::UTF8)
    $body = $reader.ReadToEnd()
    $reader.Close()
    $auth = [string]$context.Request.Headers['Authorization']
    $line = (@{ path = $context.Request.Url.AbsolutePath; auth = $auth; body = $body } | ConvertTo-Json -Compress -Depth 4)
    [System.IO.File]::AppendAllText($log, $line + "`n", [System.Text.Encoding]::UTF8)

    $answer = $answers[[Math]::Min($count, $answers.Count - 1)]
    $count++
    $bytes = [System.Text.Encoding]::UTF8.GetBytes([string]$answer.Body)
    $context.Response.StatusCode = [int]$answer.Status
    $context.Response.ContentType = 'application/json'
    $context.Response.ContentLength64 = $bytes.Length
    $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $context.Response.Close()
}
'@
    [System.IO.File]::WriteAllText($scriptPath, $stub, (New-Object System.Text.UTF8Encoding($true)))
    $process = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scriptPath`"", $Port, "`"$answersPath`"", "`"$log`"")
    Start-Sleep -Seconds 2
    return [pscustomobject]@{ Process = $process; Endpoint = "http://127.0.0.1:$Port/v1"; Log = $log }
}

function Stop-AiStub($Stub) {
    if ($null -eq $Stub -or $Stub.Process.HasExited) { return }
    $Stub.Process.Kill()
    $Stub.Process.WaitForExit(5000) | Out-Null
}

function Get-AiRequests($Stub) {
    if (-not (Test-Path $Stub.Log)) { return @() }
    # 1 件だけでも配列のまま返す。PowerShell 5.1 は 1 件の PSCustomObject に Count を持たせない
    return ,@(Get-Content $Stub.Log -Encoding UTF8 | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

# OpenAI 互換の答えの形に包む
function New-AiAnswer([string]$Content) {
    $payload = @{ choices = @(@{ message = @{ role = 'assistant'; content = $Content } }) }
    return @{ Status = 200; Body = ($payload | ConvertTo-Json -Depth 6 -Compress) }
}

# ------------------------------------------------------------------ 起動と終了

function Stop-TestExpzip {
    if (-not $script:Exe) { return }
    Get-Process Expzip -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -ieq $script:Exe) } |
        ForEach-Object {
            # 終わりかけのプロセスは Kill を断ることがある。終わるのを待てばよい
            try { $_.Kill() } catch [System.ComponentModel.Win32Exception] { }
            $_.WaitForExit(5000) | Out-Null
        }
}

function Start-Expzip([string[]]$Arguments = @()) {
    $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = if ($quoted.Count -gt 0) {
        Start-Process $script:Exe -ArgumentList $quoted -PassThru
    }
    else {
        Start-Process $script:Exe -PassThru
    }

    $window = $null
    $deadline = [DateTime]::Now.AddSeconds(30)
    while ([DateTime]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $process.Refresh()
        if ($process.HasExited) { throw 'Expzip が起動直後に終わりました' }
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { continue }
        $window = $script:Automation::FromHandle($process.MainWindowHandle)
        if ($null -ne (ById $window 'AboutButton')) { break }
    }
    if ($null -eq $window) { throw 'Expzip の窓が出ませんでした' }

    $app = [pscustomobject]@{ Process = $process; Window = $window }
    Focus-App $app
    Wait-Idle $app
    return $app
}

function Stop-Expzip($App, [switch]$Force) {
    if ($null -eq $App) { return }
    if (-not $App.Process.HasExited) {
        if (-not $Force) { $App.Process.CloseMainWindow() | Out-Null }
        if (-not $App.Process.WaitForExit(8000)) { $App.Process.Kill() }
    }
    Start-Sleep -Milliseconds 400
}

# Expzip を前に出す。**前に出たことを確かめる** (#169)。
# テストは隠れた PowerShell から走らせているので、Windows は SetForegroundWindow を断ることがある
# (前にいないプロセスがほかのウィンドウを前に出すのを防ぐ決まり)。断られても何も言わないので、
# 以前はそのままキーを送り、別のウィンドウに届いて後の確認がまとめて落ちていた。
# Alt を 1 回押して離すと断られなくなる、よく知られた手を使う
function Focus-App($App) {
    $handle = $App.Process.MainWindowHandle
    [ExpzipUi.Native]::ShowWindow($handle, 9) | Out-Null

    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        if ($attempt -gt 0) {
            [ExpzipUi.Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
            [ExpzipUi.Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        }
        [ExpzipUi.Native]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 300
        if (Test-AppInFront $App) {
            # 送り直して前に出たときは残しておく。断られていたことの手掛かりになる
            if ($attempt -gt 0) { Write-Host ("        (Expzip を前に出すのに {0} 回かかりました)" -f ($attempt + 1)) }
            return
        }
    }

    throw ("Expzip を前に出せませんでした (前にあるのは「{0}」)" -f (Get-ForegroundTitle))
}

# 前にあるのが Expzip のウィンドウか。Expzip が出したダイアログ (パスワードの窓など) も含める
function Test-AppInFront($App) {
    $processId = 0
    [ExpzipUi.Native]::GetWindowThreadProcessId([ExpzipUi.Native]::GetForegroundWindow(), [ref]$processId) | Out-Null
    return $processId -eq $App.Process.Id
}

# 処理中の印 (中止ボタン) が消えるまで待つ
function Wait-Idle($App, [int]$TimeoutMs = 30000) {
    Wait-Until -TimeoutMs $TimeoutMs {
        $cancel = ById $App.Window 'CancelButton'
        ($null -eq $cancel) -or $cancel.Current.IsOffscreen
    } | Out-Null
    Start-Sleep -Milliseconds 300
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMs = 10000) {
    $deadline = [DateTime]::Now.AddMilliseconds($TimeoutMs)
    do {
        try {
            $result = & $Condition
            if ($result) { return $result }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::Now -lt $deadline)
    return $null
}

# ------------------------------------------------------------------ 要素を探す

function Find-One($Scope, $Condition) {
    if ($null -eq $Scope) { return $null }
    return $Scope.FindFirst($script:Scope::Descendants, $Condition)
}

function Find-All($Scope, $Condition) {
    if ($null -eq $Scope) { return @() }
    return @($Scope.FindAll($script:Scope::Descendants, $Condition))
}

function Condition($Property, $Value) {
    return New-Object System.Windows.Automation.PropertyCondition($Property, $Value)
}

function ById($Scope, [string]$Id) {
    return Find-One $Scope (Condition $script:Automation::AutomationIdProperty $Id)
}

function ByName($Scope, [string]$Name) {
    return Find-One $Scope (Condition $script:Automation::NameProperty $Name)
}

function ByType($Scope, $Type) {
    return Find-All $Scope (Condition $script:Automation::ControlTypeProperty $Type)
}

function Texts($Scope) {
    return ((Find-All $Scope ([System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | ')
}

function ValueOf($Element) {
    if ($null -eq $Element) { return '' }
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        return $pattern.Current.Value
    }
    return $Element.Current.Name
}

function IsReadOnly($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        return $pattern.Current.IsReadOnly
    }
    return $true
}

function Set-Text($Element, [string]$Text) {
    $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Start-Sleep -Milliseconds 200
}

function Push($Element, [int]$WaitMs = 700) {
    if ($null -eq $Element) { throw '押す相手が見つかりません' }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds $WaitMs
}

function Toggle-Check($Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 300
}

function IsChecked($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        return $pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    }
    return $false
}

# 一覧から選んでいる項目の名前 (ComboBox など)
function Get-Selected($Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $chosen = @($pattern.Current.GetSelection())
    if ($chosen.Count -eq 0) { return '' }
    return $chosen[0].Current.Name
}

function Select-Element($Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
}

function Expand-Element($Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 400
}

# ------------------------------------------------------------------ 窓

function Get-AppWindows($App) {
    $condition = New-Object System.Windows.Automation.AndCondition(
        (Condition $script:Automation::ProcessIdProperty $App.Process.Id),
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::Window))
    return @($script:Automation::RootElement.FindAll($script:Scope::Descendants, $condition))
}

# 名前で窓を探す。所有された窓は主窓の子として並ばないことがある
function Find-Window($App, [string]$Name, [int]$TimeoutMs = 8000) {
    return Wait-Until -TimeoutMs $TimeoutMs {
        Get-AppWindows $App | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1
    }
}

function Test-WindowGone($App, [string]$Name) {
    return [bool](Wait-Until -TimeoutMs 5000 {
        -not (Get-AppWindows $App | Where-Object { $_.Current.Name -eq $Name })
    })
}

# MessageBox。本文と押せる口を返す
function Find-MessageBox($App, [int]$TimeoutMs = 10000) {
    $box = Wait-Until -TimeoutMs $TimeoutMs {
        Get-AppWindows $App | Where-Object { $_.Current.ClassName -eq '#32770' } | Select-Object -First 1
    }
    if ($null -eq $box) { return $null }

    # 中の部品は Pane として見えることがあるので、種類ではなくクラス名で分ける。
    # 本文は ID 65535 の Static
    $parts = Find-All $box ([System.Windows.Automation.Condition]::TrueCondition)
    $text = (@($parts | Where-Object { $_.Current.ClassName -eq 'Static' -and $_.Current.AutomationId -eq '65535' } |
        ForEach-Object { $_.Current.Name }) -join "`n").Replace("`r`n", "`n")
    $buttons = @($parts | Where-Object { $_.Current.ClassName -eq 'Button' })
    return [pscustomobject]@{
        Element = $box
        Text    = $text
        Buttons = @($buttons | ForEach-Object { $_.Current.Name })
        Handles = $buttons
    }
}

# 口を押す。UI Automation の Invoke は閉じ終わるまで戻らず固まることがあるので、
# BM_CLICK を置いて戻る
function Close-MessageBox($Box, [string]$Button) {
    $target = $Box.Handles | Where-Object { $_.Current.Name -eq $Button } | Select-Object -First 1
    if ($null -eq $target) { throw "MessageBox に「$Button」がありません: $($Box.Buttons -join ', ')" }
    [ExpzipUi.Native]::PostMessage([IntPtr]$target.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 900
}

# ツールバーの口から出る一覧を開く
function Open-DropDown($App, [string]$ButtonId) {
    # 窓が前面から外れると一覧はすぐ閉じる。開けなければ前に出し直してもう一度押す
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Focus-App $App
        Push (ById $App.Window $ButtonId) 900
        $menu = Find-DropDown $App
        if ($null -ne $menu) { return $menu }
    }
    return $null
}

function Find-DropDown($App) {
    # 閉じかけの前の一覧をつかまないよう、項目が並んでいるものを待つ
    return Wait-Until -TimeoutMs 3000 {
        $condition = New-Object System.Windows.Automation.AndCondition(
            (Condition $script:Automation::ProcessIdProperty $App.Process.Id),
            (Condition $script:Automation::ControlTypeProperty $script:ControlType::Menu))
        $script:Automation::RootElement.FindAll($script:Scope::Descendants, $condition) |
            Where-Object { (ByType $_ $script:ControlType::MenuItem).Count -gt 0 } |
            Select-Object -First 1
    }
}

function Close-DropDown($App) {
    Send-Keys $App '{ESC}'
}

function Send-Keys($App, [string]$Keys) {
    Focus-App $App
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds 600
}

# ------------------------------------------------------------------ 主窓の中身

function Get-Status($App) {
    return (ById $App.Window 'StatusMessage').Current.Name
}

function Get-Rows($App) {
    $list = ById $App.Window 'EntryList'
    return @($list.FindAll($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem)))
}

# 一覧の行の高さ (画面の px)。行の間隔はどの一覧もエクスプローラーに合わせる (#125、#131)。
# 表示倍率で値が変わるので、同じ画面にあるメインの一覧と比べる
function Get-RowHeight($List) {
    $row = $List.FindFirst($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem))
    if ($null -eq $row) { return 0 }
    return $row.Current.BoundingRectangle.Height
}

# 画面のその範囲に描かれた字の幅 (px)。いちばん多い色を地の色とみなし、
# それと違う色の点がある列を、左端から続いているところまで測る。
# 字の高さより広く空いたら、そこから先は字ではない (隣の区切り線など) とみなす
function Measure-InkWidth($Rect) {
    $width = [int][Math]::Floor($Rect.Width)
    $height = [int][Math]::Floor($Rect.Height)
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int][Math]::Ceiling($Rect.X), [int][Math]::Ceiling($Rect.Y), 0, 0, $bitmap.Size)
        $counts = @{}
        for ($x = 0; $x -lt $width; $x += 2) {
            for ($y = 0; $y -lt $height; $y += 2) {
                $argb = $bitmap.GetPixel($x, $y).ToArgb()
                $counts[$argb] = 1 + [int]$counts[$argb]
            }
        }
        $back = [System.Drawing.Color]::FromArgb(($counts.GetEnumerator() |
            Sort-Object Value -Descending | Select-Object -First 1).Key)
        $first = -1
        $last = -1
        for ($x = 0; $x -lt $width; $x++) {
            if ($first -ge 0 -and $x - $last -gt $height) { break }
            for ($y = 0; $y -lt $height; $y++) {
                $c = $bitmap.GetPixel($x, $y)
                if (([Math]::Abs($c.R - $back.R) + [Math]::Abs($c.G - $back.G) + [Math]::Abs($c.B - $back.B)) -gt 90) {
                    if ($first -lt 0) { $first = $x }
                    $last = $x
                    break
                }
            }
        }
        if ($first -lt 0) { return 0 }
        return $last - $first + 1
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

# その字を描いたときの幅。大きさは問わない (見出しどうしの比にしか使わない)
function Measure-TextShape([string]$Text) {
    Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
    $formatted = New-Object System.Windows.Media.FormattedText($Text,
        [System.Globalization.CultureInfo]::GetCultureInfo('ja-JP'),
        [System.Windows.FlowDirection]::LeftToRight,
        (New-Object System.Windows.Media.Typeface('Segoe UI')), 100.0,
        [System.Windows.Media.Brushes]::Black, 1.0)
    return $formatted.BuildGeometry((New-Object System.Windows.Point(0, 0))).Bounds.Width
}

# 字が列に収まっていない見出し (#143)。文言を長くしたときに、列の幅を見直し忘れると切れる。
# 見出しの字の外枠は、字の幅ではなく字に割り当てた場所の幅で報告されるので、枠どうしを
# 比べても切れたことは分からない。しかも収まらない字は次の行へ折り返されて見えなくなるので、
# 右端にかかる線を探しても見つからない。
# そこで、画面に描かれた字の幅と、同じ字を描いたときの幅の比を見出しごとに出す。
# 収まっている見出しはどれもほぼ同じ比になり、切れた見出しは欠けたぶん比が小さくなる。
# 見出しどうしで比べるので、字の大きさや表示倍率を知らなくてよい。
# 基準は真ん中の比。字の形によって 1 割ほどはずれるので、それより欠けたものを切れたとみなす。
# 画面に描かれたものを見るので、一覧の上にほかのウィンドウが重なっていない時に呼ぶ
function Get-ClippedHeaders($List) {
    $headers = @($List.FindAll($script:Scope::Descendants,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::HeaderItem)))
    $measured = @()
    foreach ($header in $headers) {
        foreach ($text in @(ByType $header $script:ControlType::Text)) {
            $name = $text.Current.Name
            $rect = $text.Current.BoundingRectangle
            if (-not $name -or $rect.IsEmpty -or $rect.Width -lt 4 -or $rect.Height -lt 4) { continue }
            $ink = Measure-InkWidth $rect
            $shape = Measure-TextShape $name
            if ($ink -le 0 -or $shape -le 0) { continue }
            $measured += [pscustomobject]@{ Name = $name; Ratio = $ink / $shape; Ink = $ink }
        }
    }
    # 比べる相手が無いまま「切れていない」で通らないように
    if ($measured.Count -lt 2) { return @("見出しを測れない ($($measured.Count) 個)") }
    $sorted = @($measured | Sort-Object Ratio)
    $full = $sorted[[int][Math]::Floor($sorted.Count / 2)].Ratio
    return @($measured | Where-Object { $_.Ratio -lt $full * 0.85 } |
        ForEach-Object { "{0} (描かれた幅 {1} px、本来の {2:0}%)" -f $_.Name, $_.Ink, (100 * $_.Ratio / $full) })
}

# 行の名前。最初のセルの字を読む。前に並ぶ絵 (私用領域の記号) は飛ばす
function Get-RowName($Row) {
    $texts = @($Row.FindAll($script:Scope::Descendants,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::Text)) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_ -notmatch '^[-]+$' })
    if ($texts.Count -gt 0) { return $texts[0] }
    return $Row.Current.Name
}

function Get-RowNames($App) {
    return @(Get-Rows $App | ForEach-Object { Get-RowName $_ })
}

function Find-Row($App, [string]$Name) {
    return Get-Rows $App | Where-Object { (Get-RowName $_) -eq $Name } | Select-Object -First 1
}

# 見えている印 (錠前 LockMark #128、警告 WarningMark #141)。絵は支援技術の木に出さないので、
# 省かれたものまで含む木 (raw view) で探す
function Get-Marks($Scope, [string]$Id) {
    $request = New-Object System.Windows.Automation.CacheRequest
    $request.TreeFilter = [System.Windows.Automation.Automation]::RawViewCondition
    $request.Add($script:Automation::AutomationIdProperty)
    $active = $request.Activate()
    try {
        return @($Scope.FindAll($script:Scope::Descendants,
            (Condition $script:Automation::AutomationIdProperty $Id)) |
            Where-Object { -not $_.Current.IsOffscreen -and -not $_.Current.BoundingRectangle.IsEmpty })
    } finally {
        $active.Dispose()
    }
}

# 行の左端から名前の字までの距離。窓の位置は起動のたびに変わるので、行から測る
function Get-NameLeft($App, [string]$Name) {
    $row = Find-Row $App $Name
    return (ByName $row $Name).Current.BoundingRectangle.X - $row.Current.BoundingRectangle.X
}

function Select-Row($App, [string]$Name) {
    $row = Find-Row $App $Name
    if ($null -eq $row) { throw "行がありません: $Name / $((Get-RowNames $App) -join ', ')" }
    Select-Element $row
    $row.SetFocus()
    Start-Sleep -Milliseconds 300
    return $row
}

# 行の名前を書き換えて確定する。入力欄に値を入れてから Enter を送る
function Rename-Row($App, [string]$From, [string]$To) {
    # 起動直後は一覧がキーを受け取れないことがあるので、入力欄が開くまで押し直す
    $box = $null
    for ($attempt = 0; $attempt -lt 3 -and $null -eq $box; $attempt++) {
        Select-Row $App $From | Out-Null
        Send-Keys $App '{F2}'
        $box = Wait-Until -TimeoutMs 3000 { ByType (ById $App.Window 'EntryList') $script:ControlType::Edit | Select-Object -First 1 }
    }
    if ($null -eq $box) { return $false }
    Set-Text $box $To
    $box.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
    return $true
}

# エクスプローラーからドロップするのと同じ形で、ファイルやフォルダーを画面のその位置へ落とす。
# 小さな窓を最前面に出して、そこからマウスでドラッグを始め、落とす位置まで動かして離す。
# 落とした先が受け取ったかを返す。座標は UI Automation と同じ、画面の実際の px
# マウスでつかんで運ぶ仕掛けを用意する。落とす (Invoke-Drop) と持ち出す (Invoke-MouseDrag) で使う
function Initialize-Dropper {
    if (-not ('ExpzipUi.Dropper' -as [type])) {
        Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ExpzipUi
{
    public static class Dropper
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        const uint Move = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, Absolute = 0x8000;

        // UI Automation の座標は画面の実際の px。表示倍率が 100% でないと、
        // DPI を気にしないスレッドのマウスの座標とずれるので、スレッドごとに合わせる
        static void UsePhysicalPixels()
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4));
        }

        // 絶対座標の移動として送る。SetCursorPos だけではドラッグ中の窓に動きが伝わらない
        static void MoveTo(int x, int y)
        {
            SetCursorPos(x, y);
            mouse_event(Move | Absolute, x * 65536 / GetSystemMetrics(0), y * 65536 / GetSystemMetrics(1), 0, UIntPtr.Zero);
        }

        // 落とした先が受け取ったか。受け取らなかった場合は None
        public static DragDropEffects Result;

        public static bool Drop(string[] paths, int x, int y, int timeoutMs)
        {
            Result = DragDropEffects.None;
            var thread = new Thread(() =>
            {
                UsePhysicalPixels();
                // 縁の無い窓でも幅は 200 px ほどより狭くならないので、落とす位置に掛からないよう十分左に置く
                var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    TopMost = true,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(x - 420, y - 20),
                    Size = new Size(40, 40),
                    BackColor = Color.Orange,
                };
                form.MouseDown += (s, e) =>
                {
                    Result = form.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
                    form.Close();
                };
                form.Shown += (s, e) =>
                {
                    // 最前面の指定だけでは、直前まで前にいた窓の後ろに回ることがある
                    form.Activate();
                    form.BringToFront();
                };
                form.Shown += (s, e) => new Thread(() =>
                {
                    UsePhysicalPixels();
                    Thread.Sleep(300);
                    var startX = x - 400;
                    MoveTo(startX, y);
                    Thread.Sleep(200);
                    mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(300);
                    for (var i = 1; i <= 12; i++)
                    {
                        MoveTo(startX + (x - startX) * i / 12, y);
                        Thread.Sleep(50);
                    }
                    Thread.Sleep(300);
                    mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                }) { IsBackground = true }.Start();
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return thread.Join(timeoutMs);
        }

        // ある位置から別の位置へ、マウスでつかんで運ぶ (#157)。Expzip から外へ持ち出すのに使う。
        // 運ぶ途中を細かく刻むのは、ドラッグの始まりと、落とす先の上に来たことを相手に伝えるため
        public static void Drag(int fromX, int fromY, int toX, int toY)
        {
            var thread = new Thread(() =>
            {
                UsePhysicalPixels();
                MoveTo(fromX, fromY);
                Thread.Sleep(300);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(300);
                for (var i = 1; i <= 60; i++)
                {
                    MoveTo(fromX + (toX - fromX) * i / 60, fromY + (toY - fromY) * i / 60);
                    Thread.Sleep(30);
                }
                // 相手の上で少し動かす。着いてすぐ離すと、相手が「上に来た」と受け取る前に落ちる
                for (var i = 0; i < 10; i++)
                {
                    MoveTo(toX + (i % 2 == 0 ? 4 : -4), toY);
                    Thread.Sleep(100);
                }
                MoveTo(toX, toY);
                Thread.Sleep(800);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            });
            thread.Start();
            thread.Join();
        }
    }
}
'@
    }
}

function Invoke-Drop([string[]]$Paths, [int]$X, [int]$Y) {
    Initialize-Dropper
    $done = [ExpzipUi.Dropper]::Drop($Paths, $X, $Y, 15000)
    Start-Sleep -Milliseconds 500
    return $done -and [ExpzipUi.Dropper]::Result -ne [System.Windows.Forms.DragDropEffects]::None
}

# 画面のある位置から別の位置へ、マウスでつかんで運ぶ (#157)
function Invoke-MouseDrag([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY) {
    Initialize-Dropper
    [ExpzipUi.Dropper]::Drag($FromX, $FromY, $ToX, $ToY)
    Start-Sleep -Milliseconds 800
}

# 主窓の真ん中。書庫を開いていなくても受け取る場所。
# 落とす前に Expzip を前に出す (#169)。ほかのウィンドウが上に重なっていると、そちらに落ちる
function Get-WindowCenter($App) {
    Focus-App $App
    $rect = $App.Window.Current.BoundingRectangle
    return [pscustomobject]@{ X = [int]($rect.X + $rect.Width / 2); Y = [int]($rect.Y + $rect.Height / 2) }
}

# ------------------------------------------------------------------ Windows の窓

# 保存する窓の名前の欄に入っている値
function Get-FileDialogName($App, [string]$Title) {
    $dialog = Find-Window $App $Title 15000
    if ($null -eq $dialog) { return $null }
    $edit = Find-All $dialog ([System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ClassName -eq 'Edit' -and $_.Current.AutomationId -eq '1001' } |
        Select-Object -First 1
    return ValueOf $edit
}

# ファイルやフォルダーを選ぶ窓にパスを入れて確定する。
# 名前の欄 (保存は 1001、フォルダーは 1152) に書き込み、確定の口 (1) を押す
function Complete-FileDialog($App, [string]$Title, [string]$Path) {
    $dialog = Find-Window $App $Title 15000
    if ($null -eq $dialog) { return $false }
    $parts = Find-All $dialog ([System.Windows.Automation.Condition]::TrueCondition)
    $edit = $parts | Where-Object { $_.Current.ClassName -eq 'Edit' -and $_.Current.AutomationId -in '1001', '1152' } |
        Select-Object -First 1
    $ok = $parts | Where-Object { $_.Current.ClassName -eq 'Button' -and $_.Current.AutomationId -eq '1' } |
        Select-Object -First 1
    if ($null -eq $edit -or $null -eq $ok) { return $false }
    [ExpzipUi.Native]::SendMessage([IntPtr]$edit.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $Path) | Out-Null
    Start-Sleep -Milliseconds 300
    [ExpzipUi.Native]::PostMessage([IntPtr]$ok.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1200
    return $true
}

# 受け取った側で動くツール (自己解凍書庫、結合用のプログラム) が出す知らせを読み、OK で閉じる
function Read-NativeMessage($Process, [int]$TimeoutMs = 30000) {
    $box = Wait-Until -TimeoutMs $TimeoutMs {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { $script:Automation::FromHandle($Process.MainWindowHandle) }
    }
    if ($null -eq $box) { return $null }
    $parts = Find-All $box ([System.Windows.Automation.Condition]::TrueCondition)
    $text = (@($parts | Where-Object { $_.Current.ClassName -eq 'Static' -and $_.Current.Name } |
        ForEach-Object { $_.Current.Name }) -join "`n").Replace("`r`n", "`n")
    $result = [pscustomobject]@{ Title = $box.Current.Name; Text = $text }
    $ok = $parts | Where-Object { $_.Current.ClassName -eq 'Button' } | Select-Object -First 1
    if ($ok) {
        [ExpzipUi.Native]::PostMessage([IntPtr]$ok.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }
    return $result
}

function Save-Shot($App, [string]$Path) {
    $rect = $App.Window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
}
