# Expzip

English | [日本語](README.ja.md)

An archiver for Windows. It lets you create, edit and extract ZIP and other archives
the same way you work in Explorer.

**No installation needed.** `Expzip.exe` runs on its own.
It does not change the registry, and it does not load any extra DLLs or plug-ins.

It is also available [on the Microsoft Store](https://apps.microsoft.com/detail/9NQ2QS67HJ4L). Feel free to use that version.

All the features listed below work, but they may change in the future.
Expzip has no relation to Explzh. It uses none of its code, and the formats it supports are different (see below).

## Supported formats

"Open" means looking inside and taking files out. "Modify" means changing an archive after it has been made.

| Format | Open | Create | Modify | Notes |
|---|:---:|:---:|:---:|---|
| ZIP | ○ | ○ | ○ | Changes: add, delete, rename, move inside the archive, create folders. Compression methods: Stored / Deflate / Deflate64 / BZip2 / LZMA / PPMd. ZIP64 (over 4GB or over 60,000 items in one archive) can also be read |
| Password-protected ZIP | ○ | ○ | ○ | Opens AES-256 / AES-192 / AES-128 and the old method (ZipCrypto). **New archives are encrypted with AES-256** |
| Nested archives | ○ | ○ | ○ | Selecting one opens it in its own tab. You can go down any number of levels, and saving your changes writes them back into the parent archive |
| Self-extracting archives (`.exe`) | ○ | ○ | × | Opens ones that contain ZIP or 7z (judged by the contents, not the name). Can be made from an open ZIP. **Finished ones are never modified**: breaking the program at the front would leave the recipient unable to extract them |
| Split files (`.001` `.002` …) | ○ | ○ | × | Opening `.001` opens the split files joined together, ready to extract. All the pieces must be present; if any are missing, Expzip tells you which numbers. Any file can be split, not only archives, and **a program to join the pieces is made at the same time**. Existing split files are not re-split |
| 7z | ○ | × | × | **Read-only**. Solid archives are supported. For password-protected 7z, only the list of contents is shown |
| tar / tar.gz / tar.bz2 / tar.xz | ○ | × | × | **Read-only.** `.tgz` `.tbz` `.tbz2` `.txz` are handled the same way |
| NSIS installers (`.exe`) | ○ | × | × | Not archives but installers; you can list the files inside and take them out |
| Installers bundled with WiX Burn (`.exe`) | ○ | × | × | Installers such as those for Python and .NET. You can list the MSI and other files inside and take them out |
| Installers containing an MSI (`.exe`) | ○ | × | × | Lists the MSI inside, and you can go into the files inside it |
| Installers containing a CAB (`.exe`) | ○ | × | × | Such as ones made with IExpress, which comes with Windows, and older driver installers. You can list the files inside and take them out |
| MSIX / AppX packages (`.msix` `.msixbundle` `.appx` `.appxbundle`) | ○ | × | × | **Read-only**. These are files for installing apps, but you can list the files inside and take them out |
| CAB (`.cab`) | ○ | × | × | **Read-only**. For a CAB split into several files, opening the first one reads the rest too |
| MSI installers (`.msi`) | ○ | × | × | **Read-only**. Lists the files under their real names and folders, and lets you take them out. The installer's own actions are not run |
| exe / dll | ○ | × | × | **Read-only**. Not archives, but you can list the icons, version information and other resources embedded in them and take them out. Icons come out as `.ico`, version information as readable text |
| RAR | × | × | × | Not supported (may be considered in the future) |
| LZH | × | × | × | Will not be supported |

**Japanese file names**

The character encoding of file names (Shift_JIS / CP932 and UTF-8) is **detected automatically**.
Japanese names in old archives are read correctly. Japanese names inside tar archives are handled the same way.

## Features

**Viewing**

- Two panes: a tree and a list. You can move around inside an archive as if it were a folder
- Name / size / compressed size / compression ratio / modified date. Click a column heading to sort
- Each part of the location bar at the top can be clicked. From a part you can also move into the folders inside it
- Several archives can be open at once in tabs. There is also a list of recently opened archives
- If another program changes an open archive, **Expzip reloads it automatically**

**Extracting**

- Extract the whole archive, the selected items, or a folder
- Expzip asks whether to overwrite **only when a file with the same name actually exists** at the destination
- Drag items straight from the archive to Explorer to take them out
- Double-clicking takes the file out temporarily and opens it in its default program
  - If you save it there, **Expzip asks whether to put the change back into the archive**
  - Files taken out temporarily are deleted when Expzip closes

**Creating and editing**

- Create a new ZIP, add files by dragging them in, delete items
- Rename (F2, or select and click again), move items inside the archive, create empty folders
- **Whether to compress is decided automatically for each file**. For example, photos and videos, which do not get smaller, are stored as they are
- Set, change and remove passwords

**Inspecting**

- **Archive inspection**. Checks in one go whether the archive is broken and whether it contains anything dangerous (see "Staying safe" below)
- Scans with your antivirus software through the Windows interface for it

**Splitting**

- Splits a large file into pieces of a size you choose. **A program to join them** is made at the same time,
  so the recipient can put the file back together without Expzip. Expzip also checks automatically that the result matches the original

**Other**

- The display language is Japanese or English. It can also follow the Windows display language
- The About window shows the full license texts of the software included in Expzip

**Using AI to work out an archive's rules (optional)**

Expzip can have an AI work out the rules behind "how this archive is put together" from a sample archive,
and then check whether archives you make follow those rules.

- **You decide whether to use it**. Nothing is sent to an AI unless you set up a connection and ask it to work out rules
- **Only file names and the folder structure are sent, never the contents of files.**
  You can see what will be sent on screen before it is sent
- You can review the rules it suggests in a list and apply only the ones you want

## How to use

1. Put `Expzip.exe` anywhere you like
2. Run it. You can open an archive by passing it as an argument, or by dragging it onto the window

The first time you run it, Windows may show "Windows protected your PC".
This is because the executable has no digital signature. Click "More info" and a "Run anyway" button appears.

Settings are saved in `Expzip.settings.json` in the same folder as `Expzip.exe`.
You can carry it around on a USB drive.

| Key | Action |
|---|---|
| Enter | Go into a folder / open a file |
| BackSpace | Go up one folder |
| F2 | Rename |
| Delete | Delete |
| Ctrl+Shift+N | New folder |
| Ctrl+S | Write a nested archive back into its parent |
| Ctrl+W | Close the tab |
| Ctrl+Tab | Next tab |
| F5 | Reload the archive |

## Visibility and screen readers

People see colors differently.
Some people use screen readers. Expzip is made to work for both.

- **The light and dark appearance switches to follow your Windows settings**. High contrast is also supported
- **Text colors that carry meaning are chosen by measuring their difference in brightness from the background**. Separate colors are kept for the light and dark appearance
- **Expzip simulates how colors look with different kinds of color vision and has confirmed that text can be told apart from the background.**
  (The method and results are in section 4.9 of the [specification](docs/SPEC.md) (Japanese))
- Danger, caution and no-problem are shown **with icons and words such as "Danger" and "Caution" as well as color**
- Items with unusual names get **a warning mark, not just a color**
- Password-protected items are shown **with a padlock mark, not just green text**. This is also conveyed in the tooltip and by screen readers
- **Buttons and list rows have names for screen readers**. Even buttons that show only an icon are announced by name
- The app icon can be told apart by differences in brightness rather than color

## Staying safe

Archives usually come from other people. **Their contents have to be treated as untrusted.**
Here is how Expzip deals with problems that are well known in archivers.

| Known problem | How Expzip handles it |
|---|---|
| **Tricks that write files outside the destination folder**. A name such as `..\..\Windows\…` is put in the archive, so that extracting it places a file somewhere unrelated | **Handled**. Such names are rejected up front, and right before writing Expzip checks again that the file stays inside the destination. The same check is made on **every path that takes files out**: ZIP, password-protected ZIP, 7z, tar and installers |
| **Tricks that use links (a kind of shortcut) to write somewhere else** | **Not affected**. Expzip never creates links, so this trick does not work |
| **The mark on files that came from the internet is lost**. Without the mark, Windows and app protections (SmartScreen, Protected View) do not kick in when the file is opened | **Handled**. The mark on the archive is carried over to the files taken out of it. This also applies when dragging files out and when making a self-extracting archive |
| **Archives that swell enormously when extracted**. A small archive grows tens of thousands of times when extracted and fills the disk | **Reported by the inspection**. It shows how many times larger the contents will be, so you notice before extracting |
| **Tricks that disguise contents by their names**. Fake extensions, characters that reverse the text direction to show an extension backwards, invisible characters in names, names that differ only in upper and lower case so that one overwrites the other | **Reported by the inspection**. The list also puts a warning mark on unusual names |
| **Names that Windows cannot create** (reserved names such as `CON`, names ending in a space or period, characters that are not allowed) | **Reported by the inspection**. This prevents extraction from failing halfway and leaving only part of the files |
| **Running something by mistake**. Double-clicking an `.exe` or `.bat` inside an archive runs it straight away | **Expzip asks before opening it**. The default answer is "Cancel" |
| **Viruses** | **Before extracting, Expzip checks the files through the Windows antivirus interface**. The antivirus software you have installed does the checking |
| **Extracting a broken archive without noticing** | **Checked by the inspection**. It verifies the contents, mismatches between the index and the actual data, archives cut off partway, extra data at the end, and duplicate names |
| **External DLLs or plug-ins are swapped out so that another program gets run**. This has happened again and again with archivers | **Cannot happen**. Expzip has no per-format DLLs or plug-ins; everything it needs is inside one executable. For reading CAB and MSI and for virus checks it uses components that come with Windows (`cabinet.dll` `msi.dll` `amsi.dll`), and it loads them only from the Windows system folder. It also does no installation and makes no registry changes |
| **Old encryption is easy to break** | Encryption is **AES-256**. Archives with the old encryption can still be read |

**What is not done yet** (listed openly)

- **The executable has no digital signature**. The first time you run it, Windows may show a warning (SmartScreen)
- Expzip does not check **whether the destination folder already contains a hidden route to somewhere else**
- **Files that are too large cannot be virus-checked**. In that case Expzip says they could not be checked
- **Passwords are remembered only while Expzip is running**. They are never saved to a file, but they do stay in memory while it runs
- Files taken out temporarily are deleted on exit, but **they cannot be deleted while another program still has them open**. Those are deleted the next time Expzip starts

The detailed reasoning is in the [security review report](docs/SECURITY_REPORT.md) (Japanese).
If you find a security problem, please do not post it in public Issues; report it as described in [how to report](.github/SECURITY.md) (Japanese).

## Requirements

- Windows 10 / 11 (64-bit)
- Nothing else is needed (no need to install .NET either)

## Building

On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then run the following
at the top of the repository. **Use `build.cmd`, not `dotnet build`**.
The `Expzip.dll` and small `Expzip.exe` that `dotnet build` produces are for development and do not run on their own.

```
build.cmd
```

This produces `publish\win-x64\Expzip.exe`. **This single file is what gets distributed.**

| Command | What it does |
|---|---|
| `build.cmd` | Makes the `Expzip.exe` for distribution |
| `build.cmd -Debug` | Only builds for development; does not make the file for distribution |

## Documents

- [Specification](docs/SPEC.md) (Japanese)
- [Security review report](docs/SECURITY_REPORT.md) (Japanese)
- [Privacy policy](PRIVACY.md#privacy-policy-english)

## Contact

- Please report bugs and requests in [Issues](https://github.com/p-studio-softworks/expzip/issues)
- If you do not have a GitHub account, or the matter should not be public, please send an email:
  p-studio-softworks@outlook.com
- For security problems, please do not post in public; report them as described in [how to report](.github/SECURITY.md) (Japanese)

## License

[MIT License](LICENSE)

`Expzip.exe` contains SharpCompress, SharpZipLib and the .NET Runtime.
Their copyright notices and permission notices are reproduced verbatim in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt),
and the same text is inside the executable
(**About → Licenses**).
