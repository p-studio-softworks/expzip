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

Complete-Suite
