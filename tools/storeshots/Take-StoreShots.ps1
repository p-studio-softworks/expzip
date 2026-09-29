<#
.SYNOPSIS
    Microsoft Store の掲載情報に使うスクリーンショットを撮る (#176)。

.DESCRIPTION
    publish\win-x64\Expzip.exe を一時フォルダーにコピーし、見本の書庫を作って開き、ウィンドウを撮る。
    日本語と英語の 2 通りを publish\store\ja と publish\store\en に書き出す。
    Store 用のアイコン (300 × 300) も publish\store に作る。

    - 先に build.cmd で publish\win-x64\Expzip.exe を作っておく
    - 走っている間はマウスとキーボードに触らないでください。ウィンドウを前に出して撮ります
    - 撮るのは Expzip のウィンドウだけ。Store の決まり (PNG、1366 × 768 以上) に合う大きさにする
    - 利用者の設定や書庫には触らない。部品は tests\ui\Common.ps1 を使う

.EXAMPLE
    .\tools\storeshots\Take-StoreShots.ps1
#>

$ErrorActionPreference = 'Stop'

# 画面の表示倍率を上げていても、ウィンドウの大きさと撮る範囲を実際の画素で扱う
Add-Type -Namespace ExpzipShots -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT r, int size);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
[ExpzipShots.Native]::SetProcessDPIAware() | Out-Null

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $repo 'publish\win-x64\Expzip.exe'
if (-not (Test-Path $source)) {
    Write-Host "Expzip.exe がありません: $source (先に build.cmd を実行してください)"
    exit 1
}

$env:EXPZIP_UI_APP = Join-Path $env:TEMP 'ExpzipStoreShots\app'
New-Item -ItemType Directory -Force -Path $env:EXPZIP_UI_APP | Out-Null
Copy-Item $source $env:EXPZIP_UI_APP -Force

. (Join-Path $repo 'tests\ui\Common.ps1')

$output = Join-Path $repo 'publish\store'
New-Item -ItemType Directory -Force -Path $output | Out-Null

# Store が勧める 1:1 のアイコン。パッケージの絵と同じく tools/icon で描く
& dotnet run --project (Join-Path $repo 'tools\icon') -- --png 300 (Join-Path $output 'AppTile300.png') | Out-Null

# 撮る範囲は影を除いたウィンドウの枠 (DWMWA_EXTENDED_FRAME_BOUNDS)
function Get-FrameBounds([IntPtr]$Handle) {
    $rect = New-Object ExpzipShots.Native+RECT
    [ExpzipShots.Native]::DwmGetWindowAttribute($Handle, 9, [ref]$rect,
        [System.Runtime.InteropServices.Marshal]::SizeOf($rect)) | Out-Null
    return $rect
}

# 見える枠を 16:9 にする。表示倍率 100% のときの 1180 × 664 に当たる大きさで、Store の最低
# (1366 × 768) を下回るときはそこまで広げる。
# 大きすぎると見本の書庫では大半が空いて見え、小さすぎると一覧の列が切れ、
# 検査結果のウィンドウがはみ出す (どちらも 150% の画面で試した)
function Set-ShotSize($App) {
    $handle = $App.Process.MainWindowHandle
    $scale = [ExpzipShots.Native]::GetDpiForWindow($handle) / 96.0
    $width = [Math]::Max(1366, [int](1180 * $scale))
    $height = [int]($width * 9 / 16)

    $outer = New-Object ExpzipShots.Native+RECT
    [ExpzipShots.Native]::GetWindowRect($handle, [ref]$outer) | Out-Null
    $frame = Get-FrameBounds $handle
    $extraWidth = ($outer.Right - $outer.Left) - ($frame.Right - $frame.Left)
    $extraHeight = ($outer.Bottom - $outer.Top) - ($frame.Bottom - $frame.Top)

    [ExpzipUi.Native]::ShowWindow($handle, 1) | Out-Null
    [ExpzipUi.Native]::MoveWindow($handle, 40, 40, $width + $extraWidth, $height + $extraHeight, $true) | Out-Null
    Start-Sleep -Milliseconds 800
}

function Save-Shot($App, [string]$Path) {
    Focus-App $App
    Start-Sleep -Milliseconds 600
    $frame = Get-FrameBounds $App.Process.MainWindowHandle
    $width = $frame.Right - $frame.Left
    $height = $frame.Bottom - $frame.Top
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($frame.Left, $frame.Top, 0, 0, $bitmap.Size)
    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host ("書きました: {0} ({1} × {2})" -f $Path, $width, $height)
}

# 中身のある見本。圧縮が効くものと効かないもの (写真) を混ぜ、一覧の圧縮率に差が出るようにする
function New-Text([string]$Line, [int]$Count) { return (@($Line) * $Count) -join "`r`n" }
function New-Noise([int]$Size) {
    $bytes = New-Object byte[] $Size
    (New-Object System.Random 176).NextBytes($bytes)
    return , $bytes
}

$samples = @{
    ja = @{
        Main = [ordered]@{
            '写真/'                  = ''
            '写真/2026-08-01 海.jpg'  = New-Noise 2400000
            '写真/2026-08-01 夕日.jpg' = New-Noise 1800000
            '写真/2026-08-02 山.jpg'  = New-Noise 3100000
            '資料/'                  = ''
            '資料/行程表.xlsx'         = New-Noise 48000
            '資料/予約の控え.pdf'       = New-Noise 210000
            '資料/持ち物リスト.txt'      = New-Text '着替え、充電器、カメラ、雨具' 400
            '地図/'                  = ''
            '地図/宿までの道.png'       = New-Noise 820000
            'はじめにお読みください.txt'  = New-Text 'この書庫には夏の旅行の写真と資料が入っています。' 300
        }
        MainName = '夏の旅行.zip'
        Risky = [ordered]@{
            '請求書/'               = ''
            '請求書/2026年9月分.pdf'   = New-Noise 90000
            '請求書/明細.pdf.exe'     = 'MZ'
            '../../スタートアップ/update.bat' = 'echo'
            'CON.txt'              = 'x'
            'readme.txt'           = New-Text '請求書を送ります。' 50
        }
        RiskyName = '請求書_9月.zip'
        Inspection = '検査結果 - 請求書_9月.zip'
    }
    en = @{
        Main = [ordered]@{
            'Photos/'                   = ''
            'Photos/2026-08-01 Beach.jpg'  = New-Noise 2400000
            'Photos/2026-08-01 Sunset.jpg' = New-Noise 1800000
            'Photos/2026-08-02 Hills.jpg'  = New-Noise 3100000
            'Documents/'                = ''
            'Documents/Itinerary.xlsx'  = New-Noise 48000
            'Documents/Booking.pdf'     = New-Noise 210000
            'Documents/Packing list.txt' = New-Text 'Clothes, charger, camera, raincoat' 400
            'Maps/'                     = ''
            'Maps/Route to hotel.png'   = New-Noise 820000
            'Read me first.txt'         = New-Text 'This archive contains photos and documents from our summer trip.' 300
        }
        MainName = 'Summer trip.zip'
        Risky = [ordered]@{
            'Invoices/'                  = ''
            'Invoices/September 2026.pdf' = New-Noise 90000
            'Invoices/Details.pdf.exe'   = 'MZ'
            '../../Startup/update.bat'   = 'echo'
            'CON.txt'                    = 'x'
            'readme.txt'                 = New-Text 'Please find the invoice attached.' 50
        }
        RiskyName = 'Invoice_September.zip'
        Inspection = 'Inspection results - Invoice_September.zip'
    }
}

foreach ($language in 'ja', 'en') {
    $sample = $samples[$language]
    Start-Suite "StoreShots-$language"
    Set-Settings @{ Language = $language }

    $folder = Join-Path $output $language
    New-Item -ItemType Directory -Force -Path $folder | Out-Null

    $stored = @($sample.Main.Keys | Where-Object { $_ -match '\.(jpg|png|xlsx|pdf)$' })
    New-TestZip (Join-Path $script:Work $sample.MainName) $sample.Main $stored | Out-Null
    New-TestZip (Join-Path $script:Work $sample.RiskyName) $sample.Risky | Out-Null

    # タイトルバーには書庫のフルパスが出る。一時フォルダーのままだと Windows のユーザー名が写るので、
    # 作業フォルダーを空いているドライブ文字に割り当て、そこから開く
    $used = @(Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Name })
    $letter = @('S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z') | Where-Object { $used -notcontains $_ } | Select-Object -First 1
    & subst.exe "${letter}:" $script:Work
    try {
        $main = "${letter}:\$($sample.MainName)"
        $risky = "${letter}:\$($sample.RiskyName)"

        # 1 枚目: 書庫を開いたところ。ほかのソフトのアイコンが写らないよう、中には入らない
        # (一覧のアイコンはこの PC の関連付けから取るので、jpg などは入れたソフトの絵になる)
        $app = Start-Expzip @($main)
        Set-ShotSize $app
        Save-Shot $app (Join-Path $folder '1-browse.png')
        Stop-Expzip $app

        # 2 枚目: 怪しい書庫を開いたところ (警告の印)。3 枚目: その検査結果
        $app = Start-Expzip @($risky)
        Set-ShotSize $app
        Save-Shot $app (Join-Path $folder '2-warning.png')
        Push (ById $app.Window 'InspectButton')
        Wait-Idle $app 60000
        $window = Find-Window $app $sample.Inspection 30000
        if ($window) {
            Save-Shot $app (Join-Path $folder '3-inspection.png')
        }
        else {
            Write-Host '検査結果のウィンドウが出ませんでした'
        }
        Stop-Expzip $app -Force
    }
    finally {
        Stop-TestExpzip
        & subst.exe "${letter}:" /D
    }
}

Stop-TestExpzip
Write-Host ''
Write-Host "できました: $output"
