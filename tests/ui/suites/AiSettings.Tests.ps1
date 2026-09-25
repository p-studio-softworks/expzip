# AI連携の設定 (#24)。接続テストは、この PC の中に立てたテスト用の接続先に対して行う
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'AiSettings'
Set-Settings
$key = 'test-key-not-real-0123456789'

$stub = Start-AiStub 8971 @(
    (New-AiAnswer 'pong'),
    @{ Status = 401; Body = '{"error":{"message":"Invalid API key"}}' },
    # Azure OpenAI と同じ断り方 (#159)
    @{ Status = 400; Body = '{"error":{"message":"Unsupported parameter: ''max_tokens'' is not supported with this model. Use ''max_completion_tokens'' instead.","type":"invalid_request_error","param":"max_tokens","code":"unsupported_parameter"}}' },
    (New-AiAnswer 'pong')
)

function Open-AiSettings($App) {
    $menu = Open-DropDown $App 'AiButton'
    if ($null -eq $menu) { return $null }
    Push (ByName $menu 'AI連携の設定…') 1200
    return Find-Window $App 'AI連携の設定'
}

# 前の結果が残っている間につかまないよう、字が変わるまで待つ。
# 続けて同じ結果になる並べ方にはしていない
function Run-Test($Dialog) {
    $before = (ById $Dialog 'ResultText').Current.Name
    Push (ById $Dialog 'TestButton')
    return Wait-Until -TimeoutMs 20000 {
        $text = (ById $Dialog 'ResultText').Current.Name
        if ($text -and $text -ne $before -and $text -notmatch 'テストしています') { $text }
    }
}

try {
    Section '開いたとき'
    $app = Start-Expzip
    $dialog = Open-AiSettings $app
    Check '窓が開く' ($null -ne $dialog)
    Check '何の設定か' ((ById $dialog 'IntroText').Current.Name -eq '書庫のルールをAIに推定させる機能の設定です。') (ById $dialog 'IntroText').Current.Name
    $privacy = (ById $dialog 'PrivacyText').Current.Name
    Check '中身は送らないと断る' ($privacy -match 'ファイルの中身は送信しません。') $privacy
    Check '鍵の置き場を断る' ($privacy -match 'APIキーは本アプリケーションと同じフォルダーに保存されますが、移動した場合には無効になります。')
    Check '口の名前' (((ById $dialog 'TestButton').Current.Name -eq '接続テスト') -and ((ById $dialog 'SaveButton').Current.Name -eq '保存') -and ((ById $dialog 'CancelButton').Current.Name -eq 'キャンセル'))
    Check '空のうちはテストできない' (-not (ById $dialog 'TestButton').Current.IsEnabled)

    Section '候補を選ぶ'
    $combo = ById $dialog 'PresetCombo'
    Expand-Element $combo
    $google = ByName $combo 'Google (Gemini)'
    Check '候補に並ぶ' ($null -ne $google)
    if ($google) { Select-Element $google }
    Check '接続先が入る' ((ValueOf (ById $dialog 'EndpointBox')) -match 'generativelanguage\.googleapis\.com') (ValueOf (ById $dialog 'EndpointBox'))

    Section '接続テスト'
    Set-Text (ById $dialog 'EndpointBox') $stub.Endpoint
    Set-Text (ById $dialog 'ModelBox') 'nise-model'
    Check '手で入れると「その他」になる' ((Get-Selected $combo) -eq 'その他') (Get-Selected $combo)
    Check '揃うとテストできる' ((ById $dialog 'TestButton').Current.IsEnabled)
    $keyBox = ById $dialog 'KeyBox'
    $keyBox.SetFocus()
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($key)
    Start-Sleep -Milliseconds 400

    $result = Run-Test $dialog
    Check '繋がったと言う' ($result -eq '接続しました。nise-model が使えます。') $result
    $sent = Get-AiRequests $stub
    Check '鍵を付けて送る' ($sent.Count -ge 1 -and $sent[0].auth -eq "Bearer $key")
    Check 'モデル名を送る'($sent.Count -ge 1 -and $sent[0].body -match '"model":\s*"nise-model"')

    $result = Run-Test $dialog
    Check '断られたらそう言う' ($result -match '^接続先から拒否されました \(HTTP 401\)。') $result
    Check '相手の理由を添える' ($result -match 'Invalid API key') $result

    Set-Text (ById $dialog 'EndpointBox') 'http://127.0.0.1:8979/v1'
    $result = Run-Test $dialog
    # 英語の例外文ではなく、何を確かめればよいかを日本語で言う
    Check '繋がらなければそう言う' ($result -eq '接続先に接続できませんでした。URL とネットワークを確認してください。') $result

    # 新しいモデルは上限を max_completion_tokens で求める。断られたら、その名前で送り直す (#159)
    Set-Text (ById $dialog 'EndpointBox') $stub.Endpoint
    $result = Run-Test $dialog
    Check '上限の名前で断られても繋がる' ($result -eq '接続しました。nise-model が使えます。') $result
    $sent = Get-AiRequests $stub
    Check 'はじめは max_tokens で送る' ($sent.Count -ge 3 -and $sent[2].body -match '"max_tokens":' -and $sent[2].body -notmatch 'max_completion_tokens') $(if ($sent.Count -ge 3) { $sent[2].body })
    Check '送り直しは max_completion_tokens' ($sent.Count -eq 4 -and $sent[3].body -match '"max_completion_tokens":' -and $sent[3].body -notmatch '"max_tokens"') $(if ($sent.Count -ge 4) { $sent[3].body })

    Push (ById $dialog 'SaveButton') 1200
    Check '保存すると閉じる' (Test-WindowGone $app 'AI連携の設定')
    Check 'ステータスバーに結果' ((Get-Status $app) -eq 'AI連携の設定を保存しました') (Get-Status $app)
    Stop-Expzip $app

    Section '設定ファイル'
    $raw = Read-Settings
    $json = $raw | ConvertFrom-Json
    Check '接続先が残る' ($json.AiEndpoint -eq $stub.Endpoint) $json.AiEndpoint
    Check 'モデル名が残る'($json.AiModel -eq 'nise-model') $json.AiModel
    Check '鍵は守られた形で残る' ($json.AiApiKeyProtected.Length -gt 40) "$($json.AiApiKeyProtected.Length) 文字"
    Check '鍵がそのまま書かれていない' ($raw -notmatch [regex]::Escape($key))

    Section '開き直す'
    $app = Start-Expzip
    $dialog = Open-AiSettings $app
    Check '接続先が戻る' ((ValueOf (ById $dialog 'EndpointBox')) -eq $stub.Endpoint)
    Check 'モデル名が戻る'((ValueOf (ById $dialog 'ModelBox')) -eq 'nise-model')
    Push (ById $dialog 'CancelButton')
    Check 'キャンセルで閉じる' (Test-WindowGone $app 'AI連携の設定')

    Section '英語'
    $menu = Open-DropDown $app 'LanguageButton'
    Push (ByName $menu 'English') 1200
    $menu = Open-DropDown $app 'AiButton'
    Push (ByName $menu 'AI settings...') 1200
    $dialog = Find-Window $app 'AI settings'
    Check '見出しが英語' ($null -ne $dialog)
    if ($dialog) {
        Check '中身も英語' ((Texts $dialog) -notmatch '[ぁ-んァ-ン]') (((Texts $dialog) -split ' \| ' | Where-Object { $_ -match '[ぁ-んァ-ン]' }) -join ', ')
        Push (ById $dialog 'CancelButton')
    }
    Stop-Expzip $app
}
finally {
    Stop-AiStub $stub
}

Complete-Suite
