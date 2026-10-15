# 書庫の書き換えを元に戻す (#260)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Undo'
Set-Settings

function New-Sample([string]$Name) {
    return New-TestZip (Join-Path $script:Work $Name) ([ordered]@{
        'docs/a.txt'  = 'a'
        'docs/b.txt'  = 'b'
        'other/c.txt' = 'c'
        'top.txt'     = 'top'
    })
}

function Sorted([string]$Archive) { return ((Get-ZipNames $Archive | Sort-Object) -join ', ') }

function Hash([string]$Path) { return (Get-FileHash -Algorithm SHA256 $Path).Hash }

# ツリーの項目。自分の名前 (最初の字) で探す
function Find-TreeItem($App, [string]$Name) {
    $tree = ById $App.Window 'FolderTree'
    return ByType $tree $script:ControlType::TreeItem | Where-Object {
        $text = ByType $_ $script:ControlType::Text | Select-Object -First 1
        $text -and $text.Current.Name -eq $Name
    } | Select-Object -First 1
}

# 一覧の行を掴んで、ツリーのフォルダーへ落とす。利用者がうっかりやってしまう操作
function Move-RowToTree($App, [string]$Row, [string]$Folder) {
    Focus-App $App
    $target = Find-TreeItem $App $Folder
    if ($null -eq $target) { Check "ツリーに $Folder がある" $false; return }
    $from = (ByName (Find-Row $App $Row) $Row).Current.BoundingRectangle
    $to = (ByName $target $Folder).Current.BoundingRectangle
    Invoke-MouseDrag ([int]($from.X + $from.Width / 2)) ([int]($from.Y + $from.Height / 2)) `
        ([int]($to.X + $to.Width / 2)) ([int]($to.Y + $to.Height / 2))
    Wait-Idle $App
}

function Remove-Row($App, [string]$Name) {
    Select-Row $App $Name | Out-Null
    Send-Keys $App '{DEL}'
    $box = Find-MessageBox $App
    if ($box) { Close-MessageBox $box 'はい(Y)' }
    Wait-Idle $App
}

function Undo($App) {
    Focus-App $App
    (ById $App.Window 'EntryList').SetFocus()
    Send-Keys $App '^z'
    Wait-Idle $App
}

# 右クリックのメニューの「元に戻す」の名前と、押せるか。$Row の行の上で出す
function Get-UndoItem($App, [string]$Row) {
    $menu = Open-RowMenu $App $Row
    $item = if ($menu) { ByType $menu $script:ControlType::MenuItem | Where-Object { $_.Current.Name -like '元に戻す*' } | Select-Object -First 1 }
    $result = if ($item) { [pscustomobject]@{ Name = $item.Current.Name; Enabled = $item.Current.IsEnabled } }
    Close-DropDown $App
    return $result
}

Section 'うっかり動かしたフォルダーを戻す'
$archive = New-Sample 'sample.zip'
$original = Hash $archive
$app = Start-Expzip @($archive)
$item = Get-UndoItem $app 'top.txt'
Check '書き換える前は戻せない' ($item -and $item.Name -eq '元に戻す' -and -not $item.Enabled) "$($item.Name) / $($item.Enabled)"
Undo $app
Check '戻せるものが無いと言う' ((Get-Status $app) -eq '元に戻せる操作がありません') (Get-Status $app)

Move-RowToTree $app 'docs' 'other'
Check 'docs が other の中へ移る' ((Sorted $archive) -match 'other/docs/a\.txt') (Sorted $archive)
Check '戻せることを知らせる' ((Get-Status $app) -eq '1 個の項目を移動しました (Ctrl+Z で元に戻せます)') (Get-Status $app)
# 移した先のフォルダーが開いている
$item = Get-UndoItem $app 'docs'
Check 'メニューに何を戻すかが出る' ($item -and $item.Name -eq '元に戻す - 移動' -and $item.Enabled) "$($item.Name) / $($item.Enabled)"

Undo $app
Check '元の場所に戻る' ((Sorted $archive) -eq 'docs/a.txt, docs/b.txt, other/c.txt, top.txt') (Sorted $archive)
Check '書庫がバイト単位で元どおり' ((Hash $archive) -eq $original)
Check '戻したと言う' ((Get-Status $app) -eq '元に戻しました: 移動') (Get-Status $app)
Check '開いていた other から docs が消える' (((Get-RowNames $app) -join ', ') -eq 'c.txt') ((Get-RowNames $app) -join ', ')
Send-Keys $app '{BACKSPACE}'
Wait-Idle $app
Check 'いちばん上に docs が戻る' ((((Get-RowNames $app) | Sort-Object) -join ', ') -eq 'docs, other, top.txt') ((Get-RowNames $app) -join ', ')

Section '何回分も、新しいものから戻す'
Remove-Row $app 'top.txt'
Check '削除のあとも戻せると知らせる' ((Get-Status $app) -eq '1 個の項目を削除しました (Ctrl+Z で元に戻せます)') (Get-Status $app)
Rename-Row $app 'other' 'other2' | Out-Null
Wait-Idle $app
Check '消して、名前を変えた' ((Sorted $archive) -eq 'docs/a.txt, docs/b.txt, other2/c.txt') (Sorted $archive)
$item = Get-UndoItem $app 'docs'
Check '次に戻すのは名前の変更' ($item -and $item.Name -eq '元に戻す - 名前の変更') $item.Name
Undo $app
Check '名前の変更が戻る' ((Sorted $archive) -eq 'docs/a.txt, docs/b.txt, other/c.txt') (Sorted $archive)
Undo $app
Check '削除が戻る' ((Sorted $archive) -eq 'docs/a.txt, docs/b.txt, other/c.txt, top.txt') (Sorted $archive)
Check 'ここでも元どおり' ((Hash $archive) -eq $original)
Undo $app
Check 'もう戻せない' ((Get-Status $app) -eq '元に戻せる操作がありません') (Get-Status $app)

Section 'コメントの変更を戻す'
$menu = Open-RowMenu $app 'docs'
Push (ByName $menu '書庫のコメント')
$window = Find-Window $app '書庫のコメント - sample.zip' 10000
if ($window) {
    Set-Text (ById $window 'CommentBox') 'あとで消すコメント'
    Push (ById $window 'SaveButton')
}
Wait-Idle $app
Check 'コメントが付く' ((Hash $archive) -ne $original)
Undo $app
Check 'コメントを付ける前に戻る' ((Hash $archive) -eq $original)
$notice = ById $app.Window 'CommentButton'
Check 'コメントの知らせも消える' (-not ($notice -and -not $notice.Current.IsOffscreen))

Section 'ほかのソフトが書き換えたあとは戻さない'
Remove-Row $app 'top.txt'
# 外で 1 個足す。戻すとこれが消えるので、戻してはいけない
$zip = [System.IO.Compression.ZipFile]::Open($archive, [System.IO.Compression.ZipArchiveMode]::Update)
try { $entry = $zip.CreateEntry('outside.txt'); $w = New-Object System.IO.StreamWriter($entry.Open()); $w.Write('x'); $w.Dispose() }
finally { $zip.Dispose() }
Start-Sleep -Milliseconds 1500
$box = $null
Focus-App $app
(ById $app.Window 'EntryList').SetFocus()
Send-Keys $app '^z'
$box = Find-MessageBox $app 10000
Check '戻せない理由を言う' ($box -and $box.Text -match 'ほかのソフトで書き換えられたため、元に戻せません') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'OK' }
Wait-Idle $app
Check '外で足したものは残る' ((Sorted $archive) -match 'outside\.txt') (Sorted $archive)
Stop-Expzip $app

Complete-Suite
