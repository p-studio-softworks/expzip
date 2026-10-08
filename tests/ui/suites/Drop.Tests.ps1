# ウィンドウへのドロップ: 書庫を開いていないときに書庫ではないものを落とすと、新しい書庫を作るか尋ねる (#156)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Drop'
Set-Settings

$question = '書庫を開いていません。新しい書庫を作成して、ドロップした項目を追加しますか?'
$dialogTitle = '新しい書庫を作成'

$folder = Join-Path $script:Work '資料'
New-Item -ItemType Directory -Force -Path (Join-Path $folder 'sub') | Out-Null
Set-Content -Path (Join-Path $folder 'a.txt') -Value 'a' -Encoding UTF8
Set-Content -Path (Join-Path $folder 'sub\b.txt') -Value 'b' -Encoding UTF8

$bundle = Join-Path $script:Work '束'
New-Item -ItemType Directory -Force -Path $bundle | Out-Null
$x = Join-Path $bundle 'x.txt'
$y = Join-Path $bundle 'y.txt'
$memo = Join-Path $bundle 'memo.v2.txt'
Set-Content -Path $x -Value 'x' -Encoding UTF8
Set-Content -Path $y -Value 'y' -Encoding UTF8
Set-Content -Path $memo -Value 'memo' -Encoding UTF8

$made = Join-Path $script:Work '資料.zip'

$app = Start-Expzip
$center = Get-WindowCenter $app

Section 'いいえ'
Check '落とせる' (Invoke-Drop @($folder) $center.X $center.Y)
$box = Find-MessageBox $app
Check '尋ねる' ($null -ne $box)
if ($box) {
    Check '文言' ($box.Text -eq $question) $box.Text
    Check 'はい・いいえで答える' (($box.Buttons -join ',') -eq 'はい(Y),いいえ(N)') ($box.Buttons -join ', ')
    Close-MessageBox $box 'いいえ(N)'
}
Check '保存先を尋ねない' ($null -eq (Find-Window $app $dialogTitle 2000))
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 0) (Get-Tabs $app).Count
Check '書庫はできない' (-not (Test-Path $made))

Section 'はい'
Focus-App $app
Invoke-Drop @($folder) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
Check '尋ねる' ($null -ne $box)
if ($box) { Close-MessageBox $box 'はい(Y)' }
$name = Get-FileDialogName $app $dialogTitle
Check '名前の初期値はフォルダーの名前' ($name -eq '資料.zip') $name
# 名前だけを入れて確定する。保存先の初期値が、落としたフォルダーの隣でなければ別の場所にできる
Check '保存先を決める' (Complete-FileDialog $app $dialogTitle '資料.zip')
Wait-Idle $app
Check '落としたフォルダーの隣にできる' (Test-Path $made) $made
if (Test-Path $made) {
    $names = Get-ZipNames $made
    Check '中身が入る' ($names -contains '資料/a.txt' -and $names -contains '資料/sub/b.txt') ($names -join ', ')
}
Check 'タブで開く' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Check '一覧に並ぶ' ((Get-RowNames $app) -contains '資料') ((Get-RowNames $app) -join ', ')

Section '書庫を開いているときは今どおり追加する'
Focus-App $app
Invoke-Drop @($x) $center.X $center.Y | Out-Null
Wait-Idle $app
Check '尋ねない' ($null -eq (Find-MessageBox $app 2000))
Check '開いている書庫に入る' ((Get-ZipNames $made) -contains 'x.txt') ((Get-ZipNames $made) -join ', ')
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Stop-Expzip $app

$app = Start-Expzip
$center = Get-WindowCenter $app

Section '複数を落とす'
Invoke-Drop @($x, $y) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
Check '尋ねる' ($null -ne $box)
if ($box) { Close-MessageBox $box 'はい(Y)' }
$name = Get-FileDialogName $app $dialogTitle
Check '名前の初期値は入っていたフォルダーの名前' ($name -eq '束.zip') $name
Check '取りやめられる' (Cancel-FileDialog $app $dialogTitle)
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 0) (Get-Tabs $app).Count
Check '書庫はできない' (-not (Test-Path (Join-Path $script:Work '束.zip')))

Section 'ファイルを 1 個落とす'
Focus-App $app
Invoke-Drop @($memo) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
$name = Get-FileDialogName $app $dialogTitle
Check '名前の初期値は拡張子を除いたファイルの名前' ($name -eq 'memo.v2.zip') $name
Cancel-FileDialog $app $dialogTitle | Out-Null

Section '書庫は尋ねずに開く'
Focus-App $app
Invoke-Drop @($made) $center.X $center.Y | Out-Null
Wait-Idle $app
Check '尋ねない' ($null -eq (Find-MessageBox $app 2000))
Check 'タブで開く' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Check '一覧に並ぶ' ((Get-RowNames $app) -contains '資料') ((Get-RowNames $app) -join ', ')
Stop-Expzip $app

# 追加するときに .DS_Store を入れず、名前の濁点を分かれていない形 (NFC) に揃える (#202)
$mac = Join-Path $script:Work 'mac'
function New-MacFolder([string]$Name, [string[]]$Files) {
    $path = Join-Path $mac $Name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    foreach ($file in $Files) { Set-Content -Path (Join-Path $path $file) -Value $file -Encoding UTF8 }
    return $path
}
$withStore = New-MacFolder 'f' @('.DS_Store', 'a.txt')
$onlyStore = New-MacFolder 'g' @('.DS_Store')
$lowerStore = New-MacFolder 'k' @('.ds_store', 'x.txt')
$loose = Join-Path (New-MacFolder 'h' @('.DS_Store')) '.DS_Store'
# Mac のファイル名と同じく、「ガ」を「カ」と濁点の 2 文字で持つ名前
$nfdGa = 'カ' + [char]0x3099
$nfcGa = [string][char]0x30AC
$nfdFile = Join-Path $mac ($nfdGa + '.txt')
Set-Content -Path $nfdFile -Value 'nfd' -Encoding UTF8
$nfdFolder = New-MacFolder ($nfdGa + 'ゾウ') @('b.txt')
$nfcFile = Join-Path $mac 'バナナ.txt'
Set-Content -Path $nfcFile -Value 'nfc' -Encoding UTF8

# 開いている書庫の、一覧の下の空いたところへ落とす。行の上に落とすと、そのフォルダーに入る
function Get-ListBlank($App) {
    Focus-App $App
    $rect = (ById $App.Window 'EntryList').Current.BoundingRectangle
    return [pscustomobject]@{ X = [int]($rect.X + $rect.Width / 2); Y = [int]($rect.Bottom - 30) }
}

function Drop-Into($App, [string[]]$Paths) {
    $spot = Get-ListBlank $App
    Invoke-Drop $Paths $spot.X $spot.Y | Out-Null
    Answer-Password $App
    Wait-Idle $App
}

# パスワード付きの書庫では、書き換える前に尋ねられることがある。尋ねられたら答える
$secret = 'Kagi-2026'
function Answer-Password($App) {
    $dialog = Find-Window $App 'パスワード' 3000
    if ($null -eq $dialog) { return }
    $box = ByType $dialog $script:ControlType::Edit | Select-Object -First 1
    $box.SetFocus()
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait($secret)
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
}

function Test-MacDrops([string]$Archive, $App) {
    Drop-Into $App @($withStore)
    $names = Get-ZipNames $Archive
    Check 'フォルダーの中身は入る' ($names -contains 'f/a.txt') ($names -join ', ')
    Check '.DS_Store は入らない' (-not ($names -contains 'f/.DS_Store')) ($names -join ', ')

    Drop-Into $App @($nfdFile, $nfdFolder)
    $names = Get-ZipNames $Archive
    Check 'NFD のファイルの名前が NFC になる' ($names -contains ($nfcGa + '.txt')) ($names -join ', ')
    Check 'NFD のフォルダーの名前も NFC になる' ($names -contains ($nfcGa + 'ゾウ/b.txt')) ($names -join ', ')
    $split = @($names | Where-Object { -not $_.IsNormalized([Text.NormalizationForm]::FormC) })
    Check '分かれた濁点の名前は無い' ($split.Count -eq 0) ($split -join ', ')
}

Section '.DS_Store を入れない (#202)'
$macZip = New-TestZip (Join-Path $script:Work 'mac.zip') ([ordered]@{ 'base.txt' = 'base' })
$app = Start-Expzip @($macZip)
Drop-Into $app @($withStore)
$names = Get-ZipNames $macZip
Check 'フォルダーの中身は入る' ($names -contains 'f/a.txt') ($names -join ', ')
Check '.DS_Store は入らない' (-not ($names -contains 'f/.DS_Store')) ($names -join ', ')

Drop-Into $app @($onlyStore)
$names = Get-ZipNames $macZip
Check '.DS_Store だけのフォルダーは空のフォルダーになる' (
    ($names -contains 'g/') -and -not ($names | Where-Object { $_ -like 'g/?*' })) ($names -join ', ')

Drop-Into $app @($loose)
$names = Get-ZipNames $macZip
Check '直接選んだ .DS_Store は入る' ($names -contains '.DS_Store') ($names -join ', ')

Drop-Into $app @($lowerStore)
$names = Get-ZipNames $macZip
Check '大文字小文字が違っても入らない' (($names -contains 'k/x.txt') -and -not ($names -contains 'k/.ds_store')) ($names -join ', ')

Section '名前の濁点を NFC に揃える (#202)'
Drop-Into $app @($nfdFile, $nfdFolder)
$names = Get-ZipNames $macZip
Check 'NFD のファイルの名前が NFC になる' ($names -contains ($nfcGa + '.txt')) ($names -join ', ')
Check 'NFD のフォルダーの名前も NFC になる' ($names -contains ($nfcGa + 'ゾウ/b.txt')) ($names -join ', ')
Drop-Into $app @($nfcFile)
$names = Get-ZipNames $macZip
Check 'NFC の名前は変わらない' ($names -contains 'バナナ.txt') ($names -join ', ')
$split = @($names | Where-Object { -not $_.IsNormalized([Text.NormalizationForm]::FormC) })
Check '分かれた濁点の名前は無い' ($split.Count -eq 0) ($split -join ', ')
Stop-Expzip $app

Section 'パスワード付きの書庫でも同じ (#202)'
$lockedZip = New-TestZip (Join-Path $script:Work 'mac-locked.zip') ([ordered]@{ 'base.txt' = 'base' })
$app = Start-Expzip @($lockedZip)
Push (ById $app.Window 'PasswordButton')
Answer-Password $app
Wait-Idle $app
Check 'パスワードを設定する' ((Get-Status $app) -match 'パスワードを設定しました') (Get-Status $app)
Stop-Expzip $app
$app = Start-Expzip @($lockedZip)
Test-MacDrops $lockedZip $app
Stop-Expzip $app

Section 'エクスプローラーへ持ち出す'
# 書庫の項目をつかんでエクスプローラーへ落とす (#17)。同じドライブへ落とすと、エクスプローラーは
# コピーではなく移動を選ぶ。以前は移動のとき、ステータスバーが「展開しています…」のまま残っていた (#157)
$outZip = New-TestZip (Join-Path $script:Work 'motidasi.zip') ([ordered]@{ 'motidasi.txt' = 'out' })
$dest = Join-Path $script:Work 'okiba'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$app = Start-Expzip @($outZip)

Start-Process explorer.exe "`"$dest`""
$explorer = Wait-Until -TimeoutMs 15000 {
    $script:Automation::RootElement.FindAll($script:Scope::Children,
        (Condition $script:Automation::ClassNameProperty 'CabinetWClass')) |
        Where-Object { $_.Current.Name -match '^okiba' } | Select-Object -First 1
}
Check 'エクスプローラーが開く' ($null -ne $explorer)
if ($explorer) {
    # Expzip の横に並べる。重なっていると、運ぶ途中で相手が隠れる
    # 大きさは Expzip に合わせる (画面の倍率によらず、左の一覧と右のファイルの欄が十分に広くなる)
    $appRect = $app.Window.Current.BoundingRectangle
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $width = [int]$appRect.Width
    $left = if ($appRect.Right + 20 + $width -le $screen.Right) { $appRect.Right + 20 } else { [Math]::Max($screen.Left, $appRect.Left - $width - 20) }
    [ExpzipUi.Native]::MoveWindow([IntPtr]$explorer.Current.NativeWindowHandle, [int]$left, [int]$appRect.Top, $width, [int]$appRect.Height, $true) | Out-Null
    Start-Sleep -Milliseconds 800

    Focus-App $app
    $name = (ByName (Find-Row $app 'motidasi.txt') 'motidasi.txt').Current.BoundingRectangle
    # 落とすのはファイルの一覧 (項目ビュー) の真ん中。位置を決め打ちすると、左のナビゲーション
    # (デスクトップなどに届く) や、右の詳細ウィンドウ (受け取らない) に当たることがある
    $items = Find-One $explorer (Condition $script:Automation::ClassNameProperty 'UIItemsView')
    $target = if ($items) { $items.Current.BoundingRectangle } else { $explorer.Current.BoundingRectangle }
    Invoke-MouseDrag ([int]($name.X + $name.Width / 2)) ([int]($name.Y + $name.Height / 2)) `
        ([int]($target.X + $target.Width / 2)) ([int]($target.Y + $target.Height / 2))

    $arrived = Wait-Until -TimeoutMs 10000 { Test-Path (Join-Path $dest 'motidasi.txt') }
    Check 'エクスプローラーに届く' ([bool]$arrived)
    $status = Wait-Until -TimeoutMs 5000 { $text = Get-Status $app; if ($text -notmatch '展開しています') { $text } }
    Check '展開したと言う' ($status -eq '1 個の項目を展開しました') (Get-Status $app)

    # 開いたエクスプローラーの窓だけを閉じる
    [ExpzipUi.Native]::PostMessage([IntPtr]$explorer.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}
Stop-Expzip $app

Complete-Suite
