# ツールバー (#102, #104)。絵だけの口でも、名前と説明が残っていること
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Toolbar'
Set-Settings
$sample = New-TestZip (Join-Path $script:Work 'icon.zip') ([ordered]@{ 'a.txt' = 'a' })

$tools = [ordered]@{
    OpenButton     = @('開く', '書庫を開く')
    RecentButton   = @('最近開いた書庫', '最近開いた書庫')
    ExtractButton  = @('展開', '選択した項目を展開する。選択していなければ書庫全体を展開する')
    AddButton      = @('追加', 'ファイルを追加する')
    PasswordButton = @('パスワード', 'パスワードを設定・変更・削除')
    InspectButton  = @('検査', 'この書庫が壊れていないか、危険なものが入っていないかを調べる')
    SplitButton    = @('分割', 'ファイルを指定した大きさに分割する。結合用のプログラムも作成する')
    SfxButton      = @('自己解凍', '自己解凍書庫として書き出す')
    AiButton       = @('AI機能', 'AI機能の設定')
    LanguageButton = @('言語', '言語設定')
    AboutButton    = @('バージョン情報', 'Expzip について')
}
$needArchive = @('ExtractButton', 'AddButton', 'PasswordButton', 'InspectButton', 'SfxButton')

# 名前を入れ忘れると WPF は中身の字を拾うので、私用領域の記号が名前になる
function Test-NoGlyphNames($App, [string]$Label) {
    $names = @(ByType $App.Window $script:ControlType::Button | ForEach-Object { $_.Current.Name })
    $raw = @($names | Where-Object { $_ -match '[-]' })
    Check $Label ($raw.Count -eq 0) "口 $($names.Count) 個 / 記号 $($raw.Count) 個"
}

function MenuNames($Menu) {
    return @(ByType $Menu $script:ControlType::MenuItem | ForEach-Object { $_.Current.Name }) -join ' / '
}

Section '書庫を開いていないとき'
$app = Start-Expzip
foreach ($id in $tools.Keys) {
    $button = ById $app.Window $id
    Check "名前: $id" ($button.Current.Name -eq $tools[$id][0]) $button.Current.Name
    # 絵だけになったので、マウスを当てたときの説明が唯一の手掛かりになる
    Check "説明: $id" ($button.Current.HelpText -eq $tools[$id][1]) $button.Current.HelpText
}
foreach ($id in $needArchive) {
    Check "押せない: $id" (-not (ById $app.Window $id).Current.IsEnabled)
}
foreach ($id in 'OpenButton', 'SplitButton', 'AiButton', 'LanguageButton', 'AboutButton') {
    Check "押せる: $id" ((ById $app.Window $id).Current.IsEnabled)
}
Test-NoGlyphNames $app '記号が名前になっていない'

# 押せないボタンの絵は淡くする (#175)。ここで測り、書庫を開いたあとの濃さと比べる
$dim = [ordered]@{}
foreach ($id in 'ExtractButton', 'SfxButton') {
    $dim[$id] = Measure-Ink (ById $app.Window $id).Current.BoundingRectangle
}

# 絵にしたぶん横に縮む。畳まれて隠れていないこと
$overflow = ById $app.Window 'OverflowButton'
Check 'ツールバーがはみ出していない' (($null -eq $overflow) -or $overflow.Current.IsOffscreen)

Section 'AI機能の一覧'
$menu = Open-DropDown $app 'AiButton'
Check '一覧が開く' ($null -ne $menu)
if ($menu) {
    Check '並び' ((MenuNames $menu) -eq 'AI連携の設定… / 書庫のルールを推定… / ルールに合っているか検査…') (MenuNames $menu)
    $learn = ByName $menu '書庫のルールを推定…'
    Check '接続先が無ければ推定は押せない' (-not $learn.Current.IsEnabled)
    Check '押せない理由を出す' ($learn.Current.HelpText -eq '先に「AI連携の設定…」で接続先を設定してください。') $learn.Current.HelpText
    $audit = ByName $menu 'ルールに合っているか検査…'
    Check 'ルールが無ければ検査は押せない' (-not $audit.Current.IsEnabled)
    Check '検査の押せない理由' ($audit.Current.HelpText -match '^ルールが保存されていません。') $audit.Current.HelpText
    Close-DropDown $app
}

Section '言語の一覧'
$menu = Open-DropDown $app 'LanguageButton'
Check '一覧が開く' ($null -ne $menu)
if ($menu) {
    Check '並び' ((MenuNames $menu) -eq 'Windows の表示言語に合わせる / 日本語 / English') (MenuNames $menu)
    Close-DropDown $app
}
Stop-Expzip $app

Section '書庫を開いたとき'
$app = Start-Expzip @($sample)
foreach ($id in $needArchive) {
    Check "押せる: $id" ((ById $app.Window $id).Current.IsEnabled)
}
# 押せるようになったぶん濃くなる。同じ絵・同じ場所どうしで比べる (#175)
foreach ($id in $dim.Keys) {
    $lit = Measure-Ink (ById $app.Window $id).Current.BoundingRectangle
    Check "押せないときは絵が淡い: $id" ($dim[$id] -lt $lit * 0.8) "押せないとき $($dim[$id]) / 押せるとき $lit"
}
$close = ByName $app.Window 'このタブを閉じる (Ctrl+W)'
Check 'タブを閉じる口に名前がある' ($null -ne $close)
Test-NoGlyphNames $app '開いた後も記号が名前になっていない'
Save-Shot $app (Join-Path $script:Work 'toolbar.png')

Section '英語'
$menu = Open-DropDown $app 'LanguageButton'
Push (ByName $menu 'English') 1200
$english = [ordered]@{
    OpenButton = 'Open'; ExtractButton = 'Extract'; AddButton = 'Add'; PasswordButton = 'Password'
    InspectButton = 'Inspect'; SplitButton = 'Split'; SfxButton = 'Self-extract'
    AiButton = 'AI features'; LanguageButton = 'Language'; AboutButton = 'About'
}
foreach ($id in $english.Keys) {
    $button = ById $app.Window $id
    Check "英語の名前: $id" ($button.Current.Name -eq $english[$id]) $button.Current.Name
}
Check 'タブを閉じる口も英語' ($null -ne (ByName $app.Window 'Close this tab (Ctrl+W)'))
Test-NoGlyphNames $app '英語でも記号が名前になっていない'
$menu = Open-DropDown $app 'AiButton'
Check 'AI の一覧も英語' ($menu -and (MenuNames $menu) -match '^AI settings\.\.\. / ') $(if ($menu) { MenuNames $menu })
Close-DropDown $app

Stop-Expzip $app
Complete-Suite
