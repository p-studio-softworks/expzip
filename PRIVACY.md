# プライバシーポリシー

[English follows Japanese.](#privacy-policy-english)

Expzip (以下「このソフト」) が、利用者の情報をどう扱うかを説明します。
2026 年 9 月 29 日に定めました。

## 開発者に届くものはありません

このソフトは、**利用者の情報を開発者 (P studio) へ送りません。**
利用状況の送信、更新の確認、広告、アカウントの登録はありません。

## 外へ送るのは、AI による書庫のルールの推定を使うときだけです

このソフトがインターネットへ送信するのは、**AI による書庫のルールの推定**を使うときだけです。
この機能は任意で、利用者が接続先を設定し、推定させない限り何も送りません。

- **送り先**は、利用者が設定した AI の提供元です。開発者を経由しません
- **送るもの**は、見本の書庫の名前、フォルダーの構成、ファイルの名前 (数が多いときは一部)、
  拡張子ごとの数です。**ファイルの中身は送りません**
- **送る前に、送る内容をそのまま画面に表示します**
- 送ったものの扱いは、その提供元の規約に従います。**無料枠では、送ったものが提供元の製品改善に
  使われることがあります**

## この PC に保存するもの

- 設定 (ウィンドウの大きさ、表示言語、最近開いた書庫の一覧、AI の接続先など) と、推定したルール
  - `Expzip.exe` を直接置いて使う場合は、`Expzip.exe` と同じフォルダーに保存します
  - Microsoft Store から入れた場合は、Windows がこのソフト専用に用意する場所に保存し、
    アンインストールすると一緒に消えます
- AI の API キーは、**Windows の仕組み (DPAPI) で暗号化して**設定と一緒に保存します。
  その PC のその利用者でなければ元に戻せません
- 書庫のパスワードは保存しません。動いている間だけメモリー上に持ちます
- 書庫から一時的に取り出したファイルは、終了時に削除します

## ウイルス検査

書庫を検査するときや展開する前に、ファイルの中身を **Windows のウイルス対策の仕組み (AMSI)** に渡し、
この PC に入っているウイルス対策ソフトに調べさせます。
そのウイルス対策ソフトが情報をどう扱うかは、そのソフトの設定と規約に従います。

## Microsoft Store から入れた場合

入手やクラッシュについての情報は Microsoft が集め、開発者は Microsoft の画面 (Partner Center) で
その集計を見られます。扱いは [Microsoft のプライバシーに関する声明](https://privacy.microsoft.com/privacystatement) に従います。

## 変更

このポリシーを変えるときは、このページを書き換えます。変更の履歴は、このリポジトリの履歴に残ります。

## 連絡先

p-studio-softworks@outlook.com

---

# Privacy Policy (English)

This page explains how Expzip ("the app") handles your information.
Effective September 29, 2026.

## Nothing is sent to the developer

The app **does not send any information about you to the developer (P studio).**
There is no usage reporting, update checking, advertising or account.

## The only data sent out is for AI rule estimation

The app sends data over the internet **only when you use AI rule estimation.**
This feature is optional. Nothing is sent unless you set up a provider and ask it to estimate rules.

- **Recipient:** the AI provider you set up. Nothing passes through the developer
- **What is sent:** the name of the sample archive, its folder structure, file names
  (only some of them when there are many) and the number of files per extension.
  **File contents are never sent**
- **The app shows exactly what will be sent before sending it**
- What the provider does with it is governed by the provider's terms.
  **On a free tier, what you send may be used to improve the provider's products**

## What is stored on this PC

- Settings (window size, display language, recently opened archives, AI provider and so on) and the estimated rules
  - When you run `Expzip.exe` directly, they are stored in the same folder as `Expzip.exe`
  - When installed from the Microsoft Store, they are stored in a location Windows provides for the app,
    and are removed when the app is uninstalled
- The AI API key is **encrypted with a Windows feature (DPAPI)** and stored with the settings.
  Only the same user on the same PC can decrypt it
- Archive passwords are not stored. They are kept in memory only while the app is running
- Files extracted temporarily from archives are deleted when the app exits

## Virus scanning

When inspecting an archive or before extracting, the app passes file contents to
**the Windows antimalware interface (AMSI)** so that the antivirus software installed on this PC can scan them.
How that antivirus software handles information is governed by its own settings and terms.

## When installed from the Microsoft Store

Microsoft collects information about acquisitions and crashes, and the developer can see aggregated reports
in Microsoft's Partner Center. This is governed by the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).

## Changes

When this policy changes, this page is updated. The history of changes is kept in this repository.

## Contact

p-studio-softworks@outlook.com
