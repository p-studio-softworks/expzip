# 書庫の検査 (#53, #36)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Inspect'
Set-Settings

$clean = New-TestZip (Join-Path $script:Work 'kirei.zip') ([ordered]@{
    'docs/'         = ''
    'docs/memo.txt' = 'memo'
    'readme.txt'    = 'readme'
})
# 大文字と小文字だけ違う名前を並べるので、区別する表に入れる
$entries = New-Object System.Collections.Specialized.OrderedDictionary ([StringComparer]::Ordinal)
$entries['../evil.txt'] = 'x'
$entries['setup.exe'] = 'MZ'
$entries['CON.txt'] = 'x'
$entries['Report.txt'] = 'a'
$entries['report.txt'] = 'b'
$risky = New-TestZip (Join-Path $script:Work 'ayashii.zip') $entries

function Run-Inspection($App, [string]$Title) {
    Push (ById $App.Window 'InspectButton')
    Wait-Idle $App 60000
    return Find-Window $App $Title 30000
}

# 支援技術が読む行の名前 (#109)。名前を入れていないと行の型の名前が読まれる
function FindingRowNames($Window) {
    $list = ById $Window 'FindingList'
    return @($list.FindAll($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem)) |
        ForEach-Object { $_.Current.Name })
}

Section '問題の無い書庫'
$app = Start-Expzip @($clean)
$window = Run-Inspection $app '検査結果 - kirei.zip'
Check '窓が開く' ($null -ne $window)
if ($window) {
    Check '見出し' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Check '閉じる口' ((ById $window 'CloseButton').Current.Name -eq '閉じる')
    $rows = FindingRowNames $window
    Check '行の名前' (($rows -join ' / ') -eq '問題なし、kirei.zip、問題は見つかりませんでした') ($rows -join ' / ')
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '怪しい書庫'
$app = Start-Expzip @($risky)
$warning = ById $app.Window 'SuspiciousWarningText'
Check '開いた時点で知らせる' ($warning -and $warning.Current.Name -match '^パスが通常と異なる項目が \d+ 個あります$') $(if ($warning) { $warning.Current.Name })
# 印の意味は一覧の行の名前にも入る (#119)。絵と色だけでは読み上げに伝わらない
$rowNames = @(Get-Rows $app | ForEach-Object { $_.Current.Name })
Check '印の意味が行の名前に入る' ($rowNames -match '^\.\.、パスが通常と異なります$') ($rowNames -join ' / ')
# 警告の絵はアイコンに重ねる (#141)。名前の前に並べると、その行だけ名前が右へずれ、
# 1 段下の階層にあるように見える
Check '通常と異なる行に警告が付く' ((Get-Marks (Find-Row $app '..') 'WarningMark').Count -eq 1)
Check '通常の行には警告が無い' ((Get-Marks (Find-Row $app 'setup.exe') 'WarningMark').Count -eq 0)
Check 'ツリーにも警告が付く' ((Get-Marks (ById $app.Window 'FolderTree') 'WarningMark').Count -ge 1)
$plainLeft = Get-NameLeft $app 'setup.exe'
$warnedLeft = Get-NameLeft $app '..'
Check '警告が付いても名前の位置は変わらない' ([Math]::Abs($warnedLeft - $plainLeft) -lt 1) "$plainLeft → $warnedLeft"

# 列の見出しの字が切れていない (#143)。画面に描かれたものを見るので、
# 検査結果のウィンドウが重なる前に見る
$clipped = Get-ClippedHeaders (ById $app.Window 'EntryList')
Check 'メインの一覧の見出しが切れていない' ($clipped.Count -eq 0) ($clipped -join ' / ')

$window = Run-Inspection $app '検査結果 - ayashii.zip'
Check '窓が開く' ($null -ne $window)
if ($window) {
    $headline = (ById $window 'Headline').Current.Name
    Check '見出しに件数' ($headline -match '^(危険 \d+ 件、注意 \d+ 件が見つかりました|危険な項目が \d+ 件見つかりました)$') $headline
    # 行の間隔はメインの一覧と同じ (#131)
    $mainHeight = Get-RowHeight (ById $app.Window 'EntryList')
    $findingHeight = Get-RowHeight (ById $window 'FindingList')
    Check '行の間隔がメインの一覧と同じ' ([Math]::Abs($findingHeight - $mainHeight) -lt 1) "メイン $mainHeight / 検査結果 $findingHeight"
    # 列の見出しの字が切れていない (#143)
    $clipped = Get-ClippedHeaders (ById $window 'FindingList')
    Check '見出しが切れていない' ($clipped.Count -eq 0) ($clipped -join ' / ')
    $findings = Texts (ById $window 'FindingList')
    Check '外へ書き込むパス' ($findings -match '展開先の外に書き込もうとするパスです \(\.\./evil\.txt\)') $findings
    Check '実行される種類' ($findings -match 'setup\.exe \| 開くとプログラムとして実行される種類のファイルです \(\.exe\)')
    Check '予約された名前' ($findings -match 'Windows が予約している名前のため、このファイルは作成できません \(CON\.txt\)')
    Check '大文字と小文字だけ違う' ($findings -match '大文字と小文字だけが異なる項目があります \([Rr]eport\.txt\)。展開すると片方が失われます')
    # 画面の文字に英語の例外文や内部の名前が混ざっていない
    Check '内部の名前が出ていない' ($findings -notmatch 'EscapingPath|ExecutableExtension|ReservedName|CaseCollision')
    # 行そのものの名前。重大度・対象・内容をこの順に読む (#109)
    $rows = FindingRowNames $window
    Check '行の名前が型の名前でない' (-not ($rows -match 'Expzip\.')) ($rows -join ' / ')
    Check '行の名前に重大度と対象と内容' (
        ($rows -match '^危険、\.\./evil\.txt、展開先の外に書き込もうとするパスです') -and
        ($rows -match '^注意、setup\.exe、開くとプログラムとして実行される種類のファイルです')) ($rows -join ' / ')
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '中断する'
# 書庫を丸ごと対策ソフトに渡す間は、途中で止められない。圧縮された実行ファイルが
# たくさん詰まっていると数秒かかる (120 個 / 142 MB で 4 秒)。格納で数個では速く、試しにならない。
# その間に中断を押してもすぐ止まること (#161)
$heavy = Join-Path $script:Work 'omoi.zip'
$zip = [System.IO.Compression.ZipFile]::Open($heavy, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem "$env:SystemRoot\System32\*.dll" | Sort-Object Length -Descending | Select-Object -Skip 60 -First 120 |
        ForEach-Object {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $_.FullName, $_.Name, [System.IO.Compression.CompressionLevel]::Fastest) | Out-Null
        }
}
finally { $zip.Dispose() }
$app = Start-Expzip @($heavy)
Push (ById $app.Window 'InspectButton') 800
$clock = [System.Diagnostics.Stopwatch]::StartNew()
Push (ById $app.Window 'CancelButton') 0
$box = Find-MessageBox $app
$clock.Stop()
Check '中断を知らせる' ($box -and $box.Text -eq '検査を中断しました。') $(if ($box) { $box.Text })
Check 'すぐ止まる' ($clock.ElapsedMilliseconds -lt 1500) "$($clock.ElapsedMilliseconds) ms"
if ($box) { Close-MessageBox $box 'OK' }
# 中断したかったのだから、途中までの結果は出さない
Check '結果の窓は出さない' (Test-WindowGone $app '検査結果 - omoi.zip')
Check 'ステータスバー' ((Get-Status $app) -eq '検査を中断しました') (Get-Status $app)
Stop-Expzip $app

Complete-Suite
