# バージョン情報の窓とライセンス表示 (#104, #105)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'About'
Set-Settings

Section '日本語'
$app = Start-Expzip
$button = ById $app.Window 'AboutButton'
Check '口の名前' ($button.Current.Name -eq 'バージョン情報') $button.Current.Name
Check '書庫を開いていなくても押せる' $button.Current.IsEnabled
Check '説明がある' ($button.Current.HelpText.Length -gt 0) $button.Current.HelpText

Push $button
$dialog = Find-Window $app 'バージョン情報'
Check '窓が開く' ($null -ne $dialog)

if ($dialog) {
    $text = Texts $dialog
    Check 'アプリの名前' ((ById $dialog 'NameText').Current.Name -eq 'Expzip')
    # 版は csproj の <Version>。ビルド番号 (0.1.0.0) ではない
    Check 'バージョンと作り' ($text -match 'バージョン \d+\.\d+\.\d+ \((x64|x86|arm64)\)') (ById $dialog 'VersionText').Current.Name
    Check 'リビジョン' ((ById $dialog 'RevisionText').Current.Name -match '^リビジョン [0-9a-f]{7}') (ById $dialog 'RevisionText').Current.Name
    Check 'コピーライト' ((ById $dialog 'CopyrightText').Current.Name -match 'Copyright')
    Check 'アイコンの場所が空けてある' ((ById $dialog 'IconPlaceholder').Current.Name.Length -gt 0) (ById $dialog 'IconPlaceholder').Current.Name
    Check '土台' ($text -match 'Windows' -and $text -match '\.NET')

    Section 'ライセンス表示'
    # MIT は著作権表示と許諾条文を複製物に含めることを条件にしている
    $license = ById $dialog 'LicenseButton'
    Check 'ライセンスの口' ($license.Current.Name -eq 'ライセンス') $license.Current.Name
    Push $license
    $notices = Find-Window $app 'ライセンス表示'
    Check 'ライセンスの窓が開く' ($null -ne $notices)

    if ($notices) {
        $box = ById $notices 'NoticeText'
        $body = ValueOf $box
        Check '本文が入っている' ($body.Length -gt 20000) "$($body.Length) 文字"
        Check 'SharpCompress' ($body -match 'SharpCompress 0\.50\.4' -and $body -match 'Copyright \(c\) 2014  Adam Hathcock')
        Check 'SharpZipLib' ($body -match 'SharpZipLib 1\.4\.2' -and $body -match '2000-2018 SharpZipLib Contributors')
        Check '.NET ランタイム' ($body -match '\.NET Foundation and Contributors')
        Check 'ランタイムの中の第三者' ($body -match 'zlib-ng' -and $body -match 'Brotli' -and $body -match 'mimalloc')
        # 名前と版だけでは含めたことにならない。条文そのものが要る
        Check '許諾条文そのもの' ($body -match 'The above copyright notice and this permission notice')
        Check '書き換えられない' (IsReadOnly $box)
        Push (ById $notices 'CloseButton')
        Check 'ライセンスの窓を閉じられる' (Test-WindowGone $app 'ライセンス表示')
    }

    Push (ById $dialog 'CloseButton')
    Check '閉じられる' (Test-WindowGone $app 'バージョン情報')
}

Section '英語'
$menu = Open-DropDown $app 'LanguageButton'
Push (ByName $menu 'English') 1200
$button = ById $app.Window 'AboutButton'
Check '口の名前が英語' ($button.Current.Name -eq 'About') $button.Current.Name
Push $button
$dialog = Find-Window $app 'About'
Check '窓の見出しが英語' ($null -ne $dialog)
if ($dialog) {
    $text = Texts $dialog
    Check '中身も英語' ($text -match 'Version \d+\.\d+\.\d+' -and $text -match 'Revision') $text
    Check '日本語が残っていない' ($text -notmatch '[ぁ-んァ-ン]') ''
    Check 'ライセンスの口も英語' ((ById $dialog 'LicenseButton').Current.Name -eq 'Licenses')
    Check '閉じる口も英語' ((ById $dialog 'CloseButton').Current.Name -eq 'Close')
    Push (ById $dialog 'CloseButton')
}

Stop-Expzip $app
Complete-Suite
