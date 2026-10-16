# 処理時間の見積もり (#269)。このパソコンで実際に試して測り、結果を残す
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Speed'
Set-Settings

$title = '処理時間の見積もり'
$place = Join-Path $script:Work 'place'
New-Item -ItemType Directory -Force -Path $place | Out-Null

function Open-SpeedWindow($App) {
    Push (ById $App.Window 'SpeedButton')
    return Find-Window $App $title 15000
}

function Get-Headline($Window) { return (ById $Window 'Headline').Current.Name }

function Get-EstimateRows($Window) {
    $list = ById $Window 'EstimateList'
    if ($null -eq $list) { return @() }
    return @($list.FindAll($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem)) |
        ForEach-Object { $_.Current.Name })
}

# 表のマスで、字が欄に収まらず切れているもの。描かれた幅を本来の幅と比べる (Get-ClippedHeaders と同じ測り方)
function Get-ClippedCells($List) {
    $measured = @()
    foreach ($row in @(ByType $List $script:ControlType::DataItem)) {
        foreach ($text in @(ByType $row $script:ControlType::Text)) {
            $name = $text.Current.Name
            $rect = $text.Current.BoundingRectangle
            if (-not $name -or $rect.IsEmpty -or $rect.Width -lt 4) { continue }
            $ink = Measure-InkWidth $rect
            $shape = Measure-TextShape $name
            if ($ink -le 0 -or $shape -le 0) { continue }
            $measured += [pscustomobject]@{ Name = $name; Ratio = $ink / $shape }
        }
    }
    if ($measured.Count -lt 2) { return @("マスを測れない ($($measured.Count) 個)") }
    $full = (@($measured | Sort-Object Ratio))[[int][Math]::Floor($measured.Count / 2)].Ratio
    return @($measured | Where-Object { $_.Ratio -lt $full * 0.85 } | ForEach-Object { $_.Name })
}

function IsSelectedChoice($Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    return $pattern.Current.IsSelected
}

# 試したファイルを残していないこと。作業用のフォルダーごと消える
function Get-Leftovers([string]$Folder) {
    return @(Get-ChildItem $Folder -Force -ErrorAction SilentlyContinue)
}

$app = Start-Expzip

Section 'まだ測っていないとき'
Check '書庫を開いていなくても押せる' ((ById $app.Window 'SpeedButton').Current.IsEnabled)
$window = Open-SpeedWindow $app
Check 'ウィンドウが開く' ($null -ne $window)
if ($window) {
    Check '見出し' ((Get-Headline $window) -eq 'まだ測定していません') (Get-Headline $window)
    Check '結果の表は出さない' ((Get-EstimateRows $window).Count -eq 0)
    Check 'コピーは押せない' (-not (ById $window 'CopyButton').Current.IsEnabled)
    $temp = ById $window 'TempChoice'
    Check '最初は一時フォルダー' (IsSelectedChoice $temp) $temp.Current.Name
    Check '一時フォルダーの場所を出す' ($temp.Current.Name -like '一時フォルダー (*)') $temp.Current.Name
    Check 'フォルダーを選ぶまでは「(選択していません)」' ((ById $window 'FolderChoice').Current.Name -eq '指定したフォルダー ((選択していません))') (ById $window 'FolderChoice').Current.Name

    Section '選んだフォルダーで測る'
    Push (ById $window 'BrowseButton') 1500
    Check 'フォルダーを選ぶ' (Complete-FileDialog $app '測定するフォルダーを選択' $place)
    $folder = ById $window 'FolderChoice'
    Check '選んだフォルダーに切り替わる' (IsSelectedChoice $folder) $folder.Current.Name
    Check '選んだ場所を出す' ($folder.Current.Name -eq "指定したフォルダー ($place)") $folder.Current.Name

    $clock = [Diagnostics.Stopwatch]::StartNew()
    Push (ById $window 'StartButton') 300
    $running = Get-Headline $window
    Check '測っている間は何をしているかを出す' ($running -match '\(\d+ / 10\)$') $running
    Check '測っている間は「中断」になる' ((ById $window 'StartButton').Current.Name -eq '中断')
    $finished = Wait-Until -TimeoutMs 120000 { (Get-Headline $window) -eq '測定しました' }
    Check '測り終わる' ($finished) ("{0:0.0} 秒 / {1}" -f $clock.Elapsed.TotalSeconds, (Get-Headline $window))
    # 10 秒ほどに収める。遅い PC でも 1 分はかけない
    Check '長くかからない' ($clock.Elapsed.TotalSeconds -lt 60) ("{0:0.0} 秒" -f $clock.Elapsed.TotalSeconds)

    $rows = Get-EstimateRows $window
    Check '1 / 10 / 100 GB の 3 行' ($rows.Count -eq 3) ($rows -join ' / ')
    Check '行の名前に大きさと 3 種類の時間' ($rows.Count -gt 0 -and $rows[0] -match '^1 GB、文書のようなもの: 圧縮 .+ / 展開 .+、写真や動画: 圧縮 .+、小さなファイルがたくさん: 圧縮 .+$') $rows[0]
    # 見出しも、いちばん長くなる 100 GB の行も欄に収まる (#143)
    $clipped = Get-ClippedHeaders (ById $window 'EstimateList')
    Check '表の見出しが切れていない' ($clipped.Count -eq 0) ($clipped -join ' / ')
    $clipped = Get-ClippedCells (ById $window 'EstimateList')
    Check '表のマスが切れていない' ($clipped.Count -eq 0) ($clipped -join ' / ')
    $conditions = (ById $window 'Conditions').Current.Name
    Check '条件にメモリー' ($conditions -match 'メモリー: \d+ GB') $conditions
    Check '条件にパソコンの名前' ($conditions -match "パソコン: $([regex]::Escape($env:COMPUTERNAME))") $conditions
    Check '条件にコアの数' ($conditions -match "\($([Environment]::ProcessorCount) コア\)") $conditions
    Check '条件に保存先' ($conditions -match "保存先: (SSD|HDD|ネットワーク|不明) \($([regex]::Escape($place))\)") $conditions
    Check '条件にディスクの速さ' ($conditions -match 'ディスクの速さ: 読み込み [\d,]+ MB/秒、書き込み [\d,]+ MB/秒') $conditions
    Check '試したファイルを残さない' ((Get-Leftovers $place).Count -eq 0) ((Get-Leftovers $place) -join ', ')

    Section 'コピー'
    $copy = ById $window 'CopyButton'
    Check 'コピーが押せる' ($copy.Current.IsEnabled)
    Push $copy
    $clip = [System.Windows.Forms.Clipboard]::GetText()
    $lines = $clip -split "`r`n"
    Check '1 行目は何の結果か' ($lines[0] -eq '処理時間の見積もり (Expzip)') $lines[0]
    Check '条件も入る' ($clip -match "パソコン: $([regex]::Escape($env:COMPUTERNAME))")
    Check '表の見出しはタブ区切り' ($lines -contains "中身の大きさ`t文書のようなもの`t写真や動画`t小さなファイルがたくさん") ($lines -join ' | ')
    Check '表の行もタブ区切り' (@($lines | Where-Object { $_ -match "^100 GB`t圧縮 .+`t圧縮 .+`t圧縮 .+$" }).Count -eq 1)
    Check '目安であることも入る' ($clip -match '目安です。')

    Push (ById $window 'CloseButton')
    Check '閉じる' (Test-WindowGone $app $title)
}

Section '結果を残す'
$settings = Read-Settings
Check '設定ファイルに残る' ($settings -match '"SpeedResult"') ''
Check '種類は名前で書く' ($settings -match '"Kind": "Documents"') ''
Stop-Expzip $app

$app = Start-Expzip
$window = Open-SpeedWindow $app
if ($window) {
    Check '次に開いたときは前回の結果' ((Get-Headline $window) -eq '前回の測定結果') (Get-Headline $window)
    Check '前回の表が出る' ((Get-EstimateRows $window).Count -eq 3)
    Check '前回のフォルダーを選んだまま' (IsSelectedChoice (ById $window 'FolderChoice'))

    Section '中断'
    $before = (ById $window 'Conditions').Current.Name
    Push (ById $window 'StartButton') 1500
    Push (ById $window 'StartButton')
    $stopped = Wait-Until -TimeoutMs 30000 { (Get-Headline $window) -eq '測定を中断しました' }
    Check '中断できる' ($stopped) (Get-Headline $window)
    Check '中断しても前の結果は残る' ((ById $window 'Conditions').Current.Name -eq $before)
    Check '中断しても試したファイルを残さない' ((Get-Leftovers $place).Count -eq 0) ((Get-Leftovers $place) -join ', ')
    Check '「測定する」に戻る' ((ById $window 'StartButton').Current.Name -eq '測定する')
    Save-Shot @{ Window = $window } (Join-Path $script:Work 'speed.png')
    Push (ById $window 'CloseButton')
}

Section '英語'
Stop-Expzip $app
Set-Settings @{ Language = 'en' }
$app = Start-Expzip
$button = ById $app.Window 'SpeedButton'
Check '英語の名前' ($button.Current.Name -eq 'Time estimate') $button.Current.Name
Push $button
$window = Find-Window $app 'Time estimate' 15000
Check '英語のウィンドウ' ($null -ne $window)
if ($window) {
    Check '英語の見出し' ((Get-Headline $window) -eq 'Not measured yet') (Get-Headline $window)
    Push (ById $window 'CloseButton')
}

Stop-Expzip $app
Complete-Suite
