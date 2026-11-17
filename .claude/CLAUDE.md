# Working rules for Expzip

A Windows archiver. C# / .NET 10 / WPF, shipped as a single `Expzip.exe`.
The full specification is `docs/SPEC.md`. This file holds **only the rules to follow**, not the history behind them.

- **Talk to the user in Japanese.** This file is in English, but conversations, tickets, commit messages and every document in the repository are written in Japanese.
  The exceptions are `README.md` and `THIRD-PARTY-NOTICES.txt`, which are in English (#198).
  **Write progress updates and reports in Japanese too.** Once one short update slips into English, the rest tends to follow
- This file lives in `.claude/` on purpose, to keep the repository top level for users (#129).

## Shared rules

Rules that apply to every application are kept in the private studio repository (#385). **Open them with the Read tool before the work they cover.**

- **Before writing on-screen text, documents, comments, tickets or commit messages**, read 「どのアプリにも共通の書き方」 in `~/.claude/roles/wording.md`. The rules below are only the ones specific to Expzip
- **Before designing anything that writes, replaces or deletes the user's files**, read 「どのアプリにも共通の決まり」 in `~/.claude/roles/security.md`
- **Before editing `docs/SPEC.md`**, read the paragraph on what a specification describes, just before 「始める前に」 in `~/.claude/roles/spec.md`
- How to change files (Edit / Write, no `sed -i`, keeping the BOM and line endings, reading only the log lines you need) is in `claude-settings/editing.md` in the studio repository, loaded from `~/.claude/CLAUDE.md`
- Which reviewer to ask for which change, and how the security design check goes, is in 「係に頼む場面」 of `agents/README.md` in the studio repository
- Finishing a change (commit, push, closing the ticket) follows the `finish-ticket` skill

## Build and test

- Build with `build.cmd` at the repository root (`-Debug` for development / `-Test` to also run the UI tests). The script itself is `build/build.ps1`. Do not call `dotnet publish` directly
- **Never release a build whose revision shows 「変更あり」**, on GitHub Releases or the Microsoft Store (#176, #180).
  The source can no longer be reproduced, so reports against it cannot be investigated.
  Commit first, then build without touching any file. `build.cmd -Msix` refuses to run with uncommitted changes
- Core tests are `tests/core/Run-CoreTests.ps1` (#332). They call `Expzip.Core` directly without the UI, take under a minute, and the PC stays usable. Run them first; `build.cmd -Test` stops before the UI tests if they fail.
  To check write ordering or races, set `AtomicFile.Trace` through `TraceLog` and keep such tests in `TraceCollection`
- UI tests are `tests/ui/Run-UiTests.ps1`. They launch and drive Expzip for real, so do not touch the mouse or keyboard while they run
- **When you change on-screen text, also update the expectations in `tests/ui/suites`.** The tests check the text
- **When you change on-screen text, regenerate the list with `dotnet run --project tools/stringdump` and commit it.**
  The diff of `tools/stringdump/strings.txt` shows whether any text changed that you did not mean to change
  (`-- --check` only checks for drift)
- **Run `build.cmd -Debug` right before stringdump** (#147). It reads the newest `Expzip.Core.dll` under `src/Expzip/bin`.
  After a release build that is the release DLL, and the UI tests build somewhere else, so without a fresh Debug build
  it reads the old text and quietly reports no change. **Check that the diff contains the text you changed**

## File formats

- `.cs` `.xaml` `.csproj` `.ps1` `.md`: UTF-8 + CRLF. `.ps1` and `.cs` carry a BOM (PowerShell 5.1 misreads Japanese without one)
- `.cmd` is ASCII only (cmd.exe reads it in the ANSI code page, so Japanese text belongs in the `.ps1`)

## Colors

- **When writing a `Style`, inherit the Fluent default with `BasedOn`.** Without it the control falls back to the old look and keeps a light background even in the dark theme. For lists with columns, inherit `GridViewItemContainerStyleKey`
- **When writing a `ControlTemplate`, also set the text color.** Otherwise the old default black remains and disappears on a dark background
- The color rules are in chapter 4.9 of `docs/SPEC.md`, including how to measure colors for different kinds of color vision

## On-screen text

- Text lives in the table in `src/Expzip.Core/Localization/Strings.Text.cs`, with Japanese and English side by side. **Never write it in XAML**
- **For the Zone.Identifier mark, in on-screen text and in `README.md` / `README.ja.md` write 「インターネットから入手したことを示す情報 (Mark of the Web)」 the first time in each message, and 「Mark of the Web」 after that (#351).** In English: "the Mark of the Web, which shows that … came from the internet", then "the Mark of the Web".
  Everywhere users do not see (comments, the specification, other documents such as `tools/*/README.md`, tests, commit messages, tickets and the wiki), write 「MotW」. Do not call it 「印」 or 「出所の印」: users do not know that name, and 「印」 also means the marks on list rows and in the tree
- Failure reasons go through `Strings.Reason` and are shown in Japanese. Never show an English exception message as is

## Attribution

- Commits are authored as `paliensup <193758124+paliensup@users.noreply.github.com>` (set for this repository only)

## Writing

- `README.md` (English) and `README.ja.md` (Japanese) are **for users**. Do not put developer instructions in them
- **When you change one README, change the other in the same commit** (#198). The English one is a direct translation of the Japanese one, not a summary.
  Links to documents that exist only in Japanese get "(Japanese)" after them in the English README
- **`THIRD-PARTY-NOTICES.txt` is English only** (#198). It is shown as is in the license window whatever the UI language,
  so Japanese text in it shows up for English users. The permission notices themselves are copied verbatim, never translated
- **Do not name other archivers as a point of comparison or as an origin** (#127). When a comparison is needed,
  use Explorer (エクスプローラー) or 7-Zip. The only exception is the one line in each README saying there is no relation.
  Details are in #127 in the issue tracker

## How to work

- **Before implementing a ticket that changes archive reading, writing, extraction, startup, the input passed to third-party libraries, or what is sent to AI, have `security-reviewer` check the design (#294).** The steps are in 「係に頼む場面」 (see Shared rules)
- **A failure must leave the user's files as they were (#320).** Follow 「どのアプリにも共通の決まり」 in `~/.claude/roles/security.md`, using `AtomicFile` to write to a temporary name and replace in one step
