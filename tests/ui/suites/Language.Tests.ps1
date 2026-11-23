# 表示言語の切り替え (#23, #104)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Language'
$sample = New-TestZip (Join-Path $script:Work 'lang.zip') ([ordered]@{
    'docs/' = ''; 'docs/a.txt' = 'a'; 'docs/b.txt' = 'b'; 'readme.txt' = 'r'
})

function Switch-Language($App, [string]$Item) {
    $menu = Open-DropDown $App 'LanguageButton'
    $choice = ByName $menu $Item
    if ($null -eq $choice) { Close-DropDown $App; return $false }
    Push $choice 1200
    return $true
}

Section '設定に日本語が入っているとき'
Set-Settings @{ Language = 'ja' }
$app = Start-Expzip @($sample)
$names = Texts $app.Window
Check 'ツールバーが日本語' ((ById $app.Window 'ExtractButton').Current.Name -eq '展開')
Check '列見出しが日本語' ($names -match '名前' -and $names -match '圧縮後' -and $names -match '圧縮率')
Check '件数が日本語' ($names -match '個のファイル')

Section '英語に切り替える'
Check '言語の一覧から English を選べる' (Switch-Language $app 'English')
$names = Texts $app.Window
Check 'ツールバーが英語' ((ById $app.Window 'ExtractButton').Current.Name -eq 'Extract')
Check '列見出しが英語' ($names -match 'Packed' -and $names -match 'Ratio' -and $names -notmatch '圧縮率')
Check '件数が英語' ($names -match '\d files?')
Check '日本語が残っていない' ($names -notmatch '[ぁ-んァ-ン]') (($names -split ' \| ' | Where-Object { $_ -match '[ぁ-んァ-ン]' }) -join ', ')

Section '次の起動でも英語'
Stop-Expzip $app
Check '設定ファイルに en が残る' ((Read-Settings) -match '"Language"\s*:\s*"en"')
$app = Start-Expzip @($sample)
Check '英語で起動する' ((ById $app.Window 'ExtractButton').Current.Name -eq 'Extract')

Section '日本語に戻す'
Check 'Language の一覧から日本語を選べる' (Switch-Language $app '日本語')
Check '日本語に戻る' ((ById $app.Window 'ExtractButton').Current.Name -eq '展開')
Stop-Expzip $app
Check '設定ファイルに ja が残る' ((Read-Settings) -match '"Language"\s*:\s*"ja"')

Section 'Windows に合わせる'
$app = Start-Expzip @($sample)
Check '選べる' (Switch-Language $app 'Windows の表示言語に合わせる')
Stop-Expzip $app
$json = Read-Settings
Check '設定から言語の指定が消える' ($json -notmatch '"Language"\s*:\s*"(ja|en)"') $json

# 設定は、画面の組み立てと並べて裏で読む (#398)。読み終わる前に画面へ文字を貼らないこと、
# 読めなくても既定の値で起動すること (#399 からは知らせも出す) を確かめる。既定の言語は Windows の表示言語に合わせるので、
# どちらの言語かは決めつけない
Section '設定ファイルに en と書いてから起動する'
Set-Settings @{ Language = 'en' }
$app = Start-Expzip @($sample)
Check '最初の画面から英語' ((ById $app.Window 'ExtractButton').Current.Name -eq 'Extract')
Check '列見出しも英語' ((Texts $app.Window) -match 'Packed')
Stop-Expzip $app

# 読めなかったときは、知らせを出し、そのセッションでは保存しない (#399)。
# 書庫を渡して起動し、最近開いた書庫を覚える保存を起こしても、元のファイルが既定の値で置き換わらないことを確かめる
function Start-ExpzipAfterWarning([string[]]$Arguments) {
    $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = if ($quoted.Count -gt 0) { Start-Process $script:Exe -ArgumentList $quoted -PassThru }
    else { Start-Process $script:Exe -PassThru }
    $box = Find-MessageBox ([pscustomobject]@{ Process = $process }) 30000

    # 警告が出ている間に、本体が見えていること (#406)。本体の枠を作る途中 (Loaded) で出していたときは、
    # 本体が隠れたままで、警告にタスクバーのボタンが無く、ほかのウィンドウの後ろに回ると見つけられなかった。
    # MainWindowHandle は、見えていて持ち主の無いウィンドウを指す。本体が隠れていると 0 になる
    # 渡した書庫は、警告を閉じてから開く。警告の間はタブが無いこと
    $mainShown = $false
    $tabsDuringWarning = $null
    if ($null -ne $box) {
        $process.Refresh()
        $mainShown = $process.MainWindowHandle -ne [IntPtr]::Zero
        if ($mainShown) {
            $tabsDuringWarning = @(Get-Tabs ([pscustomobject]@{ Window = $script:Automation::FromHandle($process.MainWindowHandle) })).Count
        }
        Close-MessageBox $box 'OK'
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

    $app = [pscustomobject]@{ Process = $process; Window = $window }
    Wait-Idle $app
    return [pscustomobject]@{ App = $app; Box = $box; MainShown = $mainShown; TabsDuringWarning = $tabsDuringWarning }
}

Section '設定ファイルが壊れている'
Remove-SettingsCopies
$broken = '{ "Language": "en", '
[System.IO.File]::WriteAllText($script:SettingsPath, $broken, (New-Object System.Text.UTF8Encoding($false)))
$started = Start-ExpzipAfterWarning @($sample)
$app = $started.App
$copies = Get-SettingsCopies
Check '読めなかったことを知らせる' ($null -ne $started.Box -and $started.Box.Text.Contains($script:SettingsPath)) $started.Box.Text
Check '写しの場所を知らせる' ($copies.Count -eq 1 -and $started.Box.Text.Contains($copies[0].FullName)) $started.Box.Text
Check '知らせている間も本体が見える' $started.MainShown
Check '既定の値で起動する' ((ById $app.Window 'ExtractButton').Current.Name -in @('展開', 'Extract'))
# 知らせのあとに、渡した書庫を開く
Check '知らせている間は書庫をまだ開かない' ($started.TabsDuringWarning -eq 0) "$($started.TabsDuringWarning) 個"
Check '渡した書庫を開く' ((Get-Tabs $app).Count -eq 1) "$((Get-Tabs $app).Count) 個"
Stop-Expzip $app
Check '元のファイルは置き換わらない' ((Read-Settings) -eq $broken) (Read-Settings)
Check '写しは元の中身' ($copies.Count -eq 1 -and [System.IO.File]::ReadAllText($copies[0].FullName) -eq $broken)

Section '壊れたままもう一度起動する'
$started = Start-ExpzipAfterWarning @()
Stop-Expzip $started.App
Check '同じ中身の写しを増やさない' ((Get-SettingsCopies).Count -eq 1) ((Get-SettingsCopies | ForEach-Object Name) -join ', ')

Section '設定ファイルを読めない'
Remove-SettingsCopies
Set-Settings @{ Language = 'en' }
$before = Read-Settings
$lock = [System.IO.File]::Open($script:SettingsPath, 'Open', 'ReadWrite', 'None')
try {
    $started = Start-ExpzipAfterWarning @($sample)
    $app = $started.App
    Check '読めなかったことを知らせる' ($null -ne $started.Box -and $started.Box.Text.Contains($script:SettingsPath)) $started.Box.Text
    Check '中身は無事かもしれないので削除を勧めない' ($null -ne $started.Box -and $started.Box.Text -notmatch '削除|delete') $started.Box.Text
    Check '知らせている間も本体が見える' $started.MainShown
    Check '既定の値で起動する' ((ById $app.Window 'ExtractButton').Current.Name -in @('展開', 'Extract'))
}
finally {
    # 開いたままのうちに保存が試みられたあと、閉じてから終了させる。終了のときの保存でも置き換わらないこと
    $lock.Dispose()
}
Stop-Expzip $app
Check '元のファイルは置き換わらない' ((Read-Settings) -eq $before) (Read-Settings)
Check '中身を読めないときは写しを作らない' ((Get-SettingsCopies).Count -eq 0)

Section '設定ファイルが無い'
Remove-Item $script:SettingsPath -Force -ErrorAction SilentlyContinue
$app = Start-Expzip @()
Check '既定の値で起動する' ((ById $app.Window 'ExtractButton').Current.Name -in @('展開', 'Extract'))
Stop-Expzip $app

Complete-Suite
