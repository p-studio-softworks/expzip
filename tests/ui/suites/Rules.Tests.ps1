# 書庫のルールの推定 (#25, #80) と、ほかの書庫への当てはめ (#27)。
# 推定の相手は、この PC の中に立てた偽の接続先。外へは何も出ない
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Rules'

# ファイルの中身にだけ書く合言葉。送った中に混ざっていないことを確かめる
$secret = 'NAKAMI-WA-OKURANAI'
$sample = New-TestZip (Join-Path $script:Work 'otehon.zip') ([ordered]@{
    'README.md'        = $secret
    'docs/'            = ''
    'docs/guide.md'    = $secret
    'src/main.c'       = $secret
})
$bad = New-TestZip (Join-Path $script:Work 'ihan.zip') ([ordered]@{
    'docs/'            = ''
    'docs/guide.md'    = 'x'
    'src/main.c'       = 'x'
    'src/cache.tmp'    = 'x'
})

$answer = @'
推定したルールです。

```json
{"rules":[
  {"kind":"required_entry","scope":"root","value":"README.md","description":"ルート直下に README.md がある","evidence":"ルート直下の一覧"},
  {"kind":"forbidden_extension","scope":"all","value":"tmp","description":".tmp を含めない","evidence":"拡張子の一覧に無い"},
  {"kind":"required_folder","scope":"root","value":"docs","description":"ルート直下に docs がある","evidence":"フォルダーの一覧"},
  {"kind":"required_entry","scope":"root","value":"CHANGELOG.md","description":"ルート直下に CHANGELOG.md がある","evidence":"思い付き"},
  {"kind":"banana","scope":"all","value":"x","description":"知らない種類","evidence":"-"}
]}
```
'@
$stub = Start-AiStub 8972 @((New-AiAnswer $answer))

function Open-AiItem($App, [string]$Item) {
    $menu = Open-DropDown $App 'AiButton'
    if ($null -eq $menu) { return $null }
    $entry = ByName $menu $Item
    if ($null -eq $entry -or -not $entry.Current.IsEnabled) {
        Close-DropDown $App
        return $entry
    }
    Push $entry 1200
    return $entry
}

function RuleRows($Dialog) {
    $list = ById $Dialog 'RuleList'
    return @($list.FindAll($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem)))
}

try {
    Set-Settings @{ AiEndpoint = $stub.Endpoint; AiModel = 'nise-model' }

    Section '推定の窓を開く'
    $app = Start-Expzip @($sample)
    $item = Open-AiItem $app '書庫のルールを推定…'
    Check '接続先と書庫が揃えば押せる' ($item -and $item.Current.IsEnabled)
    $dialog = Find-Window $app '書庫のルールを推定'
    Check '窓が開く' ($null -ne $dialog)

    $intro = (ById $dialog 'IntroText').Current.Name
    Check '何をするかを言う' ($intro -eq 'otehon.zip を「お手本」として、この書庫のルールを推定します。') $intro
    $payload = ValueOf (ById $dialog 'PayloadBox')
    Check '送るものを見せる' ($payload -match 'README\.md' -and $payload -match 'docs') "$($payload.Length) 文字"
    Check '中身は送るものに入っていない' ($payload -notmatch $secret)
    $size = (ById $dialog 'PayloadText').Current.Name
    Check '送る量を言う' ($size -match 'ファイルの中身は送信しません。' -and $size -match 'この書庫のすべての名前を送信します。合計 [\d,]+ バイトです。$') $size
    Check '口の名前' ((ById $dialog 'SendButton').Current.Name -eq '推定する' -and (ById $dialog 'SaveButton').Current.Name -eq '保存する' -and (ById $dialog 'CloseButton').Current.Name -eq '閉じる')
    Check 'まだ何も送っていない' ((Get-AiRequests $stub).Count -eq 0)

    Section '推定する'
    Push (ById $dialog 'SendButton')
    $result = Wait-Until -TimeoutMs 20000 {
        $text = (ById $dialog 'ResultText').Current.Name
        if ($text -match '推定しました|推定できませんでした') { $text }
    }
    Check '件数を言う' ($result -match '^3 件のルールを推定しました。') $result
    Check '除外したものも言う' ($result -match 'お手本の書庫が守っていないもの 1 件' -and $result -match 'ルールとして解釈できないもの 1 件を除外しました。') $result
    $notice = (ById $dialog 'NoticeText').Current.Name
    Check '提案だと断る' ($notice -eq 'これらはAIが推定したルールです。書庫の本来のルールとは異なる場合があります。ルールとして使用したいものを選択してください。') $notice
    $rows = RuleRows $dialog
    Check '一覧に並ぶ (除外したものも見せる)' ($rows.Count -eq 4) "$($rows.Count) 行"
    # 支援技術が読む行の名前。型の名前ではなくルールの説明を読む (#109)
    $ruleNames = @($rows | ForEach-Object { $_.Current.Name })
    Check '行の名前がルールの説明' (
        (-not ($ruleNames -match 'Expzip\.')) -and
        ($ruleNames -match '^ルート直下に README\.md がある$')) ($ruleNames -join ' / ')
    $sent = Get-AiRequests $stub
    Check '1 回だけ送る' ($sent.Count -eq 1) "$($sent.Count) 回"
    Check '送った中に中身が無い' ($sent.Count -ge 1 -and $sent[0].body -notmatch $secret)

    Section '選択したルールを削除'
    $delete = ById $dialog 'DeleteButton'
    Check '口の名前' ($delete.Current.Name -eq '選択したルールを削除') $delete.Current.Name
    Check '選ぶまでは押せない' (-not $delete.Current.IsEnabled)
    Check '押せない理由' ($delete.Current.HelpText -match '^削除するルールを選択してください。') $delete.Current.HelpText
    # 字が口に収まっていること。長くしたので、切れていないかを見る
    $label = ByType $delete $script:ControlType::Text | Select-Object -First 1
    if ($label) {
        $inner = $label.Current.BoundingRectangle
        $outer = $delete.Current.BoundingRectangle
        Check '字が口に収まる' (($inner.Left -ge $outer.Left) -and ($inner.Right -le $outer.Right)) ("字 {0:0} / 口 {1:0}" -f $inner.Width, $outer.Width)
    }
    $docs = $rows | Where-Object { (Texts $_) -match 'docs' } | Select-Object -First 1
    Select-Element $docs
    Check '選ぶと押せる' ((ById $dialog 'DeleteButton').Current.IsEnabled)
    Check '押したら何が起きるか' ((ById $dialog 'DeleteButton').Current.HelpText -match '^選択したルールを一覧から削除します。') (ById $dialog 'DeleteButton').Current.HelpText
    Push (ById $dialog 'DeleteButton')
    Check '消したと言う' ((ById $dialog 'ResultText').Current.Name -eq '1 件のルールを一覧から削除しました。保存するとファイルに反映されます。') (ById $dialog 'ResultText').Current.Name
    Check '一覧から消える' ((RuleRows $dialog).Count -eq 3)

    Section '保存する'
    Push (ById $dialog 'SaveButton')
    $saved = (ById $dialog 'ResultText').Current.Name
    Check '保存したと言う' ($saved -eq '2 件のルールを保存しました (使用するのは 2 件)。') $saved
    Check 'ファイルができる' (Test-Path $script:RulesPath)
    Push (ById $dialog 'CloseButton')
    Check '閉じる' (Test-WindowGone $app '書庫のルールを推定')
    Stop-Expzip $app

    Section 'ほかの書庫に当てはめる'
    $app = Start-Expzip @($bad)
    $item = Open-AiItem $app 'ルールに合っているか検査…'
    Check 'ルールがあれば押せる' ($item -and $item.Current.IsEnabled)
    $audit = Find-Window $app 'ルールの検査結果 - ihan.zip'
    Check '窓が開く' ($null -ne $audit)
    if ($audit) {
        $headline = (ById $audit 'Headline').Current.Name
        Check '見出し' ($headline -eq 'ルールに合っていない項目が 1 個あります。あるはずの項目が 1 個ありません') $headline
        $source = (ById $audit 'SourceLine').Current.Name
        Check 'どのルールで調べたか' ($source -match '^2 件のルールで検査しました \(お手本: otehon\.zip、\d{4}/\d{2}/\d{2} \d{2}:\d{2}\)。$') $source
        $findings = Texts (ById $audit 'FindingList')
        Check '違反の場所' ($findings -match 'cache\.tmp') $findings
        Check '足りないもの' ($findings -match 'README\.md' -and $findings -match '見つからない') $findings
        Check '保存したものしか使わない' ($findings -notmatch 'docs|CHANGELOG')
        # 支援技術が読む行の名前。種類・対象・内容をこの順に読む (#109)
        $rowNames = @((ById $audit 'FindingList').FindAll($script:Scope::Children,
            (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem)) |
            ForEach-Object { $_.Current.Name })
        Check '行の名前が型の名前でない' (-not ($rowNames -match 'Expzip\.')) ($rowNames -join ' / ')
        Check '行の名前に種類と対象と内容' (
            ($rowNames -match '^含めない拡張子、.*cache\.tmp、') -and
            ($rowNames -match '^見つからない、README\.md、')) ($rowNames -join ' / ')
        Push (ById $audit 'CloseButton')
    }
    Stop-Expzip $app
}
finally {
    Stop-AiStub $stub
}

Complete-Suite
