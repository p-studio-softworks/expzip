# 書庫の中を名前で探す (#215): 欄の場所とキー、絞り込み、結果の行からの操作
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Search'
Set-Settings

# macOS で作った書庫は「が」を「か」と濁点の 2 文字で持っていることがある (#203)
$decomposed = [string][char]0x304B + [char]0x3099 + 'っこう.txt'
$archive = New-TestZip (Join-Path $script:Work '探す.zip') ([ordered]@{
    'docs/'                     = ''
    'docs/Report.txt'           = 'report'
    'docs/2024/'                = ''
    'docs/2024/report-final.txt' = 'final'
    'img/'                      = ''
    'img/photo.jpg'             = 'jpg'
    'report/'                   = ''
    'report/a.txt'              = 'a'
    'readme.txt'                = 'readme'
    "メモ/$decomposed"          = 'school'
})

function Crumbs($App) {
    return @(ByType (ById $App.Window 'Crumbs') $script:ControlType::Button |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -notmatch ' の中$' }) -join ' > '
}
function Rows($App) { return ((Get-RowNames $App) -join ', ') }
# 見つかったものの顔ぶれ。並び順は名前の比べ方で変わるので、ここでは見ない
function Found($App) { return ((Get-RowNames $App | Sort-Object) -join ', ') }
function Expected([string[]]$Names) { return (($Names | Sort-Object) -join ', ') }
function Focused { return [System.Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId }
function Headers($App) {
    return @(ByType (ById $App.Window 'EntryList') $script:ControlType::HeaderItem |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ })
}
# 行の 2 つ目の字。検索の結果では「場所」の列
function Place($App, [string]$Name) {
    $texts = @(ByType (Find-Row $App $Name) $script:ControlType::Text | ForEach-Object { $_.Current.Name })
    return $texts[1]
}
function Type-Search($App, [string]$Keys) {
    Send-Keys $App '^f'
    Send-Keys $App $Keys
}

$app = Start-Expzip @($archive)
$box = ById $app.Window 'SearchBox'

Section '欄'
Check '欄がある' ($null -ne $box)
Check '空のときの案内は書庫の名前' ($box.Current.Name -eq '探す.zip の検索') $box.Current.Name
Check '案内が見える' ((Texts $app.Window) -match '探す\.zip の検索')
$address = (ById $app.Window 'AddressBar').Current.BoundingRectangle
$rect = $box.Current.BoundingRectangle
Check 'アドレスバーの右' ($rect.Left -gt $address.Right -and [Math]::Abs($rect.Top - $address.Top) -lt 12) "欄 $rect / アドレスバー $address"
Check 'ふだんは場所の列が無い' (-not ((Headers $app) -contains '場所')) ((Headers $app) -join ', ')

Section 'キー'
foreach ($key in '^f', '^e', '{F3}') {
    Select-Row $app 'readme.txt' | Out-Null
    Send-Keys $app $key
    Check "$key で欄へ移る" ((Focused) -eq 'SearchBox') (Focused)
}

Section '1 文字ごとに絞る'
Type-Search $app 'r'
Check '1 文字で絞る' ((Found $app) -eq (Expected 'report', 'readme.txt', 'report-final.txt', 'Report.txt')) (Rows $app)
Check 'フォルダーが先' ((Get-RowNames $app)[0] -eq 'report') (Rows $app)
Send-Keys $app 'ep'
Check '大文字と小文字を区別しない' ((Found $app) -eq (Expected 'report', 'report-final.txt', 'Report.txt')) (Rows $app)
Check '場所の列が出る' ((Headers $app) -contains '場所') ((Headers $app) -join ', ')
Check '場所の列は名前のすぐ右' ((Headers $app)[1] -eq '場所') ((Headers $app) -join ', ')
Check 'フォルダーの場所' ((Place $app 'report-final.txt') -eq '探す.zip\docs\2024') (Place $app 'report-final.txt')
Check 'ルートの場所は書庫の名前' ((Place $app 'report') -eq '探す.zip') (Place $app 'report')
Check '区切りに検索結果と出る' ((Crumbs $app) -eq '探す.zip > 「rep」の検索結果') (Crumbs $app)
Check '見出しが切れない' ((Get-ClippedHeaders (ById $app.Window 'EntryList')).Count -eq 0) ((Get-ClippedHeaders (ById $app.Window 'EntryList')) -join ', ')
Send-Keys $app 'ort-'
Check '書き足すとさらに絞る' ((Rows $app) -eq 'report-final.txt') (Rows $app)
Send-Keys $app '{BACKSPACE}{BACKSPACE}{BACKSPACE}{BACKSPACE}'
Check '消すと広がる' ((Found $app) -eq (Expected 'report', 'report-final.txt', 'Report.txt')) (Rows $app)

Section '字の違い'
Set-Text $box 'ＲＥＰＯＲＴ'
Check '全角でも見つかる' ((Found $app) -eq (Expected 'report', 'report-final.txt', 'Report.txt')) (Rows $app)
Set-Text $box 'がっこう'
Check '濁点が分かれた名前も見つかる' ((Rows $app) -eq $decomposed) (Rows $app)
Set-Text $box 'zzz'
Check '無ければそう言う' ((Texts $app.Window) -match '検索条件に一致する項目はありません')

Section 'Esc で戻る'
Send-Keys $app '^f'
Send-Keys $app '{ESC}'
Check '元の一覧に戻る' ((Rows $app) -eq 'docs, img, report, メモ, readme.txt') (Rows $app)
Check '欄が空になる' ((ValueOf $box) -eq '') (ValueOf $box)
Check '場所の列が消える' (-not ((Headers $app) -contains '場所')) ((Headers $app) -join ', ')
Check '区切りが戻る' ((Crumbs $app) -eq '探す.zip') (Crumbs $app)

Section '書庫全体を探す'
Select-Row $app 'img' | Out-Null
Send-Keys $app '{ENTER}'
Type-Search $app 'txt'
Check '見ているフォルダーの外も見つかる' ((Found $app) -eq (Expected 'a.txt', $decomposed, 'readme.txt', 'report-final.txt', 'Report.txt')) (Rows $app)
Send-Keys $app '{DOWN}'
Check '↓ で結果へ移る' ((Focused) -ne 'SearchBox') (Focused)
Send-Keys $app '{BACKSPACE}'
Check 'Backspace で探す前のフォルダーへ戻る' ((Crumbs $app) -eq '探す.zip > img') (Crumbs $app)
Check '中身も戻る' ((Rows $app) -eq 'photo.jpg') (Rows $app)
Check '欄も空になる' ((ValueOf $box) -eq '') (ValueOf $box)

Section '結果のフォルダーに入る'
Type-Search $app 'report'
Select-Row $app 'report' | Out-Null
Send-Keys $app '{ENTER}'
Check 'そのフォルダーへ移る' ((Crumbs $app) -eq '探す.zip > report') (Crumbs $app)
Check '探すのをやめる' ((ValueOf $box) -eq '') (ValueOf $box)
Check '中身が並ぶ' ((Rows $app) -eq 'a.txt') (Rows $app)

Section '結果から展開する'
Type-Search $app 'report'
$found = Join-Path $script:Work 'found'
$picked = Join-Path $script:Work 'picked'
New-Item -ItemType Directory -Force -Path $found, $picked | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '選んでいなければ見つかったもの全部' (Complete-FileDialog $app '見つかった項目の展開先を選択' $found)
$result = Find-MessageBox $app 30000
if ($result) { Close-MessageBox $result 'OK' }
$files = @(Get-ChildItem $found -Recurse -File | ForEach-Object { $_.FullName.Substring($found.Length + 1) } | Sort-Object)
Check '書庫の中の階層のまま出る' (($files -join ', ') -eq 'docs\2024\report-final.txt, docs\Report.txt, report\a.txt') ($files -join ', ')

Select-Row $app 'report-final.txt' | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '選んだものだけ' (Complete-FileDialog $app '選択した項目の展開先を選択' $picked)
$result = Find-MessageBox $app 30000
if ($result) { Close-MessageBox $result 'OK' }
$files = @(Get-ChildItem $picked -Recurse -File | ForEach-Object { $_.FullName.Substring($picked.Length + 1) })
Check '選んだものが最上位に来る' (($files -join ', ') -eq 'report-final.txt') ($files -join ', ')

Section '結果から名前を変える'
Set-Text $box 'photo'
Check '名前を書き換えられる' (Rename-Row $app 'photo.jpg' 'picture.jpg')
Wait-Idle $app
$names = Get-ZipNames $archive
Check '入っていたフォルダーのまま変わる' (($names -contains 'img/picture.jpg') -and -not ($names -contains 'img/photo.jpg')) ($names -join ', ')
Check '結果のままでいる' ((ValueOf $box) -eq 'photo') (ValueOf $box)

Section '結果から削除する'
Set-Text $box 'a.txt'
Select-Row $app 'a.txt' | Out-Null
Send-Keys $app '{DEL}'
$confirm = Find-MessageBox $app
Check '確かめる' ($null -ne $confirm)
if ($confirm) { Close-MessageBox $confirm 'はい(Y)' }
Wait-Idle $app
Check '書庫から消える' (-not ((Get-ZipNames $archive) -contains 'report/a.txt')) ((Get-ZipNames $archive) -join ', ')
Check '結果を出し直す' ((ValueOf $box) -eq 'a.txt' -and (Texts $app.Window) -match '検索条件に一致する項目はありません') (Rows $app)
Send-Keys $app '^f'
Send-Keys $app '{ESC}'
Check '探す前のフォルダーに戻る' ((Crumbs $app) -eq '探す.zip > report') (Crumbs $app)

Section 'ツリーで選ぶと探すのをやめる'
Type-Search $app 'txt'
$tree = ById $app.Window 'FolderTree'
$current = ByType $tree $script:ControlType::TreeItem | Where-Object { $null -ne (ByName $_ 'report') } | Select-Object -Last 1
Check '探している間はツリーを選ばない' ($current -and -not $current.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected)
if ($current) {
    Select-Element $current
    Start-Sleep -Milliseconds 600
    Check '押したフォルダーへ移る' ((Crumbs $app) -eq '探す.zip > report') (Crumbs $app)
    Check '欄が空になる' ((ValueOf $box) -eq '') (ValueOf $box)
}

Section '× で消す'
Type-Search $app 'txt'
$clear = ById $app.Window 'SearchClear'
# 入力中は Fluent の入力欄が自分の × を出す。2 つ並ばないよう、こちらのは隠れている
Check '入力中は出さない' ($null -eq $clear -or $clear.Current.IsOffscreen)
Send-Keys $app '{DOWN}'
$clear = ById $app.Window 'SearchClear'
Check '欄から離れると出る' ($clear -and -not $clear.Current.IsOffscreen)
Check '名前がある' ($clear -and $clear.Current.Name -eq '検索をやめる') $(if ($clear) { $clear.Current.Name })
if ($clear) { Push $clear }
Check '押すと探す前に戻る' ((ValueOf $box) -eq '' -and (Crumbs $app) -eq '探す.zip > report') "$(Crumbs $app) / $(ValueOf $box)"
Stop-Expzip $app

Complete-Suite
