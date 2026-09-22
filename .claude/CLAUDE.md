# Working rules for Expzip

A Windows archiver. C# / .NET 10 / WPF, shipped as a single `Expzip.exe`.
The full specification is `docs/SPEC.md`. This file holds **only the rules to follow**, not the history behind them.

- **Talk to the user in Japanese.** This file is in English, but conversations, tickets, commit messages and every document in the repository are written in Japanese.
- This file lives in `.claude/` on purpose, to keep the repository top level for users (#129).

## Build and test

- Build with `build.cmd` at the repository root (`-Debug` for development / `-Test` to also run the UI tests). The script itself is `build/build.ps1`. Do not call `dotnet publish` directly
- UI tests are `tests/ui/Run-UiTests.ps1`. They launch and drive Expzip for real, so do not touch the mouse or keyboard while they run
- **When you change on-screen text, also update the expectations in `tests/ui/suites`.** The tests check the text
- **When you change on-screen text, regenerate the list with `dotnet run --project tools/stringdump` and commit it.**
  The diff of `tools/stringdump/strings.txt` shows whether any text changed that you did not mean to change
  (`-- --check` only checks for drift)
- **Run `build.cmd -Debug` right before stringdump** (#147). It reads the newest `Expzip.dll` under `src/Expzip/bin`.
  After a release build that is the release DLL, and the UI tests build somewhere else, so without a fresh Debug build
  it reads the old text and quietly reports no change. **Check that the diff contains the text you changed**

## File formats

- `.cs` `.xaml` `.csproj` `.ps1` `.md`: UTF-8 + CRLF. `.ps1` and `.cs` carry a BOM (PowerShell 5.1 misreads Japanese without one)
- `.cmd` is ASCII only (cmd.exe reads it in the ANSI code page, so Japanese text belongs in the `.ps1`)
- Bulk replacements must not break the BOM or the line endings

## Colors

- **When writing a `Style`, inherit the Fluent default with `BasedOn`.** Without it the control falls back to the old look and keeps a light background even in the dark theme. For lists with columns, inherit `GridViewItemContainerStyleKey`
- **When writing a `ControlTemplate`, also set the text color.** Otherwise the old default black remains and disappears on a dark background
- The color rules are in chapter 4.9 of `docs/SPEC.md`, including how to measure colors for different kinds of color vision

## On-screen text

- Text lives in the table in `Localization/Strings.Text.cs`, with Japanese and English side by side. **Never write it in XAML**
- Verbs for operations are written in kanji (入力する、設定する、削除する、作成する、選択する、使用する、展開する)
- Do not use developer jargon (断片 → 分割ファイル、置き場 → 保存先). **Write 「ビルド」, not 「建てる」**
- Use the words people actually say: 「ウィンドウ」, not 「窓」; 「バージョン」, not 「版」 (#142). This also applies to `README.md` and `THIRD-PARTY-NOTICES.txt`
- Put a half-width space between a number and its unit (`3 件`). Files and items are counted with 「個」; rules, violations and detections with 「件」
- Write the long vowel: `フォルダー` `ヘッダー`. Write `既に` in kanji
- Tooltips and the status bar do not end with 「。」. Sentences in dialogs do
- Do not explain internal reasons. **Keep warnings that matter for safety**
- Failure reasons go through `Strings.Reason` and are shown in Japanese. Never show an English exception message as is

## Attribution

- The copyright notice is `Copyright (c) <year> P studio`. **Never show a personal name**
- Commits are authored as `paliensup <193758124+paliensup@users.noreply.github.com>` (set for this repository only)
- **Do not add `Co-Authored-By` to commits.** GitHub would show the commit as co-authored

## Writing

- `README.md` is **for users**. Do not put developer instructions in it
- **Do not write 「色弱」 or 「色盲」.** Write 「色の見え方の違い」 or 「色覚特性」, and describe a type by how colors look,
  such as 「赤と緑の区別がつきにくい」. This also applies to tickets and commit messages
- The specification `docs/SPEC.md` describes **how things are now**. **Do not write plans or phases**
- **The issue tracker is private. Never link to it from the repository** (a bare number such as `#123` is fine)
- **Do not name other archivers as a point of comparison or as an origin** (#127). When a comparison is needed,
  use Explorer (エクスプローラー) or 7-Zip. The only exception is the one line in the README saying there is no relation.
  Details are in #127 in the issue tracker

## How to work

- Change files with Edit / Write. **Rewriting them with an external script makes the whole file get read again, which is wasteful**
- Only look at the lines of logs and output that you need (for example `Select-String 'NG'`)
