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

Section '問題の無い書庫'
$app = Start-Expzip @($clean)
$window = Run-Inspection $app '検査結果 - kirei.zip'
Check '窓が開く' ($null -ne $window)
if ($window) {
    Check '見出し' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Check '閉じる口' ((ById $window 'CloseButton').Current.Name -eq '閉じる')
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '怪しい書庫'
$app = Start-Expzip @($risky)
$warning = ById $app.Window 'SuspiciousWarningText'
Check '開いた時点で知らせる' ($warning -and $warning.Current.Name -match '^パスが通常と異なる項目が \d+ 個あります$') $(if ($warning) { $warning.Current.Name })

$window = Run-Inspection $app '検査結果 - ayashii.zip'
Check '窓が開く' ($null -ne $window)
if ($window) {
    $headline = (ById $window 'Headline').Current.Name
    Check '見出しに件数' ($headline -match '^(危険 \d+ 件、注意 \d+ 件が見つかりました|危険な項目が \d+ 件見つかりました)$') $headline
    $findings = Texts (ById $window 'FindingList')
    Check '外へ書き込むパス' ($findings -match '展開先の外に書き込もうとするパスです \(\.\./evil\.txt\)') $findings
    Check '実行される種類' ($findings -match 'setup\.exe \| 開くとプログラムとして実行される種類のファイルです \(\.exe\)')
    Check '予約された名前' ($findings -match 'Windows が予約している名前のため、このファイルは作成できません \(CON\.txt\)')
    Check '大文字と小文字だけ違う' ($findings -match '大文字と小文字だけが異なる項目があります \([Rr]eport\.txt\)。展開すると片方が失われます')
    # 画面の文字に英語の例外文や内部の名前が混ざっていない
    Check '内部の名前が出ていない' ($findings -notmatch 'EscapingPath|ExecutableExtension|ReservedName|CaseCollision')
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Complete-Suite
