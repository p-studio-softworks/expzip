using System.IO;
using System.IO.Compression;
using Expzip.Archives;
using Expzip.Inspection;

namespace Expzip.Localization;

/// <summary>
/// 画面に出す文言の本体 (#23)。
/// </summary>
/// <remarks>
/// 仕組みは <c>Strings.cs</c> にある。ここは日本語と英語を並べるだけに保つ。
/// 並び順は画面の上から下、操作の流れの順。訳を直すときに元の文と
/// 見比べられるよう、1つの文言につき1つのメンバーにしてある。
/// </remarks>
internal static partial class Strings
{
    // ------------------------------------------------------------------ ツールバー

    public static string Open => Pick("開く", "Open");

    public static string OpenTooltip => Pick("書庫を開く", "Open an archive");

    public static string RecentTooltip => Pick("最近開いた書庫", "Recently opened archives");

    public static string Extract => Pick("展開", "Extract");

    public static string ExtractTooltip => Pick(
        "選択した項目を展開する。選択していなければ書庫全体を展開する",
        "Extract the selected items. With nothing selected, extracts the whole archive.");

    public static string Add => Pick("追加", "Add");

    public static string AddTooltip => Pick(
        "ファイルを追加する",
        "Add files to the archive");

    public static string Password => Pick("パスワード", "Password");

    public static string PasswordTooltip => Pick(
        "パスワードを設定・変更・削除",
        "Set, change, or remove the password");

    // 歯車ひとつに言語と AI が同居していたのをやめた (#104)。
    // 性質の違うものが1つの口に入っていると、何が出てくるのか開くまで分からない

    public static string AiMenu => Pick("AI機能", "AI features");

    public static string AiTooltip => Pick(
        "AI機能の設定",
        "AI feature settings");

    public static string LanguageTooltip => Pick(
        "言語設定", "Language settings");

    // ------------------------------------------------------------------ 言語の切り替え (#23)

    public static string LanguageMenu => Pick("言語", "Language");

    public static string LanguageAuto => Pick("Windows の表示言語に合わせる", "Match Windows");

    /// <summary>言語の名前は、その言語で書く。探している人が読める形にするため。</summary>
    public static string LanguageJapanese => "日本語";

    public static string LanguageEnglish => "English";

    // ------------------------------------------------------------------ バージョン情報 (#104)

    public static string AboutTitle => Pick("バージョン情報", "About");

    public static string AboutTooltip => Pick("Expzip について", "About Expzip");

    /// <summary>版と、動いている側の作り。x64 の exe を x86 の窓で見ることはない。</summary>
    public static string AboutVersion(string version, string architecture) => Pick(
        $"バージョン {version} ({architecture})",
        $"Version {version} ({architecture})");

    /// <summary>ビルドしたときのコミット。手元の木と突き合わせるためのもの。</summary>
    public static string AboutRevision(string revision) => Pick(
        $"リビジョン {revision}", $"Revision {revision}");

    /// <summary>
    /// そのコミットから手を入れた木でビルドした場合 (#104)。
    /// </summary>
    /// <remarks>
    /// **黙って番号だけ出さない。**そのコミットを取り寄せても同じものにならないため、
    /// 突き合わせる人が嘘の手掛かりを追うことになる。
    /// </remarks>
    public static string AboutRevisionModified(string revision) => Pick(
        $"リビジョン {revision} (変更あり)", $"Revision {revision} (modified)");

    public static string AboutRevisionUnknown => Pick(
        "リビジョン 不明", "Revision unknown");

    public static string AboutPlatform(string os, string runtime) => Pick(
        $"{os} / {runtime}", $"{os} / {runtime}");

    /// <summary>絵の置き場に添える説明 (#104)。空の枠が何なのか分からないままにしない。</summary>
    public static string AboutIconLater => Pick(
        "Expzipアイコン", "Expzip icon");

    public static string AboutClose => Pick("閉じる", "Close");

    // ------------------------------------------------------------------ ライセンス表示 (#105)

    public static string AboutLicense => Pick("ライセンス", "Licenses");

    public static string LicenseTitle => Pick(
        "ライセンス表示", "Third-Party Notices");

    /// <summary>組み立てを間違えたときだけ出る。黙って空にはしない。</summary>
    public static string LicenseMissing(string resource) => Pick(
        "ライセンス表示を読み出せませんでした。",
        "Could not read the third-party notices.");

    // ------------------------------------------------------------------ アドレスバーとタブ

    /// <summary>
    /// 畳んだ区切りを出す口 (#90)。
    /// </summary>
    /// <remarks>
    /// **「場所」の見出しは外した。**エクスプローラーのアドレスバーに見出しは無い。
    /// いまの場所はタイトルバーに出るので、見出しが無くて分からなくなることもない。
    /// </remarks>
    public static string LocationHidden => Pick(
        "隠れている場所", "Hidden parts of the path");

    /// <summary>区切りの口 (#90)。押すと、その場所の中にあるフォルダが並ぶ。</summary>
    public static string LocationInside(string name) => Pick(
        $"{name} の中", $"Inside {name}");

    public static string NewTabTooltip => Pick(
        "新しい書庫を作成して開く", "Create and open a new archive");

    public static string NewTabName => Pick("新しい書庫", "New archive");

    public static string CloseTabTooltip => Pick(
        "このタブを閉じる (Ctrl+W)", "Close this tab (Ctrl+W)");

    // ------------------------------------------------------------------ 一覧の列と右クリック

    public static string ColumnName => Pick("名前", "Name");

    public static string ColumnSize => Pick("サイズ", "Size");

    public static string ColumnCompressed => Pick("圧縮後", "Packed");

    public static string ColumnRatio => Pick("圧縮率", "Ratio");

    public static string ColumnDate => Pick("更新日時", "Modified");

    public static string MenuOpen => Pick("開く", "Open");

    public static string MenuRename => Pick("名前の変更", "Rename");

    public static string MenuDelete => Pick("削除", "Delete");

    public static string MenuNewFolder => Pick("新しいフォルダー", "New folder");

    /// <summary>書庫を読み直す (#64)。エクスプローラーと同じ文言にする。</summary>
    public static string MenuRefresh => Pick("最新の情報に更新", "Refresh");

    // ------------------------------------------------------------------ ステータスバー

    public static string NoArchiveOpen => Pick("書庫が開かれていません", "No archive is open");

    public static string EmptyFolder => Pick("このフォルダーは空です", "This folder is empty");

    public static string Stop => Pick("中断", "Stop");

    public static string SelectionNone => Pick("選択 0 個", "0 selected");

    public static string Selection(int count, long totalBytes) => Pick(
        $"選択 {count:N0} 個 ({totalBytes:N0} バイト)",
        $"{count:N0} selected ({totalBytes:N0} bytes)");

    public static string FileCount(int count, string limits) => Pick(
        $"{count:N0} 個のファイル{limits}",
        $"{count:N0} {Plural(count, "file", "files")}{limits}");

    public static string TotalSize(long total, long compressed) => Pick(
        $"合計 {total:N0} バイト (圧縮後 {compressed:N0} バイト)",
        $"{total:N0} bytes total ({compressed:N0} bytes packed)");

    public static string SuspiciousCount(int count) => Pick(
        $"パスが通常と異なる項目が {count:N0} 個あります",
        $"{count:N0} {Plural(count, "entry has", "entries have")} an unusual path");

    /// <summary>書庫にできることの断り書き。件数の後ろに添える。</summary>
    public static string LimitEncryptedAes => Pick(" (パスワード付き / AES)", " (password protected / AES)");

    public static string LimitEncrypted => Pick(" (パスワード付き)", " (password protected)");

    public static string LimitReadOnly(string format) => Pick(
        $" ({format} は読み取りのみに対応)", $" ({format} is read-only)");

    /// <summary>自己解凍書庫であることの断り書き (#32)。</summary>
    /// <remarks>
    /// 形式名では言えない。ZIP そのものは書き換えられるが、前に取り出すプログラムが
    /// 付いている状態では書き換えない、という話のため。
    /// </remarks>
    public static string LimitSelfExtracting => Pick(
        " (自己解凍書庫 / 読み取りのみ)", " (self-extracting / read-only)");

    /// <summary>分割された書庫であることの断り書き (#61)。</summary>
    public static string LimitSplit(int count) => Pick(
        $" (分割された書庫 / {count:N0} 個の分割ファイル / 読み取りのみ)",
        $" (split archive / {count:N0} volumes / read-only)");

    /// <summary>断片が揃っていない場合 (#61)。</summary>
    public static string SplitVolumeMissing(string name) => Pick(
        $"分割ファイルが揃っていません。{name} が見つかりません。",
        $"The split archive is incomplete. {name} is missing.");

    /// <summary>書庫の中の書庫であることの断り書き (#30)。</summary>
    public static string LimitInside(string parentName) => Pick(
        $" ({parentName} の中)", $" (inside {parentName})");

    public static string EncryptedTooltip => Pick(
        "この項目はパスワードで保護されています。展開するときにパスワードを入力してください。",
        "This item is protected with a password. You will be asked for it when extracting.");

    public static string SuspiciousPathTooltipLine1 => Pick(
        "このパスは通常の書庫では使われない形式です。",
        "This path is not in a form normally used by archives.");

    public static string SuspiciousPathTooltipLine2 => Pick(
        "展開しても、選択したフォルダーの外には書き込まれません。",
        "Extracting it will not write anything outside the folder you choose.");

    public static string SuspiciousPathTooltip
        => SuspiciousPathTooltipLine1 + Environment.NewLine + SuspiciousPathTooltipLine2;

    // ------------------------------------------------------------------ 最近使った書庫 (#10)

    public static string RecentEmpty => Pick("(履歴はありません)", "(No history)");

    /// <summary>下線の付いた文字がアクセスキーになる。</summary>
    public static string ClearRecent => Pick("履歴を消去(_C)", "_Clear history");

    public static string RecentMissing(string path) => Pick(
        $"ファイルが見つかりませんでした。{Environment.NewLine}{Environment.NewLine}{path}"
        + $"{Environment.NewLine}{Environment.NewLine}履歴から削除します。",
        $"The file was not found.{Environment.NewLine}{Environment.NewLine}{path}"
        + $"{Environment.NewLine}{Environment.NewLine}It will be removed from the history.");

    // ------------------------------------------------------------------ 圧縮方式 (#11)

    // ------------------------------------------------------------------ 書庫の形式 (#19)

    public static string FormatUnknown => Pick("不明", "Unknown");

    /// <summary>NSIS 製インストーラーとして読めなかったときの断り (#68)。</summary>
    public static string NsisNotSupported => Pick(
        "このインストーラーには対応していません。",
        "This installer is not supported.");

    /// <summary>「開く」ダイアログの絞り込み。</summary>
    /// <remarks>
    /// 自己解凍書庫 (#32) は名前が .exe なので、書庫ファイルの組にも入れる。
    /// 書庫でない .exe を選んだ場合は、開いた時点で分かる。
    /// </remarks>
    public static string OpenFilter => Pick(
        "書庫ファイル|*.zip;*.7z;*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz;*.exe;*.001"
        + "|ZIP書庫 (*.zip)|*.zip"
        + "|7z書庫 (*.7z)|*.7z"
        + "|tar書庫 (*.tar;*.tar.gz;*.tar.bz2;*.tar.xz)|*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz"
        + "|自己解凍書庫 (*.exe)|*.exe"
        + "|分割された書庫 (*.001)|*.001"
        + "|すべてのファイル (*.*)|*.*",
        "Archive files|*.zip;*.7z;*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz;*.exe;*.001"
        + "|ZIP archives (*.zip)|*.zip"
        + "|7z archives (*.7z)|*.7z"
        + "|tar archives (*.tar;*.tar.gz;*.tar.bz2;*.tar.xz)|*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz"
        + "|Self-extracting archives (*.exe)|*.exe"
        + "|Split archives (*.001)|*.001"
        + "|All files (*.*)|*.*");

    public static string ZipFilter => Pick("ZIP書庫 (*.zip)|*.zip", "ZIP archives (*.zip)|*.zip");

    // ------------------------------------------------------------------ 自己解凍書庫の作成 (#29)

    public static string Sfx => Pick("自己解凍", "Self-extract");

    public static string SfxTooltip => Pick(
        "自己解凍書庫として書き出す",
        "Save as a self-extracting archive");

    public static string SfxDialogTitle => Pick(
        "自己解凍書庫として保存", "Save as a self-extracting archive");

    public static string SfxFilter => Pick(
        "自己解凍書庫 (*.exe)|*.exe", "Self-extracting archives (*.exe)|*.exe");

    public static string SfxCreating => Pick(
        "自己解凍書庫を作成しています…", "Creating the self-extracting archive...");

    public static string SfxDone(string path, long length) => Pick(
        $"{path} を作成しました ({length:N0} バイト)",
        $"Created {path} ({length:N0} bytes)");

    public static string SfxSameFile => Pick(
        "元の書庫と同じファイル名では保存できません。",
        "It cannot be saved over the archive it is made from.");

    public static string SfxCancelled => Pick(
        "自己解凍書庫の作成を中断しました", "Stopped creating the self-extracting archive");

    public static string SfxFailed(string path, string reason) => Pick(
        $"{path} を作成できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Could not create {path}.{Environment.NewLine}{Environment.NewLine}{reason}");

    /// <summary>自己解凍書庫にできない理由 (#29)。作る前に断る。</summary>
    public static string SfxRejected(SfxRejection reason) => reason switch
    {
        SfxRejection.NotZip => Pick(
            "自己解凍書庫にできるのは ZIP のみです。",
            "Only ZIP archives can be made self-extracting."),
        SfxRejection.AlreadySelfExtracting => Pick(
            "この書庫は既に自己解凍書庫です。",
            "This archive is already self-extracting."),
        SfxRejection.Encrypted => Pick(
            "パスワードが設定された書庫は自己解凍書庫にできません。",
            "A password-protected archive cannot be made self-extracting."),
        SfxRejection.TooMany => Pick(
            "この書庫は項目が多すぎるため、自己解凍書庫にできません。",
            "This archive has too many entries to be made self-extracting."),
        _ => Pick(
            "この書庫は大きすぎるため、自己解凍書庫にできません。",
            "This archive is too large to be made self-extracting."),
    };

    public static string AllFilesFilter => Pick(
        "すべてのファイル (*.*)|*.*", "All files (*.*)|*.*");

    // ------------------------------------------------------------------ 書庫を開く・作る

    public static string OpenDialogTitle => Pick("書庫を開く", "Open archive");

    public static string NewArchiveDialogTitle => Pick("新しい書庫を作成", "Create a new archive");

    public static string NewArchiveFileName => Pick("新しい書庫.zip", "New archive.zip");

    public static string CreateArchiveFailed(string path, string reason) => Pick(
        $"{path} を作成できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Could not create {path}.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string Reading(string fileName, int done, int total) => Pick(
        $"{fileName} を読み込んでいます… ({done:N0} / {total:N0} 件)",
        $"Reading {fileName}… ({done:N0} / {total:N0})");

    public static string ReadCancelled => Pick("読み込みを中断しました", "Reading was stopped");

    public static string OpenArchiveFailed(string path, string reason) => Pick(
        $"{path} を開けませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Could not open {path}.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string EncryptedEntriesNotice => Pick(
        $"この書庫には暗号化されたファイルが含まれています。{Environment.NewLine}{Environment.NewLine}"
        + "一覧は表示できますが、これらのファイルの展開には対応していません。",
        $"This archive contains encrypted files.{Environment.NewLine}{Environment.NewLine}"
        + "The list can be shown, but extracting those files is not supported.");

    // ------------------------------------------------------------------ 名前の変更 (#15)

    public static string RenameSeparatorNotAllowed => Pick(
        "名前に \\ と / は使用できません。",
        "A name cannot contain \\ or /.");

    public static string RenameReservedName => Pick(
        "その名前は使用できません。", "That name cannot be used.");

    public static string RenameInvalidCharacter(char invalid) => Pick(
        $"名前に使用できない文字 ({invalid}) が含まれています。",
        $"The name contains a character that cannot be used ({invalid}).");

    public static string RenameDuplicate(string name) => Pick(
        $"このフォルダーには既に「{name}」が存在します。",
        $"This folder already contains \"{name}\".");

    public static string Renaming(int done) => Pick(
        $"名前を変更しています… ({done:N0} 個)",
        $"Renaming… ({done:N0})");

    public static string RenameFailed(string reason) => Pick(
        $"名前を変更できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The item could not be renamed.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string RenameCancelled => Pick("名前の変更を中断しました", "Renaming was stopped");

    public static string RenameDone(int count) => Pick(
        $"{count:N0} 個の項目の名前を変更しました",
        $"Renamed {count:N0} {Plural(count, "item", "items")}");

    // ------------------------------------------------------------------ フォルダの作成 (#50)

    /// <summary>新しく作るフォルダの仮の名前。エクスプローラーに合わせる。</summary>
    public static string NewFolderName => Pick("新しいフォルダー", "New folder");

    public static string CreatingFolder => Pick("フォルダーを作成しています…", "Creating the folder…");

    public static string CreateFolderFailed(string reason) => Pick(
        $"フォルダーを作成できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The folder could not be created.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string FolderCreated(string name) => Pick(
        $"「{name}」を作成しました", $"Created \"{name}\"");

    // ------------------------------------------------------------------ パスワード (#20, #63)

    public static string PasswordDialogTitle => Pick("パスワード", "Password");

    public static string PasswordOk => Pick("OK", "OK");

    public static string PasswordCancel => Pick("キャンセル", "Cancel");

    public static string AskPassword(string archiveName) => Pick(
        $"「{archiveName}」はパスワードで保護されています。",
        $"\"{archiveName}\" is protected with a password.");

    public static string AskPasswordAgain(string archiveName) => Pick(
        $"パスワードが違います。{Environment.NewLine}「{archiveName}」のパスワードを入力してください。",
        $"That password is not correct.{Environment.NewLine}"
        + $"Enter the password for \"{archiveName}\".");

    public static string SetPasswordPrompt(string archiveName) => Pick(
        $"「{archiveName}」に設定するパスワードを入力してください。{Environment.NewLine}{Environment.NewLine}"
        + "パスワードを忘れると、中身を展開できなくなります。",
        $"Enter the password to set on \"{archiveName}\".{Environment.NewLine}{Environment.NewLine}"
        + "If you forget the password, the contents cannot be extracted.");

    public static string ChangePasswordPrompt(string archiveName) => Pick(
        $"「{archiveName}」の新しいパスワードを入力してください。{Environment.NewLine}{Environment.NewLine}"
        + "空欄のままにすると、パスワードを削除します。",
        $"Enter the new password for \"{archiveName}\".{Environment.NewLine}{Environment.NewLine}"
        + "Leave it empty to remove the password.");

    public static string NoPasswordSet => Pick(
        "この書庫にはパスワードが設定されていません", "This archive has no password");

    public static string PasswordUnchanged => Pick(
        "パスワードは変更されていません", "The password is unchanged");

    public static string PasswordRemoved => Pick(
        "パスワードを削除しました", "The password was removed");

    public static string PasswordSet => Pick(
        "パスワードを設定しました", "The password was set");

    public static string PasswordSetAes => Pick(
        "パスワードを設定しました (AES-256)", "The password was set (AES-256)");

    public static string RemovingPassword => Pick(
        "パスワードを削除しています…", "Removing the password…");

    public static string ApplyingPassword => Pick(
        "パスワードを設定して書庫を作り直しています…",
        "Rebuilding the archive with the password…");

    public static string Rebuilding(int done) => Pick(
        $"書庫を作り直しています… ({done:N0} 個)",
        $"Rebuilding the archive… ({done:N0})");

    public static string ChangePasswordFailed(string reason) => Pick(
        $"パスワードを変更できませんでした。書庫の変更はありません。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The password could not be changed. The archive was left untouched.{Environment.NewLine}{Environment.NewLine}{reason}");

    // ------------------------------------------------------------------ 削除

    public static string ConfirmDelete(int count, string preview, string more, string detail) => Pick(
        $"選択した {count:N0} 個の項目を書庫から削除します。"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}{detail}"
        + $"{Environment.NewLine}{Environment.NewLine}この操作は取り消せません。削除しますか?",
        $"{count:N0} selected {Plural(count, "item", "items")} will be deleted from the archive."
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}{detail}"
        + $"{Environment.NewLine}{Environment.NewLine}This cannot be undone. Delete them?");

    public static string DeleteFolderDetail(int affected) => Pick(
        $"{Environment.NewLine}{Environment.NewLine}"
        + $"フォルダーの中身を含めて {affected:N0} 個のファイルが削除されます。",
        $"{Environment.NewLine}{Environment.NewLine}"
        + $"{affected:N0} {Plural(affected, "file", "files")} will be deleted, "
        + "including the contents of the folders.");

    public static string Deleting => Pick("削除しています…", "Deleting…");

    public static string DeleteCancelled => Pick("削除を中断しました", "Deleting was stopped");

    public static string DeleteCancelledDetail => Pick(
        "削除を中断しました。書庫の変更はありません。",
        "Deleting was stopped. The archive was left untouched.");

    public static string DeleteDone(int count) => Pick(
        $"{count:N0} 個の項目を削除しました",
        $"Deleted {count:N0} {Plural(count, "item", "items")}");

    public static string DeleteFailed(string reason) => Pick(
        $"削除できませんでした。書庫の変更はありません。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The items could not be deleted. The archive was left untouched.{Environment.NewLine}{Environment.NewLine}{reason}");

    // ------------------------------------------------------------------ 追加

    public static string AddDialogTitle => Pick(
        "書庫に追加するファイルを選択", "Select the files to add to the archive");

    public static string NoArchiveToAddTo => Pick(
        "追加先の書庫がありません。書庫を開くか、新規作成してください。",
        "There is no archive to add to. Open an archive or create a new one.");

    public static string FormatIsReadOnly(string format) => Pick(
        $"{format} 書庫にはファイルを追加できません。読み取りのみに対応しています。",
        $"Files cannot be added to {format} archives. This format is supported for reading only.");

    public static string ConfirmReplace(int count, string preview, string more) => Pick(
        $"同じ名前の項目が書庫内に {count:N0} 個あります。置き換えますか?"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "「いいえ」を選ぶと、それらは置き換えません。",
        $"The archive already contains {count:N0} {Plural(count, "item", "items")} "
        + $"with the same name. Replace {Plural(count, "it", "them")}?"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "Choosing No leaves what is already in the archive.");

    public static string Adding(string name) => Pick($"追加中: {name}", $"Adding: {name}");

    public static string RebuildingEncrypted => Pick(
        "パスワード付きの書庫を作り直しています…",
        "Rebuilding the password-protected archive…");

    public static string AddFailed(string reason) => Pick(
        $"書庫に追加できませんでした。書庫の変更はありません。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The files could not be added to the archive. The archive was left untouched.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string AddCancelled => Pick("追加を中断しました", "Adding was stopped");

    public static string AddCancelledDetail => Pick(
        "追加を中断しました。書庫の変更はありません。",
        "Adding was stopped. The archive was left untouched.");

    public static string AddedFilesLine(int count) => Pick(
        $"追加したファイル: {count:N0} 個",
        $"Files added: {count:N0}");

    public static string ReplacedFilesLine(int count) => Pick(
        $"置き換えたファイル: {count:N0} 個",
        $"Files replaced: {count:N0}");

    public static string KeptFilesLine(int count) => Pick(
        $"置き換えずそのまま残したファイル: {count:N0} 個",
        $"Files kept instead of replaced: {count:N0}");

    public static string FailedFilesLine(int count) => Pick(
        $"追加できなかったファイル: {count:N0} 個",
        $"Files that could not be added: {count:N0}");

    public static string AddDone(int count) => Pick(
        $"{count:N0} 個のファイルを追加しました",
        $"Added {count:N0} {Plural(count, "file", "files")}");

    // ------------------------------------------------------------------ 既定のアプリで開く (#12, #16)

    public static string OpenedReadOnly(string name, string format) => Pick(
        $"{name} を開きました ({format} は読み取りのみのため、書き換えても書庫は更新されません)",
        $"Opened {name} ({format} is read-only, so any changes will not go back into the archive)");

    public static string OpenedWatching(string name) => Pick(
        $"{name} を開きました。保存すると、書庫に反映するか確認します",
        $"Opened {name}. When you save it, you will be asked whether to put it back.");

    // ------------------------------------------------------------------ 書庫の中の書庫 (#30)

    public static string OpenedNested(string entryPath, string parentName) => Pick(
        $"{parentName} 内の {entryPath} を新しいタブで開きました",
        $"Opened {entryPath} from {parentName} in a new tab");

    public static string Save => Pick("保存", "Save");

    // ------------------------------------------------------------------ AI 連携の設定 (#24)

    public static string AiSettingsMenu => Pick("AI連携の設定…", "AI settings...");

    public static string AiDialogTitle => Pick("AI連携の設定", "AI settings");

    public static string AiIntro => Pick(
        "書庫のルールをAIに推定させる機能の設定です。",
        "Settings for the AI that works out the rules of an archive.");

    public static string AiPresetLabel => Pick("接続先", "Provider");

    public static string AiPresetCustom => Pick("その他", "Other");

    public static string AiPresetGoogle => Pick("Google (Gemini)", "Google (Gemini)");

    public static string AiPresetOpenAi => Pick("OpenAI", "OpenAI");

    public static string AiPresetAnthropic => Pick("Anthropic (Claude)", "Anthropic (Claude)");

    public static string AiPresetLocal => Pick("ローカル", "Local");

    public static string AiEndpointLabel => Pick("URL", "URL");

    public static string AiModelLabel => Pick("モデル名", "Model");

    public static string AiKeyLabel => Pick("APIキー", "API key");

    /// <summary>送るものと鍵の置き場。隠さずダイアログに出す (#24)。</summary>
    public static string AiPrivacyNotice => Pick(
        "設定したAIに書庫のファイル名とフォルダー構成を送信してルールを推定します。"
        + "ファイルの中身は送信しません。"
        + "無料枠では送信したものが提供元の製品改善に使用される可能性がありますのでご注意ください。"
        + "入力したAPIキーは本アプリケーションと同じフォルダーに保存されますが、"
        + "移動した場合には無効になります。",
        "The AI you set up receives the file names and folder structure of the archive "
        + "and works out the rules from them. File contents are never sent. "
        + "On a free tier, what you send may be used to improve the provider's products. "
        + "The API key you enter is stored in the same folder as this application, "
        + "and stops working if it is moved elsewhere.");

    public static string AiCancel => Pick("キャンセル", "Cancel");

    public static string AiTest => Pick("接続テスト", "Test connection");

    /// <summary>
    /// 試している間の表示。**経過した秒を出す** (#76)。
    /// 待ち時間を延ばしたぶん、動いているのか固まったのかが分からなくなるため。
    /// </summary>
    public static string AiTesting(int seconds) => seconds <= 0
        ? Pick("接続をテストしています…", "Testing...")
        : Pick($"接続をテストしています… ({seconds} 秒)", $"Testing... ({seconds}s)");

    public static string AiBadEndpoint => Pick(
        "URLが正しくありません。http:// または https:// で始まるURLを入力してください。",
        "That is not a usable URL. It should start with http:// or https://.");

    public static string AiNoModel => Pick(
        "モデル名を入力してください。", "Enter the model name.");

    public static string AiReachable(string model) => Pick(
        $"接続しました。{model} が使えます。",
        $"Connected. {model} is available.");

    public static string AiRefused(int status, string reason) => Pick(
        $"接続先から拒否されました (HTTP {status})。{Environment.NewLine}{reason}",
        $"The request was refused (HTTP {status}).{Environment.NewLine}{reason}");

    /// <summary>
    /// 待ち時間切れ。**繋がらなかったのとは違う** (#76)。
    /// 相手には届いており、答えが返る前に上限に達しただけなので、
    /// URLや通信経路を疑わせない。考えるモデルほど時間がかかる。
    /// </summary>
    public static string AiTimedOut(int seconds) => Pick(
        $"{seconds} 秒待ちましたが、回答が得られませんでした。"
        + $"{Environment.NewLine}"
        + "接続先が混雑しているか、回答に時間のかかるモデルの可能性があります。"
        + "もう一度試すか、軽量なモデルに変更してください。",
        $"Waited {seconds}s with no answer.{Environment.NewLine}"
        + "The service may be busy, or the model may take a long time to think. "
        + "Try again, or switch to a lighter model.");

    public static string AiSaved => Pick("AI連携の設定を保存しました", "Saved the AI settings");

    public static string AiKeyLost => Pick(
        "保存されているAPIキーが使用できませんでした。再度入力してください。",
        "The saved API key could not be used. Please enter it again.");

    public static string AiEmptyAnswer => Pick(
        "AI から回答がありませんでした。もう一度試してください。",
        "The AI returned an empty answer. Please try again.");

    // ------------------------------------------------------------ お手本からのルール推定 (#25)

    public static string RuleMenu => Pick(
        "書庫のルールを推定…", "Work out the rules of this archive...");

    public static string RuleNeedsAi => Pick(
        "先に「AI連携の設定…」で接続先を設定してください。",
        "Set up the connection under AI settings... first.");

    public static string RuleDialogTitle => Pick(
        "書庫のルールを推定", "Work out the archive's rules");

    public static string RuleIntro(string archiveName) => Pick(
        $"{archiveName} を「お手本」として、この書庫のルールを推定します。",
        $"Treat {archiveName} as the model archive and work out its rules.");

    public static string RuleSendLabel => Pick("書庫詳細", "Archive details");

    /// <summary>
    /// 送る前に、送る量と端折った数をそのまま出す (#25)。
    /// </summary>
    /// <remarks>
    /// **効いた上限をそのまま書く** (#70)。「多すぎるため」とだけ書くと、書庫が
    /// 大きすぎたように読める。実際に効くのはたいてい1フォルダあたりの上限のほうで、
    /// 159ファイル・10KB の書庫でも端折りは起きる。
    /// <para>
    /// **端折りが無くても上限を書く** (#89)。送る前に「何が送られるのか」を
    /// 知りたい気持ちは、端折りの有無では変わらない。
    /// </para>
    /// </remarks>
    public static string RulePayload(int bytes, int omitted, int total) => omitted == 0
        ? Pick($"この書庫のすべての名前を送信します。合計 {bytes:N0} バイトです。",
            $"Every name in this archive is included. {bytes:N0} bytes in total.")
        : Pick($"送信する名前は最大 {total:N0} 個のため、"
            + $"{omitted:N0} 個は送信しません。"
            + $"合計 {bytes:N0} バイトです。",
            $"At most {total:N0} names are sent in all, "
            + $"so {omitted:N0} of them are left out. "
            + $"{bytes:N0} bytes in total.");

    /// <summary>送るものの断り。AI連携の設定と同じ書きぶりにする (#24)。</summary>
    public static string RulePrivacyShort => Pick(
        "設定したAIに上記の書庫詳細を送信してルールを推定します。"
        + "ファイルの中身は送信しません。",
        "The archive details above are sent to the AI you set up, "
        + "and the rules are worked out from them. File contents are never sent.");

    public static string RuleSend => Pick("推定する", "Work out the rules");

    /// <summary>推定している間の表示。経過した秒を出す (#76)。</summary>
    public static string RuleSending(int seconds) => seconds <= 0
        ? Pick("ルールを推定しています…", "Working it out...")
        : Pick($"ルールを推定しています… ({seconds} 秒)", $"Working it out... ({seconds}s)");

    public static string RuleClose => Pick("閉じる", "Close");

    public static string RuleFound(int count) => Pick(
        $"{count} 件のルールを推定しました。", $"Worked out {count} rule(s).");

    public static string RuleNoneFound => Pick(
        "ルールを推定できませんでした。",
        "No rules could be worked out.");

    /// <summary>採らなかった候補の内訳。黙って減らすと、数が合わない理由が分からない。</summary>
    /// <remarks>
    /// **「破っている」と「確かめられない」は分けて数える** (#80)。前者は AI の
    /// 読み違いだが、後者はこちらが評価できなかったということで、意味がまるで違う。
    /// 混ぜて「お手本が満たさない」とだけ言うと、こちらの落ち度を相手のせいにする。
    /// </remarks>
    public static string RuleDropped(int broken, int unchecked_, int unusable)
    {
        var parts = new List<string>();

        if (broken > 0)
        {
            parts.Add(Pick($"お手本の書庫が守っていないもの {broken} 件",
                $"{broken} that the model archive itself breaks"));
        }

        if (unchecked_ > 0)
        {
            parts.Add(Pick($"確認できる対象が無いもの {unchecked_} 件",
                $"{unchecked_} with nothing to check them against"));
        }

        if (unusable > 0)
        {
            parts.Add(Pick($"ルールとして解釈できないもの {unusable} 件",
                $"{unusable} that could not be read as a rule"));
        }

        return parts.Count == 0
            ? string.Empty
            : Pick(string.Join("、", parts) + "を除外しました。",
                "Left out " + string.Join(" and ", parts) + ".");
    }

    /// <summary>これは提案であって、確かめた結果ではない (仕様書 10.5節)。</summary>
    /// <remarks>
    /// **#72 で足す・直す・消す口を外したのに、この文だけ残っていた** (#77)。
    /// いまできるのは「使う / 使わない」の選びだけ。無い機能を案内していた。
    /// </remarks>
    public static string RuleProposalNotice => Pick(
        "これらはAIが推定したルールです。書庫の本来のルールとは異なる場合があります。"
        + "ルールとして使用したいものを選択してください。",
        "These are the rules the AI worked out. They may differ from the real ones. "
        + "Choose which ones to use.");

    public static string RuleUnreadable => Pick(
        "AI の回答をルールとして解釈できませんでした。"
        + "モデルを変更するか、もう一度試してください。",
        "The AI's answer could not be read as a set of rules. "
        + "Try again, or try a different model.");

    // ------------------------------------------------ 確かめて直す (#26)

    public static string RuleColumnUse => Pick("使用", "Use");

    /// <summary>「使用」の見出しを押すと何が起きるか (#94)。押せると分かる形が他に無い。</summary>
    public static string RuleUseAll => Pick(
        "クリックすると、すべて使用する・すべて使用しないを切り替えます。",
        "Click to turn them all on, or all off.");

    /// <summary>選んだルールを一覧から消す (#98)。</summary>
    /// <remarks>
    /// **何を消すのかを名前に書く** (#101)。「消す」だけだと、「使用」のチェックを
    /// 付けたものが消えると読める。あれは使うかどうかの印で、選びの印ではない。
    /// </remarks>
    public static string RuleDelete => Pick("選択したルールを削除", "Remove selected rules");

    /// <summary>押せない訳 (#101)。押せないまま置くと、壊れているように見える。</summary>
    public static string RuleDeleteNone => Pick(
        "削除するルールを選択してください。行をクリックすると選択できます"
        + "(Ctrl または Shift を押すと複数選択できます)。",
        "Select the rules to remove first. Click a row to select it "
        + "(hold Ctrl or Shift to select several).");

    /// <summary>
    /// 消すことと、使用を外すことの違い (#98)。
    /// </summary>
    /// <remarks>
    /// **消すと、次の推定でまた挙がってくることがある。**外したものは印を付けて
    /// 残るので挙がってこない (#26)。この違いは、押す前に分かっていないと困る。
    /// </remarks>
    public static string RuleDeleteHint => Pick(
        "選択したルールを一覧から削除します。行をクリックして選択してください"
        + "(「使用」のチェックとは別です)。"
        + "削除したルールは、次の推定で再び提案されることがあります。"
        + "提案されないようにするには、「使用」を外して保存してください。",
        "Removes the selected rules from the list. Click a row to select it "
        + "(this is not the Use box). "
        + "A removed rule can be proposed again the next time you work out the rules. "
        + "To keep one from coming back, clear its Use box and save instead.");

    /// <summary>消したことの知らせ (#98)。**まだファイルは変わっていない**と言う。</summary>
    public static string RuleRemoved(int count) => Pick(
        $"{count} 件のルールを一覧から削除しました。保存するとファイルに反映されます。",
        $"Removed {count} rule(s) from the list. Saving writes the change to the file.");

    public static string RuleColumnSource => Pick("提案元", "From");

    /// <summary>いま開いている書庫に当てるとどうなるか (#26)。</summary>
    public static string RuleColumnVerdict => Pick("この書庫では", "In this archive");

    public static string RuleSourceAi => Pick("AI", "AI");

    public static string RuleSourceHand => Pick("自分", "You");

    /// <summary>
    /// AI が指した場所を、こちらで数え上げて補ったもの (#84)。
    /// **AI が言っていないことを AI 名義にしない。**
    /// 出どころなので、こちらの都合の「補い」ではなく**誰が出したか**で言う (#89)。
    /// </summary>
    public static string RuleSourceFilled => Pick("アプリ", "App");

    /// <summary>補ったルールの説明。こちらが書くので、書きぶりは一定になる。</summary>
    public static string RuleFilledSays(string name) => Pick(
        $"この場所のすべてのフォルダーに {name} がある。",
        $"Every folder in this place holds {name}.");

    /// <summary>補った件数の知らせ。**AI が挙げた数と混ぜない** (#84)。</summary>
    public static string RuleFilledCount(int count) => Pick(
        $"うち {count} 件は、AI が示した場所を Expzip が確認して補いました。",
        $"{count} of them were filled in by counting the places the AI pointed at.");

    /// <summary>補ったルールの根拠。**何個数えたかを出す。**</summary>
    public static string RuleFilledSaw(int places) => Pick(
        $"同じ場所の {places} 個すべてにある (Expzip が確認)",
        $"present in all {places} of them (counted)");

    public static string RuleHolds => Pick("順守", "Holds");

    public static string RuleBreaks(int count) => Pick(
        $"{count:N0} 件が違反", $"{count:N0} do not match");

    public static string RuleMissing => Pick("見つからない", "Not found");

    /// <summary>当てる先が1つも無い。守られているとは言えない (#26)。</summary>
    public static string RuleNothingToCheck => Pick("対象なし", "Nothing to check");

    /// <summary>採らなかった候補が、なぜ採られなかったか (#80)。</summary>
    public static string RuleDropBroken => Pick("除外 (お手本の書庫が守っていない)", "Not taken (model breaks it)");

    /// <summary>
    /// 当てる先が無くて確かめられなかった。**AI の間違いとは限らない。**
    /// 書けないことを値に押し込まれると、ここに来る。
    /// </summary>
    public static string RuleDropUnchecked
        => Pick("除外 (確認できる対象なし)", "Not taken (cannot be checked)");

    public static string RuleSave => Pick("保存する", "Save");

    public static string RuleLoaded(int count, string from) => from.Length == 0
        ? Pick($"保存されている {count} 件のルールを読み込みました。",
            $"Loaded {count} saved rule(s).")
        : Pick($"保存されている {count} 件のルールを読み込みました (お手本: {from})。",
            $"Loaded {count} saved rule(s) (learned from {from}).");

    public static string RuleAlreadyHad => Pick(
        "既に一覧にあるルールは追加していません。", " Ones already listed were not added again.");

    public static string RuleSaved(int total, int used) => Pick(
        $"{total:N0} 件のルールを保存しました (使用するのは {used:N0} 件)。",
        $"Saved {total:N0} rule(s); {used:N0} of them are in use.");

    /// <summary>保存したルールのうち、実際に使うもの。ステータスバーに出す。</summary>
    public static string RuleKept(int used) => Pick(
        $"ルールを保存しました (使用するのは {used:N0} 件)",
        $"Saved the rules; {used:N0} in use");

    public static string RuleCleared => Pick(
        "保存されていたルールを削除しました。", "Removed the saved rules.");

    public static string RuleSaveFailed => Pick(
        "ルールを保存できませんでした。exe と同じフォルダーに書き込めないようです。",
        "Could not save the rules. The folder holding the exe appears not to be writable.");

    // ------------------------------------------------ ルールに合っているか見る (#27)

    public static string RuleAuditMenu => Pick(
        "ルールに合っているか検査…", "Check against the rules...");

    public static string RuleNoneSaved => Pick(
        "ルールが保存されていません。先に「書庫のルールを推定…」で保存してください。",
        "No rules are saved yet. "
        + "Set them under \"Work out the rules of this archive...\" first.");

    public static string RuleAuditTitle(string archiveName) => Pick(
        $"ルールの検査結果 - {archiveName}",
        $"Rule check - {archiveName}");

    public static string RuleAuditClean => Pick(
        "ルールに合っていない項目は見つかりませんでした",
        "Nothing conflicts with the rules");

    /// <summary>合っていない項目の数と、そもそも無かったものの数 (#27)。</summary>
    /// <remarks>
    /// **無かったものは一覧に印を付けられない。**指させる項目が無いため、
    /// 数だけでも別に言う。印が付かないことを「問題なし」と読ませない。
    /// </remarks>
    public static string RuleAuditFound(int broken, int missing) => missing == 0
        ? Pick($"ルールに合っていない項目が {broken:N0} 個あります",
            $"{broken:N0} item(s) conflict with the rules")
        : broken == 0
            ? Pick($"あるはずの項目が {missing:N0} 個ありません",
                $"{missing:N0} required item(s) are missing")
            : Pick($"ルールに合っていない項目が {broken:N0} 個あります。"
                + $"あるはずの項目が {missing:N0} 個ありません",
                $"{broken:N0} item(s) conflict with the rules, "
                + $"and {missing:N0} required item(s) are missing");

    public static string RuleAuditSource(int rules, string from, DateTimeOffset at) =>
        from.Length == 0
            ? Pick($"{rules:N0} 件のルールで検査しました。",
                $"Applied {rules:N0} rule(s).")
            : Pick($"{rules:N0} 件のルールで検査しました "
                + $"(お手本: {from}、{at.LocalDateTime:yyyy/MM/dd HH:mm})。",
                $"Applied {rules:N0} rule(s) "
                + $"(learned from {from} on {at.LocalDateTime:yyyy/MM/dd HH:mm}).");

    /// <summary>多すぎて出し切れなかったときだけ出す (#27)。</summary>
    public static string RuleAuditTrimmed(int count) => Pick(
        $"ほかに {count:N0} 件のルールがありますが、数が多すぎるため表示していません。修正してから、もう一度検査してください。",
        $"{count:N0} more are not listed because there are too many. "
        + "Narrow the rules or fix these first.");

    public static string RuleAuditColumnTarget => Pick("対象", "Item");

    public static string RuleAuditColumnRule => Pick("ルール", "Rule");

    /// <summary>一覧の旗に添える説明 (#27)。</summary>
    public static string RuleBreaksTooltip(string rules) => Pick(
        $"ルールに合っていません:{Environment.NewLine}{rules}",
        $"Does not match the rules:{Environment.NewLine}{rules}");

    /// <summary>自分で対処するものに印を付ける列 (#87)。</summary>
    public static string RuleColumnHandle => Pick("修正する", "Fix");

    /// <summary>
    /// 直し終えたときに押す (#87)。
    /// </summary>
    /// <remarks>
    /// **「更新」にした** (#88)。押してすることは書庫の読み直しで、他の画面の
    /// 「更新」と同じ。長い文にしても、そこは伝わらない。何をするかは
    /// <see cref="RuleDoneHint"/> で添える。
    /// </remarks>
    public static string RuleDone => Pick("更新", "Refresh");

    /// <summary>「更新」に添える説明 (#88)。押すと何が起きるかを言う。</summary>
    public static string RuleDoneHint => Pick(
        "書庫を読み込み直して、もう一度ルールに合っているか検査します。",
        "Reads the archive again and re-checks it against the rules.");

    /// <summary>印を付けたものが全部直っていた (#87)。</summary>
    public static string RuleDoneAll => Pick(
        "「修正する」に印を付けた項目は、すべてルールに合うようになりました。",
        "Everything you marked now matches the rules.");

    /// <summary>
    /// 印を付けたのに、まだ直っていないものがある (#87)。
    /// **確かめずに印を消さない**ので、こう言える。
    /// </summary>
    public static string RuleDoneLeft(int count) => Pick(
        $"「修正する」に印を付けた項目のうち、{count} 個はまだルールに合っていません。",
        $"{count} of the items you marked still do not match the rules.");

    /// <summary>
    /// ツリーに出す印の説明 (#87)。**配下も含む**ことを言う。
    /// </summary>
    /// <remarks>
    /// 印が付いているフォルダ自体に問題があるとは限らない。中のどこかにある、
    /// という意味だと分からないと、開いても何も無いように見える。
    /// </remarks>
    public static string RuleTreeBreakTooltip => Pick(
        "このフォルダー以下にルールに合っていない項目があります。",
        "Something in or below this folder does not match the rules.");

    /// <summary>
    /// ツリーの印に添える、違反の中身 (#88)。
    /// </summary>
    /// <remarks>
    /// **印だけでは、直すときに何をすればよいか分からない。**どこの何が、どの
    /// ルールに合っていないのかまで書く。結果の窓を開き直さずに済ませるための説明。
    /// </remarks>
    public static string RuleTreeBreakDetail(string heading, string items) =>
        $"{heading}{Environment.NewLine}{Environment.NewLine}{items}";

    /// <summary>違反1件を1行で書く (#88)。</summary>
    public static string RuleTreeBreakItem(string path, string what) => $"{path}: {what}";

    /// <summary>説明に載せ切れなかった残り (#88)。黙って切ると、これで全部だと読める。</summary>
    public static string RuleTreeBreakMore(int count) => Pick(
        $"ほか {count} 件",
        $"and {count} more");

    public static string RuleColumnKind => Pick("種類", "Kind");

    /// <remarks>
    /// **「どこに」から「対象」に変えた** (#81)。場所の列 (<see cref="RuleColumnPlace"/>)
    /// を足したので、この列はフォルダかファイルかだけを言う。1つの見出しが
    /// 2つの意味を持たないようにする。
    /// </remarks>
    public static string RuleColumnScope => Pick("対象", "Applies to");

    /// <summary>当てる場所の列 (#81)。</summary>
    public static string RuleColumnPlace => Pick("場所", "Where");

    /// <summary>
    /// 「この形のものが必ずある」(#83)。「必ずある」は名前しか取らないので、
    /// 名前が場所ごとに違うもの (examples の各フォルダの `.ino` など) が書けなかった。
    /// </summary>
    public static string RuleKindRequiredPattern => Pick("この形の名前が必ずある", "A shape must exist");

    /// <summary>場所が決まっていないとき、その列に出す言葉 (#81)。</summary>
    public static string RuleWhereAnywhere => Pick("書庫全体", "Whole archive");

    public static string RuleColumnValue => Pick("値", "Value");

    public static string RuleColumnDescription => Pick("説明", "What it says");

    public static string RuleColumnEvidence => Pick("根拠", "Why");

    public static string RuleKindRequiredEntry => Pick("必ずある", "Must exist");

    public static string RuleKindRequiredFolder => Pick("必ずあるフォルダー", "Folder must exist");

    public static string RuleKindForbiddenExtension => Pick("含めない拡張子", "Extension not allowed");

    public static string RuleKindForbiddenName => Pick("含めない名前", "Name not allowed");

    public static string RuleKindNamePattern => Pick("名前の形", "Name pattern");

    public static string RuleScopeRoot => Pick("ルート直下", "Directly under the root");

    public static string RuleScopeFolders => Pick("すべてのフォルダー", "Every folder");

    public static string RuleScopeFiles => Pick("すべてのファイル", "Every file");

    public static string RuleScopeAll => Pick("すべての項目", "Every entry");

    // -------------------------------------------------- 送る中身 (#25)。そのまま画面にも出す

    public static string RuleDigestArchive(string name) => Pick(
        $"書庫: {name}", $"Archive: {name}");

    public static string RuleDigestCounts(int files, int folders) => Pick(
        $"ファイル {files:N0} 個、フォルダー {folders:N0} 個",
        $"{files:N0} file(s), {folders:N0} folder(s)");

    public static string RuleDigestFolders(int count) => Pick(
        $"## フォルダー ({count:N0} 個)", $"## Folders ({count:N0})");

    public static string RuleDigestNoFolders => Pick(
        "(フォルダーなし)", "(none)");

    public static string RuleDigestMoreFolders(int count) => Pick(
        $"(ほか {count:N0} 個は送らない)", $"({count:N0} more not sent)");

    public static string RuleDigestExtensions => Pick(
        "## 拡張子ごとのファイル数 (端折らない)",
        "## File count per extension (complete)");

    public static string RuleDigestNoExtension => Pick("(拡張子なし)", "(no extension)");

    public static string RuleDigestRoot(int count) => Pick(
        $"## ルート直下のファイル ({count:N0} 個)",
        $"## Files directly under the root ({count:N0})");

    public static string RuleDigestNoRootFiles => Pick(
        "(ルート直下のファイルなし)", "(none)");

    public static string RuleDigestMoreFiles(int count) => Pick(
        $"(ほか {count:N0} 個は送らない)", $"({count:N0} more not sent)");

    /// <remarks>
    /// **数を書かない** (#99)。1フォルダあたりの数は配る順番の決め方でしかなく、
    /// 枠が余っていれば端折った所へ配り直すので、上限として書くと嘘になる。
    /// </remarks>
    public static string RuleDigestSamples => Pick(
        "## 各フォルダーのファイル名", "## File names per folder");

    public static string RuleDigestMoreHere(int count) => Pick(
        $"…ほか {count:N0} 個", $"...and {count:N0} more");

    /// <remarks>
    /// **効いた上限だけを書く** (#99)。1フォルダあたりの数は配る順番の決め方に
    /// なったので、端折りが出るのは**全体の枠を使い切ったときだけ**になった。
    /// </remarks>
    public static string RuleDigestOmitted(int count, int total) => Pick(
        $"※ 名前 {count:N0} 個は送っていない "
        + $"(全体で最大 {total:N0} 個までのため)。"
        + "ここに出ていない名前を根拠にしないこと。",
        $"Note: {count:N0} names are not included "
        + $"({total:N0} names at most in total). "
        + "Do not base any rule on names that are not shown here.");

    /// <summary>AI への頼み方 (#25)。答えられる形をあらかじめ絞る。</summary>
    /// <remarks>
    /// **画面の言語で頼む。**説明と根拠は AI が書くため、頼んだ言語で返ってくる。
    /// 日本語で使っている人に英語の説明を並べても読まれない。
    /// </remarks>
    public static string RulePromptSystem => Pick(
        RulePromptJapanese, RulePromptEnglish);

    private const string RulePromptJapanese = """
        あなたは書庫 (ZIP など) の作り方を読み取る手伝いをします。
        これから、ある書庫のフォルダ構成とファイル名の一覧を渡します。
        ファイルの中身はありません。名前と構成だけです。

        目的は、別の書庫を同じ作り方で作るための決まりを挙げることです。
        この書庫を言い直すことではありません。次に別の中身で書庫を作る人が、
        それを見て同じ形に揃えられるものを挙げてください。

        次の JSON だけを返してください。前後に説明を書かないでください。

        {"rules":[{"kind":"...","scope":"...","where":"...","value":"...",
                   "description":"...","evidence":"..."}]}

        kind は次のいずれかです。
        - required_entry: この名前のものが必ずある。value は名前
        - required_folder: このフォルダが必ずある。value はフォルダ名
        - forbidden_extension: この拡張子を含めない。value は ".tmp" のような拡張子
        - forbidden_name: この名前のものを含めない。value は名前
        - name_pattern: 名前がこの形をしている。value は .NET の正規表現
        - required_pattern: この形のものが必ず1つはある。value は .NET の正規表現

        name_pattern と required_pattern は間違えやすいので気をつけてください。
        - name_pattern は「**その形のものしか無い**」。1つも無い場所は素通りします
        - required_pattern は「**その形のものが1つはある**」。空の場所は違反になります

        例: examples の各フォルダに .ino が1つはあり、それ以外は置かない、と言いたいとき
        {"kind":"required_pattern","scope":"files","where":"^[^/]+/examples/[^/]+$",
         "value":".+\\.ino","description":"...","evidence":"..."}
        {"kind":"name_pattern","scope":"files","where":"^[^/]+/examples/[^/]+$",
         "value":".+\\.ino","description":"...","evidence":"..."}

        scope は、何に当てるかです。次のいずれかです。
        - root: ルート直下だけ
        - folders: フォルダ
        - files: ファイル
        - all: すべての項目

        where は、どこに当てるかです。**対象を含むフォルダ**の書庫内パスに当てる
        正規表現を書いてください。書庫全体に当てるなら省いてください。

        **「どこに」と「どんな形か」を1本の正規表現に繋げないでください。**
        繋げると、その形に合わない他の場所の項目まで違反になってしまいます。
        必ず where と value に分けてください。

        例: libraries 直下のフォルダ名が usb_host_ で始まる、と言いたいとき
        {"kind":"name_pattern","scope":"folders","where":"^[^/]+/libraries$",
         "value":"usb_host_[a-z0-9_]+","description":"...","evidence":"..."}

        例: 書庫のルートに library.json がある、と言いたいとき
        {"kind":"required_entry","scope":"all","where":"^[^/]+$",
         "value":"library.json","description":"...","evidence":"..."}

        書庫全体が1つのフォルダに包まれていることがあります。その場合、
        利用者はその**フォルダの中**を「書庫のルート」と受け取ります。
        description でもそう書いてください。
        いっぽう scope の root は、**包んでいるフォルダそのもの**を指し、
        その中身は指しません。中を指すには where に ^[^/]+$ を書いてください。

        description には、そのルールを日本語の一文で書いてください。
        evidence には、一覧のどこからそう読み取ったかを短く書いてください。

        探してほしいもの:
        - 繰り返し現れる形。兄弟のフォルダに同じ顔ぶれのファイルが揃っている、など
        - 置き場所の決まり。ある種類のものが、決まった場所にまとめられている
        - 名前の付け方。日付、連番、接頭辞など、同じ形をしている名前の並び
        - 無いもの。作業中の一時ファイルや、OSが作るファイルが含まれていない

        守ってほしいこと:
        - 一覧に出ている名前だけを根拠にしてください
        - 一覧は端折られていることがあります。「ほか N 個は送らない」と書いてあれば、そこは見えていません
        - この書庫だけの固有の名前を、そのまま決まりにしないでください。
          例:「Foo-main フォルダがある」は、次の書庫では名前が変わるので役に立ちません。
          形が繰り返されているなら name_pattern で書いてください
        - name_pattern は、一覧にあるその範囲の名前すべてに当てはまるものだけにしてください
        - 何にでも当てはまる正規表現 (".*" など) は挙げないでください
        - 数を絞らず、気付いたものを挙げてください。多くても20件までにしてください
        """;

    private const string RulePromptEnglish = """
        You help read off how an archive (ZIP and the like) is put together.
        You will be given the folder structure and file names of one archive.
        There are no file contents - only names and structure.

        The goal is to list rules for building another archive the same way,
        not to restate this one. List what someone packing different contents
        next time could follow to arrive at the same shape.

        Return only the following JSON. Do not write anything before or after it.

        {"rules":[{"kind":"...","scope":"...","where":"...","value":"...",
                   "description":"...","evidence":"..."}]}

        kind must be one of:
        - required_entry: an entry with this name must exist. value is the name
        - required_folder: this folder must exist. value is the folder name
        - forbidden_extension: this extension must not appear. value is like ".tmp"
        - forbidden_name: an entry with this name must not appear. value is the name
        - name_pattern: names have this shape. value is a .NET regular expression
        - required_pattern: at least one entry of this shape exists.
          value is a .NET regular expression

        These two are easy to confuse:
        - name_pattern says "nothing but this shape". An empty place passes
        - required_pattern says "at least one of this shape". An empty place fails

        Example: every folder under examples holds one .ino and nothing else
        {"kind":"required_pattern","scope":"files","where":"^[^/]+/examples/[^/]+$",
         "value":".+\\.ino","description":"...","evidence":"..."}
        {"kind":"name_pattern","scope":"files","where":"^[^/]+/examples/[^/]+$",
         "value":".+\\.ino","description":"...","evidence":"..."}

        scope says what to apply it to. It must be one of:
        - root: only directly under the root
        - folders: folders
        - files: files
        - all: every entry

        where says where to apply it: a regular expression matched against the
        archive path of the **folder containing** the entry. Omit it to apply
        the rule to the whole archive.

        Do NOT join "where" and "what shape" into one regular expression.
        Joined, entries elsewhere that do not fit that shape become violations.
        Always split them into where and value.

        Example: folder names directly under libraries start with usb_host_
        {"kind":"name_pattern","scope":"folders","where":"^[^/]+/libraries$",
         "value":"usb_host_[a-z0-9_]+","description":"...","evidence":"..."}

        Example: library.json sits directly inside the wrapping folder
        {"kind":"required_entry","scope":"all","where":"^[^/]+$",
         "value":"library.json","description":"...","evidence":"..."}

        An archive is sometimes wrapped in a single folder. Then root means
        that wrapping folder itself, not what is inside it. Use where to
        point inside the wrapper.

        Write description as one English sentence stating the rule.
        Write evidence as a short note on where in the listing you read it.

        What to look for:
        - Shapes that repeat. Sibling folders holding the same set of files, and the like
        - Where things live. A kind of file gathered in one settled place
        - How names are formed. Dates, running numbers, prefixes - names of one shape
        - What is absent. No scratch files, no files the OS leaves behind

        Rules to follow:
        - Base every rule only on names that appear in the listing
        - The listing may be trimmed. Where it says "N more not sent", you cannot see those
        - Do not turn a name unique to this archive into a rule.
          "There is a Foo-main folder" is useless next time, when that name differs.
          If the shape repeats, write it as a name_pattern instead
        - A name_pattern must match every name in its scope that appears in the listing
        - Do not list a regular expression that matches anything (such as ".*")
        - Do not hold back on count. List what you notice, up to 20 rules
        """;

    public static string SaveTooltip => Pick(
        "変更を親の書庫に反映する (Ctrl+S)",
        "Put the changes back into the parent archive (Ctrl+S)");

    public static string SaveAs => Pick("名前を付けて保存", "Save as");

    public static string SaveAsTooltip => Pick(
        "親の書庫のタブが閉じられているため、別のファイルとして保存する (Ctrl+S)",
        "The parent archive's tab has been closed, so save this as a separate file (Ctrl+S)");

    public static string SaveAsDialogTitle => Pick(
        "中の書庫に名前を付けて保存", "Save the inner archive as");

    /// <summary>親のタブが閉じられ、上書き保存ができなくなったときの知らせ (#30)。</summary>
    public static string NestOrphaned(string name, string parentName) => Pick(
        $"{parentName} のタブが閉じられたため、{name} は「名前を付けて保存」でのみ保存できます",
        $"The tab for {parentName} was closed, so {name} can only be saved as a separate file");

    public static string NestSavedAs(string path) => Pick(
        $"{path} に保存しました", $"Saved to {path}");

    public static string NestSaveAsFailed(string path, string reason) => Pick(
        $"{path} に保存できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Could not save to {path}.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ConfirmApplyNest(string entryPath, string parentName) => Pick(
        $"{entryPath}{Environment.NewLine}{Environment.NewLine}"
        + $"変更されています。{parentName} に反映しますか?{Environment.NewLine}{Environment.NewLine}"
        + "「いいえ」を選ぶと、変更内容は失われます。",
        $"{entryPath}{Environment.NewLine}{Environment.NewLine}"
        + $"This has been changed. Put it back into {parentName}?{Environment.NewLine}{Environment.NewLine}"
        + "Choosing No discards the changes.");

    public static string NestApplied(string entryPath, string parentName) => Pick(
        $"{entryPath} を {parentName} に反映しました",
        $"Put {entryPath} back into {parentName}");

    public static string NestNoChanges => Pick(
        "変更されていないため、反映する内容はありません",
        "Nothing to put back - it has not been changed");

    /// <summary>親書庫のタブが先に閉じられていた場合 (#30)。</summary>
    /// <remarks>
    /// 開いているタブが無いと、パスワードが要るかどうかも分からないまま
    /// 書き込むことになる。仕様書 4.3節 でも、親を先に閉じた場合は上書き保存
    /// できないと決めてある。
    /// </remarks>
    public static string NestParentClosed(string parentName) => Pick(
        $"{parentName} のタブが閉じられているため、反映できません。"
        + $"{parentName} を開き直してから、もう一度保存してください。",
        $"The tab for {parentName} has been closed, so the changes cannot be put back. "
        + $"Open {parentName} again and save once more.");

    public static string ReloadedAfterNestApply(string name) => Pick(
        $"中の書庫を反映したため、{name} を読み込み直しました",
        $"Reloaded {name} after putting the inner archive back");

    public static string ConfirmApplyEdit(string entryPath) => Pick(
        $"{entryPath}{Environment.NewLine}{Environment.NewLine}"
        + $"変更されました。書庫に反映しますか?{Environment.NewLine}"
        + "「いいえ」を選んでも変更内容は保持しています。Expzip の終了時に再度確認します。",
        $"{entryPath}{Environment.NewLine}{Environment.NewLine}"
        + $"This file was edited. Put it back into the archive?{Environment.NewLine}{Environment.NewLine}"
        + "Choosing No keeps your edits; you will be asked again when you quit.");

    public static string ApplyingEdit(string name) => Pick(
        $"{name} を書庫に反映しています…", $"Putting {name} back into the archive…");

    public static string ApplyEditFailed(string entryPath, string reason) => Pick(
        $"{entryPath} を書庫に反映できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Could not put {entryPath} back into the archive.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ApplyEditCancelled => Pick("反映を中断しました", "Putting it back was stopped");

    public static string ApplyEditDone(string name) => Pick(
        $"{name} を書庫に反映しました", $"Put {name} back into the archive");

    public static string ConfirmPendingEdits(string names) => Pick(
        $"書庫に反映していない変更があります。{Environment.NewLine}{Environment.NewLine}{names}"
        + $"{Environment.NewLine}{Environment.NewLine}反映してから終了しますか?{Environment.NewLine}"
        + "「いいえ」を選ぶと、変更内容は失われます。",
        $"Some edits have not been put back into the archive."
        + $"{Environment.NewLine}{Environment.NewLine}{names}"
        + $"{Environment.NewLine}{Environment.NewLine}Put them back before quitting?{Environment.NewLine}"
        + "Choosing No discards those edits.");

    public static string CannotOpenOutsidePath(string sourceName) => Pick(
        $"このファイルは開けません。{Environment.NewLine}{Environment.NewLine}"
        + $"{sourceName}{Environment.NewLine}{Environment.NewLine}"
        + "書庫の外を指すパスが指定されています。",
        $"This file cannot be opened.{Environment.NewLine}{Environment.NewLine}"
        + $"{sourceName}{Environment.NewLine}{Environment.NewLine}"
        + "Its path points outside the archive.");

    public static string ConfirmExecutable(string fileName) => Pick(
        $"{fileName}{Environment.NewLine}{Environment.NewLine}"
        + "このファイルを開くとプログラムとして実行されます。入手元が不明な書庫の場合は、開かないでください。"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "続行しますか?",
        $"{fileName}{Environment.NewLine}{Environment.NewLine}"
        + $"Opening this file will run it.{Environment.NewLine}"
        + $"Do not open it if you are unsure where the archive came from."
        + $"{Environment.NewLine}{Environment.NewLine}Continue?");

    public static string TempUnavailable => Pick(
        "一時フォルダーを使用できないため、ファイルを開けません",
        "Files cannot be opened because the temporary folder is unavailable");

    public static string TempUnavailableDetail(string root, string reason) => Pick(
        $"ファイルを開くための一時フォルダーを作成できませんでした。{Environment.NewLine}"
        + $"ツールバーの「展開」からは展開できます。{Environment.NewLine}{Environment.NewLine}"
        + $"{root}{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The temporary folder used to open files could not be prepared.{Environment.NewLine}"
        + $"You can still get the files out with Extract.{Environment.NewLine}{Environment.NewLine}"
        + $"{root}{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ExtractingOne(string name) => Pick(
        $"{name} を展開しています…", $"Extracting {name}…");

    public static string ExtractOneCancelled => Pick(
        "展開を中断しました", "Extracting was stopped");

    public static string ExtractOneFailedReason => Pick(
        "書庫から展開できませんでした。", "It could not be extracted from the archive.");

    public static string OpenEntryFailed(string name, string reason) => Pick(
        $"{name} を開けませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"{name} could not be opened.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string OpenedWithDefaultApp(string fileName) => Pick(
        $"{fileName} を既定のアプリで開きました",
        $"Opened {fileName} with its default app");

    public static string OpenLaunchCancelled => Pick(
        "キャンセルしました", "Cancelled");

    public static string DefaultAppFailed(string reason) => Pick(
        $"既定のアプリで開けませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"It could not be opened with the default app.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ChooseAppPrompt(string fileName) => Pick(
        $"{fileName} を開くアプリを選択してください",
        $"Choose an app to open {fileName}");

    public static string NoAppFound(string reason) => Pick(
        $"このファイルを開けるアプリが見つかりませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"No app was found that can open this file.{Environment.NewLine}{Environment.NewLine}{reason}");

    // ------------------------------------------------------------------ ドラッグでの取り出し (#17)

    public static string NothingToExtract => Pick(
        "展開できるファイルがありません", "There are no files to extract");

    public static string DragCancelled => Pick(
        "展開を中止しました", "Extracting was called off");

    public static string ExtractingCount(int count) => Pick(
        $"{count:N0} 個の項目を展開しています…", $"Extracting {count:N0}…");

    public static string DragExtractFailed(string reason) => Pick(
        $"展開に失敗しました。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Extracting failed.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string DragCannotStart => Pick(
        "展開できません",
        "Could not extract");

    public static string DragExtracted(int count) => Pick(
        $"{count:N0} 個の項目を展開しました",
        $"Extracted {count:N0} {Plural(count, "item", "items")}");

    public static string DragFailed => Pick(
        "ドラッグを完了できませんでした", "The drag could not be completed");

    public static string DragTooLarge(
        int fileLimit, long megabyteLimit, int count, long megabytes) => Pick(
        $"ドラッグで展開できるのは {fileLimit:N0} 個 / {megabyteLimit:N0} MB までです。"
        + $"{Environment.NewLine}選択されているのは {count:N0} 個 / {megabytes:N0} MB です。"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "ツールバーの「展開」を使用してください。",
        $"Dragging can extract up to {fileLimit:N0} items / {megabyteLimit:N0}MB."
        + $"{Environment.NewLine}You have selected {count:N0} items / {megabytes:N0}MB."
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "Use Extract on the toolbar instead.");

    // ------------------------------------------------------------------ 書庫内の移動 (#43)

    public static string MoveConflict(string names) => Pick(
        $"移動先に同じ名前の項目があります。{Environment.NewLine}{Environment.NewLine}{names}"
        + $"{Environment.NewLine}{Environment.NewLine}名前を変更してから移動してください。",
        $"The destination already contains items with the same name."
        + $"{Environment.NewLine}{Environment.NewLine}{names}"
        + $"{Environment.NewLine}{Environment.NewLine}Rename them before moving.");

    public static string Moving(int done) => Pick(
        $"移動しています… ({done:N0} 個)", $"Moving… ({done:N0})");

    public static string MoveFailed(string reason) => Pick(
        $"移動できませんでした。書庫の変更はありません。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The items could not be moved. The archive was left untouched.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string MoveCancelled => Pick("移動を中断しました", "Moving was stopped");

    public static string MoveDone(int count) => Pick(
        $"{count:N0} 個の項目を移動しました",
        $"Moved {count:N0} {Plural(count, "item", "items")}");

    // ------------------------------------------------------------------ 展開

    public static string ExtractSelectedTitle => Pick(
        "選択した項目の展開先を選択", "Select where to extract the selected items");

    public static string ExtractFolderTitle(string name) => Pick(
        $"「{name}」の展開先を選択", $"Select where to extract \"{name}\"");

    public static string ExtractAllTitle => Pick(
        "書庫全体の展開先を選択", "Select where to extract the whole archive");

    public static string ConfirmOverwrite(int count, string preview, string more) => Pick(
        $"展開先に同じ名前のファイルが {count:N0} 個あります。上書きしますか?"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "「いいえ」を選ぶと、それらは展開しません。",
        $"The destination already has {count:N0} {Plural(count, "file", "files")} "
        + $"with the same name. Overwrite {Plural(count, "it", "them")}?"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "Choosing No leaves those files alone and skips them.");

    public static string ExtractCalledOff => Pick(
        "展開を中止しました", "Extracting was called off");

    public static string Extracting(string name) => Pick(
        $"展開中: {name}", $"Extracting: {name}");

    public static string ExtractFailed(string reason) => Pick(
        $"展開に失敗しました。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Extracting failed.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ExtractCancelledStatus(int extracted) => Pick(
        $"展開を中断しました ({extracted:N0} 個展開済み)",
        $"Extracting was stopped ({extracted:N0} extracted)");

    public static string ExtractDone(int extracted) => Pick(
        $"{extracted:N0} 個のファイルを展開しました",
        $"Extracted {extracted:N0} {Plural(extracted, "file", "files")}");

    public static string ExtractCancelledLine1 => Pick(
        "展開を中断しました。", "Extracting was stopped.");

    public static string ExtractCancelledLine2 => Pick(
        "中断するまでに展開したファイルは、そのまま残っています。",
        "The files extracted before it stopped were left in place.");

    public static string ExtractCancelledLine3 => Pick(
        "書き込み途中のファイルは削除しました。",
        "The file being written at that moment was deleted.");

    public static string ExtractDestinationLine(string destination) => Pick(
        $"展開先: {destination}", $"Destination: {destination}");

    public static string ExtractedFilesLine(int count) => Pick(
        $"展開したファイル: {count:N0} 個", $"Files extracted: {count:N0}");

    public static string SkippedFilesLine(int count) => Pick(
        $"上書きせずそのまま残したファイル: {count:N0} 個",
        $"Files skipped instead of overwritten: {count:N0}");

    public static string RejectedFilesLine(int count) => Pick(
        $"安全でないパスのため展開しなかったファイル: {count:N0} 個",
        $"Files not extracted because of an unsafe path: {count:N0}");

    public static string RejectedFilesDetail => Pick(
        "展開先の外に書き込もうとする項目が含まれていました。",
        "The archive contained entries that would write outside the destination.");

    public static string NotWrittenFilesLine(int count) => Pick(
        $"書き込めなかったファイル: {count:N0} 個",
        $"Files that could not be written: {count:N0}");

    // ------------------------------------------------------------------ 書庫検査 (#53)

    public static string Inspect => Pick("検査", "Inspect");

    public static string InspectTooltip => Pick(
        "この書庫が壊れていないか、危険なものが入っていないかを調べる",
        "Check this archive for damage and for content that could cause trouble");

    /// <summary>検査中のステータスバー。区切りごとに何をしているかを出す。</summary>
    public static string Inspecting(InspectionPhase phase, string name) => phase switch
    {
        InspectionPhase.Structure => Pick(
            "検査中: 索引とヘッダーを照合しています",
            "Inspecting: comparing the index against the entry headers"),

        InspectionPhase.Safety => Pick(
            "検査中: 名前とサイズを調べています",
            "Inspecting: checking names and sizes"),

        _ => name.Length == 0
            ? Pick("検査中: 中身を読み込んでいます", "Inspecting: reading the contents")
            : Pick($"検査中: {name}", $"Inspecting: {name}"),
    };

    public static string InspectClean => Pick(
        "検査しました。問題は見つかりませんでした",
        "Inspection finished. Nothing to report.");

    public static string InspectFound(int count) => Pick(
        $"検査しました。{count:N0} 件見つかりました",
        $"Inspection finished. {count:N0} {Plural(count, "item", "items")} to report.");

    public static string InspectCancelledStatus => Pick(
        "検査を中断しました", "The inspection was stopped");

    public static string InspectFailed(string reason) => Pick(
        $"検査できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The archive could not be inspected.{Environment.NewLine}{Environment.NewLine}{reason}");

    // ------------------------------------------------------------------ 検査結果の画面 (#57)

    public static string InspectionTitle(string archiveName) => Pick(
        $"検査結果 - {archiveName}", $"Inspection results - {archiveName}");

    public static string InspectionColumnSeverity => Pick("重大度", "Severity");

    public static string InspectionColumnTarget => Pick("対象", "Item");

    public static string InspectionColumnDetail => Pick("内容", "What was found");

    public static string InspectionClose => Pick("閉じる", "Close");

    public static string SeverityName(InspectionSeverity severity) => severity switch
    {
        InspectionSeverity.Danger => Pick("危険", "Danger"),
        InspectionSeverity.Warning => Pick("注意", "Caution"),
        _ => Pick("問題なし", "All clear"),
    };

    /// <summary>書庫そのものが対象のときに、対象の欄へ出す文字。</summary>
    public static string InspectionArchiveItself => Pick(
        "(書庫そのもの)", "(the archive itself)");

    /// <summary>見出し。いちばん重い結果を一言で表す。</summary>
    public static string InspectionHeadline(int danger, int warning) => (danger, warning) switch
    {
        ( > 0, > 0) => Pick(
            $"危険 {danger:N0} 件、注意 {warning:N0} 件が見つかりました",
            $"Found {danger:N0} dangerous and {warning:N0} questionable "
            + $"{Plural(danger + warning, "item", "items")}"),

        ( > 0, _) => Pick(
            $"危険な項目が {danger:N0} 件見つかりました",
            $"Found {danger:N0} dangerous {Plural(danger, "item", "items")}"),

        (_, > 0) => Pick(
            $"注意したい項目が {warning:N0} 件見つかりました",
            $"Found {warning:N0} questionable {Plural(warning, "item", "items")}"),

        _ => Pick("問題は見つかりませんでした", "No problems were found"),
    };

    /// <summary>
    /// 何をどれだけ調べたかを開くための見出し (#57)。
    /// </summary>
    /// <remarks>
    /// 既定では畳んでおく。変わるのは数だけで、利用者が知りたいのは結末のほう。
    /// 見たい人が開けるようにはしておく。
    /// </remarks>
    public static string InspectionDetails => Pick("調べた内容", "What was checked");

    /// <summary>
    /// 行った検査の種類。
    /// </summary>
    /// <remarks>
    /// 何も出ないと検査が働いたのか分からないため、問題が無かった場合でも必ず出す (#57)。
    /// </remarks>
    public static string InspectionChecksLine => Pick(
        "行った検査: 構造 (索引・ヘッダー・CRC の照合)、安全性 (パス・名前・圧縮率)、マルウェア (AMSI)",
        "Checks performed: structure (index, headers, CRC), "
        + "safety (paths, names, ratios), malware (AMSI)");

    public static string InspectionChecksLineWithoutMalware => Pick(
        "行った検査: 構造 (索引・ヘッダー・CRC の照合)、安全性 (パス・名前・圧縮率)",
        "Checks performed: structure (index, headers, CRC), safety (paths, names, ratios)");

    public static string InspectionContentsLine(int checkedCount, int fileCount) => Pick(
        $"{fileCount:N0} 個のファイルのうち、{checkedCount:N0} 個は中身まで読み込んで確認しました。",
        $"Read and verified the contents of {checkedCount:N0} of {fileCount:N0} "
        + $"{Plural(fileCount, "file", "files")}.");

    public static string InspectionMalwareLine(int scanned) => Pick(
        $"うち {scanned:N0} 個をウイルス対策ソフトで検査しました。書庫ファイル自体も検査しています。",
        $"Of those, {scanned:N0} {Plural(scanned, "was", "were")} handed to the antimalware "
        + "service. The archive file itself was handed over as well.");

    /// <summary>マルウェア検査が使えなかったことを、黙って省かずに出す (#56、#57)。</summary>
    public static string InspectionMalwareUnavailable => Pick(
        "マルウェアの検査は行えませんでした。この PC のウイルス対策ソフトが、Windows の検査機能 (AMSI) "
        + "に対応していません。ほかの検査は行っています。",
        "The malware check could not run: the antimalware software on this machine does not "
        + "answer Windows' scan interface (AMSI). The other checks did run.");

    public static string InspectionCancelledLine => Pick(
        "検査は途中で中断されました。ここに表示しているのは、中断するまでに検査した範囲の結果です。",
        "The inspection was stopped partway. What follows covers only what it reached.");

    public static string InspectionElapsedLine(TimeSpan elapsed) => elapsed.TotalSeconds < 1
        ? Pick(
            $"かかった時間: {elapsed.TotalMilliseconds:N0} ミリ秒",
            $"Time taken: {elapsed.TotalMilliseconds:N0} ms")
        : Pick(
            $"かかった時間: {elapsed.TotalSeconds:N1} 秒",
            $"Time taken: {elapsed.TotalSeconds:N1} s");

    public static string InspectionNoProblems => Pick(
        "問題は見つかりませんでした", "Nothing to report");

    /// <summary>同じ種類が多すぎて省いた分をまとめる1行。</summary>
    public static string InspectionMoreLine(int count) => Pick(
        $"同じものがほかに {count:N0} 件あります",
        $"{count:N0} more like this");

    /// <summary>見つかった事柄の説明 (#54、#55、#56)。</summary>
    public static string InspectionMessage(InspectionIssue issue, string? detail) => issue switch
    {
        InspectionIssue.CrcMismatch => Pick(
            "中身が壊れています。書庫に記録された CRC と一致しません",
            "The contents are damaged: they do not match the checksum recorded in the archive"),

        InspectionIssue.HeaderMismatch => Pick(
            $"書庫の索引と項目のヘッダーが一致しません{Paren(detail)}",
            $"The archive index and the entry header disagree{Paren(detail)}"),

        InspectionIssue.Truncated => Pick(
            "書庫が途中で切れています",
            "The archive is cut short: it ends before the point its own index refers to"),

        InspectionIssue.TrailingData => Pick(
            $"書庫の末尾のあとに、書庫ではないデータが {detail} バイトあります",
            $"{detail} bytes that are not part of the archive follow its end"),

        InspectionIssue.UnsupportedMethod => Pick(
            $"対応していない圧縮方式のため展開できません{Paren(detail)}",
            $"Stored with a compression method Expzip cannot read{Paren(detail)}"),

        InspectionIssue.DuplicateName => Pick(
            "同じ名前の項目が 2 個以上あります。展開すると 1 個しか残りません",
            "More than one item has this name. Extracting leaves only one of them."),

        InspectionIssue.Unreadable => Pick(
            $"中身を読み出せませんでした{Paren(detail)}",
            $"The contents could not be read{Paren(detail)}"),

        InspectionIssue.EncryptedNotChecked => Pick(
            "パスワードが不明なため、中身を検査できませんでした",
            "The contents could not be checked: the password is not known"),

        InspectionIssue.EscapingPath => Pick(
            $"展開先の外に書き込もうとするパスです{Paren(detail)}",
            $"This path would write outside the folder you extract into{Paren(detail)}"),

        InspectionIssue.SuspiciousPath => Pick(
            $"通常の書庫にはない形式のパスです{Paren(detail)}",
            $"This path is not shaped like anything a normal archive holds{Paren(detail)}"),

        InspectionIssue.ReservedName => Pick(
            $"Windows が予約している名前のため、このファイルは作成できません{Paren(detail)}",
            $"Windows treats this as a device name, so the file cannot be created{Paren(detail)}"),

        InspectionIssue.TrailingSpaceOrDot => Pick(
            $"名前の末尾が空白またはピリオドのため、Windows ではこの名前のファイルを作成できません{Paren(detail)}",
            $"Windows cannot create this name: it ends with a space or a period{Paren(detail)}"),

        InspectionIssue.ControlCharacter => Pick(
            "名前に制御文字が含まれています。表示されている名前と実際の名前が異なります",
            "The name contains control characters, so what you see is not the real name"),

        InspectionIssue.InvalidCharacter => Pick(
            $"Windows のファイル名に使用できない文字が含まれています{Paren(detail)}",
            $"The name contains characters Windows does not allow in a file name{Paren(detail)}"),

        InspectionIssue.CaseCollision => Pick(
            $"大文字と小文字だけが異なる項目があります{Paren(detail)}。展開すると片方が失われます",
            $"Another item differs only in letter case{Paren(detail)}. "
            + "Extracting loses one of them."),

        InspectionIssue.BidiOverride => Pick(
            "文字の向きを変える記号で拡張子が偽装されています。表示されている拡張子と実際の拡張子が異なります",
            "A text-direction override disguises the extension: "
            + "what you see is not the real extension"),

        InspectionIssue.HighRatio => Pick(
            $"展開するとサイズが {detail} 倍になります",
            $"This expands to {detail} times its stored size"),

        InspectionIssue.ZipBomb => Pick(
            $"書庫全体を展開するとサイズが {detail} 倍になります。展開先の空き容量に注意してください",
            $"The whole archive expands to {detail} times its size. "
            + "Watch the free space where you extract it."),

        InspectionIssue.ExecutableExtension => Pick(
            $"開くとプログラムとして実行される種類のファイルです{Paren(detail)}",
            $"Opening this runs it instead of showing its contents{Paren(detail)}"),

        InspectionIssue.MalwareDetected => Pick(
            "ウイルス対策ソフトが脅威として検出しました",
            "The antimalware service flagged this"),

        InspectionIssue.TooLargeToScan => Pick(
            $"サイズが大きすぎるため、マルウェアの検査を行えませんでした{Paren(detail)}",
            $"Too large to hand over in one piece, so the malware check was skipped{Paren(detail)}"),

        _ => string.Empty,
    };

    // ------------------------------------------------------------------ ファイルの分割 (#59)

    public static string Split => Pick("分割", "Split");

    public static string SplitTooltip => Pick(
        "ファイルを指定した大きさに分割する。つなぎ直すためのプログラムも作成する",
        "Split a large file into fixed-size pieces, with a program to put them back together");

    public static string SplitTitle => Pick("ファイルの分割", "Split a file");

    public static string SplitSourceLabel => Pick("分割するファイル", "File");

    public static string SplitDestinationLabel => Pick("保存先", "Put pieces in");

    public static string SplitSizeLabel => Pick("1つの大きさ", "Piece size");

    public static string SplitBrowse => Pick("参照", "Browse");

    public static string SplitStart => Pick("分割", "Split");

    public static string SplitCancel => Pick("キャンセル", "Cancel");

    public static string SplitUnitKilobytes => "KB";

    public static string SplitUnitMegabytes => "MB";

    public static string SplitUnitGigabytes => "GB";

    public static string SplitSourceTitle => Pick(
        "分割するファイルを選択", "Choose the file to split");

    public static string SplitDestinationTitle => Pick(
        "分割ファイルの保存先を選択", "Choose where to put the pieces");

    public static string SplitAnyFile => Pick(
        "すべてのファイル|*.*", "All files|*.*");

    public static string SplitSourceMissing => Pick(
        "分割するファイルを選択してください。", "Choose the file you want to split.");

    public static string SplitDestinationMissing => Pick(
        "分割ファイルの保存先を選択してください。", "Choose where the pieces should go.");

    public static string SplitTooSmall(long minimum) => Pick(
        $"1つの大きさは {minimum / 1024:N0} KB 以上にしてください。",
        $"Each piece must be at least {minimum / 1024:N0} KB.");

    /// <summary>分割しても 1 つにしかならないときの断り。</summary>
    public static string SplitNotNeeded => Pick(
        "分割の必要はありません。1つの大きさが元のファイルより大きくなっています。",
        "No need to split: each piece would be larger than the file itself.");

    public static string SplitPreview(int parts) => Pick(
        $"{parts:N0} 個の分割ファイルと、つなぎ直すためのプログラムを作成します。元のファイルはそのまま残ります。",
        $"This makes {parts:N0} {Plural(parts, "piece", "pieces")} plus one program to put "
        + "them back together. The original file is left alone.");

    public static string Splitting(string name) => Pick(
        $"分割中: {name}", $"Splitting: {name}");

    public static string SplitCancelledStatus => Pick(
        "分割を中断しました", "Splitting was stopped");

    public static string SplitDoneStatus(int parts) => Pick(
        $"{parts:N0} 個に分割しました",
        $"Split into {parts:N0} {Plural(parts, "piece", "pieces")}");

    public static string SplitDone(int parts, string joiner, string destination) => Pick(
        $"{parts:N0} 個に分割しました。{Environment.NewLine}{Environment.NewLine}"
        + $"保存先: {destination}{Environment.NewLine}{Environment.NewLine}"
        + $"元に戻すときは、すべての分割ファイルを同じフォルダーに置いて「{joiner}」を実行してください。"
        + $"{Environment.NewLine}"
        + "7-Zip などほかのソフトでもつなぎ直せます。",
        $"Split into {parts:N0} {Plural(parts, "piece", "pieces")}."
        + $"{Environment.NewLine}{Environment.NewLine}"
        + $"Location: {destination}{Environment.NewLine}{Environment.NewLine}"
        + $"To put it back together, keep every piece in one folder and run \"{joiner}\"."
        + $"{Environment.NewLine}"
        + "Other tools such as 7-Zip can join them too.");

    public static string SplitCancelled => Pick(
        "分割を中断しました。書き込み途中の分割ファイルは削除しました。",
        "Splitting was stopped. The half-written pieces were deleted.");

    public static string SplitFailed(string reason) => Pick(
        $"分割できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The file could not be split.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string SplitSourceShrank => Pick(
        "分割中に元のファイルを読み込めなくなりました。",
        "The file became unreadable while it was being split.");

    // ------------------------------------------------------------------ 共通

    /// <summary>一覧に添える箇条書きの印。</summary>
    public static string Bullet => Pick("・", "- ");

    /// <summary>
    /// 7z / tar の展開が合言葉を求められて止まったときの理由 (#19)。
    /// </summary>
    /// <remarks>
    /// ZIP は合言葉を尋ねて展開できる (#20) が、7z の復号には対応していない。
    /// </remarks>
    public static string PasswordNotSupported => Pick(
        "パスワードが必要です。パスワード付き書庫の展開には対応していません。",
        "A password is required. Extracting password-protected archives of this "
        + "format is not supported.");

    /// <summary>取り出したファイルの置き場を作れなかったときの理由 (#12)。</summary>
    public static string TempWorkspaceFailed(string root) => Pick(
        $"一時フォルダーを作成できませんでした。{Environment.NewLine}{root}",
        $"Could not create a place to put temporary files.{Environment.NewLine}{root}");

    /// <summary>「この名前は この理由で駄目だった」の1行。</summary>
    public static string FailureLine(string name, string reason) => Pick(
        $"  {name} … {reason}", $"  {name} — {reason}");

    public static string More(int count) => Pick(
        $"{Environment.NewLine}  ほか {count:N0} 個",
        $"{Environment.NewLine}  and {count:N0} more");

    public static string Stopping => Pick("中断しています…", "Stopping…");

    /// <summary>知らない圧縮方式で取り出せなかったことを知らせる (#66)。</summary>
    public static string UnsupportedCompressionMethod => Pick(
        "この圧縮方式には対応していません",
        "This compression method is not supported.");

    /// <summary>外での書き換えに追随できなかったことを知らせる (#64)。</summary>
    public static string ReloadFailed(string name, string reason) => Pick(
        $"{name} を読み込み直せませんでした ({reason})。表示は変更前のままです",
        $"Could not reload {name} ({reason}). The list still shows the earlier contents.");

    /// <summary>外で書き換わった書庫を読み直したことを知らせる (#64)。</summary>
    public static string ReloadedAfterExternalChange(string name) => Pick(
        $"{name} がほかのプログラムで変更されたため、表示を更新しました",
        $"{name} changed outside Expzip, so the list was refreshed.");

    public static string RecoveredFromError => Pick(
        "処理を中断しました", "The operation was stopped");

    public static string UnhandledError(string type, string message) => Pick(
        $"処理中に問題が発生しました。書庫の変更はありません。{Environment.NewLine}{Environment.NewLine}"
        + $"{type}{Environment.NewLine}{message}",
        $"Something went wrong during the operation. The archive was left untouched."
        + $"{Environment.NewLine}{Environment.NewLine}"
        + $"{type}{Environment.NewLine}{message}");

    /// <summary>書庫の形式の名前。ZIP や 7z は訳さない。</summary>
    public static string FormatName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Tar => "tar",
        ArchiveFormat.Nsis => "NSIS",
        _ => FormatUnknown,
    };

    // ------------------------------------------------------------------ 失敗の理由 (#106)

    /// <summary>
    /// 失敗の理由を、利用者が次にすることを決められる言葉にする (#106)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **例外の文をそのまま出さない。**.NET の例外の文は、日本語の Windows でも英語で来る。
    /// 「Access to the path ... is denied.」のまま日本語の文の中に差し込まれていた。
    /// </para>
    /// <para>
    /// よくある失敗 (書き込めない・使用中・見つからない・空きが無い・壊れている) だけを
    /// 言い直し、それ以外は元の文を出す。黙って消すと、不具合を伝えてもらう手掛かりが無くなる。
    /// </para>
    /// </remarks>
    public static string Reason(Exception error)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        const int HandleDiskFull = unchecked((int)0x80070027);
        const int DiskFull = unchecked((int)0x80070070);

        // 例外の文に 'C:\...' の形でパスが入っていれば、それを使う
        var quoted = System.Text.RegularExpressions.Regex.Match(error.Message, "'([^']+)'");
        var path = quoted.Success ? quoted.Groups[1].Value : null;

        return error switch
        {
            UnauthorizedAccessException => path is null
                ? Pick("アクセスが拒否されました。", "Access was denied.")
                : Pick($"{path} へのアクセスが拒否されました。", $"Access to {path} was denied."),

            FileNotFoundException or DirectoryNotFoundException => path is null
                ? Pick("ファイルまたはフォルダーが見つかりません。", "The file or folder was not found.")
                : Pick($"{path} が見つかりません。", $"{path} was not found."),

            PathTooLongException => Pick("パスが長すぎます。", "The path is too long."),

            IOException { HResult: SharingViolation or LockViolation } => path is null
                ? Pick("ほかのプログラムが使用中のため、アクセスできません。",
                    "Another program is using the file.")
                : Pick($"{path} は、ほかのプログラムが使用中のためアクセスできません。",
                    $"{path} is being used by another program."),

            IOException { HResult: DiskFull or HandleDiskFull } => Pick(
                "ディスクの空き容量が足りません。", "There is not enough free disk space."),

            InvalidDataException => Pick(
                "書庫が壊れているか、対応していない形式です。",
                "The archive is damaged or in an unsupported format."),

            System.Net.Http.HttpRequestException => Pick(
                "接続先に接続できませんでした。URL とネットワークを確認してください。",
                "Could not connect. Check the URL and your network."),

            _ => error.Message,
        };
    }

    /// <summary>
    /// 実行ファイルに入れてあるはずのものが無いとき (#106)。組み立てを間違えない限り出ない。
    /// </summary>
    public static string EmbeddedToolMissing(string resource) => Pick(
        $"Expzip.exe に必要なファイルが含まれていません ({resource})。",
        $"A file Expzip.exe needs is missing from it ({resource}).");
}
