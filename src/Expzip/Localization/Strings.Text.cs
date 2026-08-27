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

    public static string RecentTooltip => Pick("最近使った書庫", "Recently opened archives");

    public static string Extract => Pick("展開", "Extract");

    public static string ExtractTooltip => Pick(
        "選択した項目を展開する。選択していなければ書庫全体を展開する",
        "Extract the selected items. With nothing selected, extracts the whole archive.");

    public static string Add => Pick("追加", "Add");

    public static string AddTooltip => Pick(
        "ファイルを書庫に追加する。一覧にドラッグしても追加できる",
        "Add files to the archive. Dragging them onto the list works too.");

    public static string Password => Pick("パスワード", "Password");

    public static string PasswordTooltip => Pick(
        "この書庫のパスワードを付ける・変える・外す",
        "Set, change, or remove this archive's password");

    public static string SettingsTooltip => Pick("設定", "Settings");

    // ------------------------------------------------------------------ 言語の切り替え (#23)

    public static string LanguageMenu => Pick("言語", "Language");

    public static string LanguageAuto => Pick("OS に合わせる", "Match Windows");

    /// <summary>言語の名前は、その言語で書く。探している人が読める形にするため。</summary>
    public static string LanguageJapanese => "日本語";

    public static string LanguageEnglish => "English";

    // ------------------------------------------------------------------ アドレスバーとタブ

    public static string LocationLabel => Pick("場所", "Location");

    public static string NewTabTooltip => Pick(
        "新しい書庫を作って開く", "Create and open a new archive");

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

    public static string MenuNewFolder => Pick("新しいフォルダ", "New folder");

    /// <summary>書庫を読み直す (#64)。エクスプローラーと同じ文言にする。</summary>
    public static string MenuRefresh => Pick("最新の情報に更新", "Refresh");

    // ------------------------------------------------------------------ ステータスバー

    public static string NoArchiveOpen => Pick("書庫が開かれていません", "No archive is open");

    public static string EmptyFolder => Pick("このフォルダは空です", "This folder is empty");

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
        $"パスが通常ではない項目が {count:N0} 件あります",
        $"{count:N0} {Plural(count, "entry has", "entries have")} an unusual path");

    /// <summary>書庫にできることの断り書き。件数の後ろに添える。</summary>
    public static string LimitEncryptedAes => Pick(" (パスワード付き / AES)", " (password protected / AES)");

    public static string LimitEncrypted => Pick(" (パスワード付き)", " (password protected)");

    public static string LimitReadOnly(string format) => Pick(
        $" ({format} は読み取りのみに対応)", $" ({format} is read-only)");

    public static string EncryptedTooltip => Pick(
        "この項目はパスワードで保護されています。取り出すときにパスワードを尋ねます。",
        "This item is protected with a password. You will be asked for it when extracting.");

    public static string SuspiciousPathTooltipLine1 => Pick(
        "このパスは通常の書庫では使われない形式です。",
        "This path is not in a form normally used by archives.");

    public static string SuspiciousPathTooltipLine2 => Pick(
        "展開しても、選んだフォルダの外には書き出されません。",
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

    public static string CompressionLevelLabel(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => Pick("格納のみ", "Store only"),
        _ => Pick("圧縮する", "Compress"),
    };

    // ------------------------------------------------------------------ 書庫の形式 (#19)

    public static string FormatUnknown => Pick("不明", "Unknown");

    /// <summary>「開く」ダイアログの絞り込み。</summary>
    public static string OpenFilter => Pick(
        "書庫ファイル|*.zip;*.7z;*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz"
        + "|ZIP書庫 (*.zip)|*.zip"
        + "|7z書庫 (*.7z)|*.7z"
        + "|tar書庫 (*.tar;*.tar.gz;*.tar.bz2;*.tar.xz)|*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz"
        + "|すべてのファイル (*.*)|*.*",
        "Archive files|*.zip;*.7z;*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz"
        + "|ZIP archives (*.zip)|*.zip"
        + "|7z archives (*.7z)|*.7z"
        + "|tar archives (*.tar;*.tar.gz;*.tar.bz2;*.tar.xz)|*.tar;*.tar.gz;*.tgz;*.tar.bz2;*.tbz;*.tbz2;*.tar.xz;*.txz"
        + "|All files (*.*)|*.*");

    public static string ZipFilter => Pick("ZIP書庫 (*.zip)|*.zip", "ZIP archives (*.zip)|*.zip");

    public static string AllFilesFilter => Pick(
        "すべてのファイル (*.*)|*.*", "All files (*.*)|*.*");

    // ------------------------------------------------------------------ 書庫を開く・作る

    public static string OpenDialogTitle => Pick("書庫を開く", "Open archive");

    public static string NewArchiveDialogTitle => Pick("新しい書庫を作成", "Create a new archive");

    public static string NewArchiveFileName => Pick("新しい書庫.zip", "New archive.zip");

    public static string CreateArchiveFailed(string path, string reason) => Pick(
        $"書庫を作成できませんでした。{Environment.NewLine}{Environment.NewLine}"
        + $"{path}{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The archive could not be created.{Environment.NewLine}{Environment.NewLine}"
        + $"{path}{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string Reading(string fileName, int done, int total) => Pick(
        $"{fileName} を読み込んでいます… ({done:N0} / {total:N0} 件)",
        $"Reading {fileName}… ({done:N0} / {total:N0})");

    public static string ReadCancelled => Pick("読み込みを中断しました", "Reading was stopped");

    public static string OpenArchiveFailed(string path, string reason) => Pick(
        $"書庫を開けませんでした。{Environment.NewLine}{Environment.NewLine}{path}"
        + $"{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The archive could not be opened.{Environment.NewLine}{Environment.NewLine}{path}"
        + $"{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string EncryptedEntriesNotice => Pick(
        $"この書庫には暗号化されたファイルが含まれています。{Environment.NewLine}{Environment.NewLine}"
        + "一覧は読めますが、中身の取り出しには対応していません。",
        $"This archive contains encrypted files.{Environment.NewLine}{Environment.NewLine}"
        + "The list can be read, but extracting their contents is not supported.");

    // ------------------------------------------------------------------ 名前の変更 (#15)

    public static string RenameSeparatorNotAllowed => Pick(
        "名前に \\ と / は使えません。フォルダの移動は名前の変更では行えません。",
        "A name cannot contain \\ or /. Renaming cannot move an item to another folder.");

    public static string RenameReservedName => Pick(
        "その名前は使えません。", "That name cannot be used.");

    public static string RenameInvalidCharacter(char invalid) => Pick(
        $"名前に使えない文字が含まれています ({invalid})。"
        + $"{Environment.NewLine}展開したときにファイルを作れなくなります。",
        $"The name contains a character that cannot be used ({invalid})."
        + $"{Environment.NewLine}Extracting it would fail to create the file.");

    public static string RenameDuplicate(string name) => Pick(
        $"このフォルダには既に「{name}」があります。",
        $"This folder already contains \"{name}\".");

    public static string Renaming(int done) => Pick(
        $"名前を変更しています… ({done:N0} 件)",
        $"Renaming… ({done:N0})");

    public static string RenameFailed(string reason) => Pick(
        $"名前を変更できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The item could not be renamed.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string RenameCancelled => Pick("名前の変更を中断しました", "Renaming was stopped");

    public static string RenameDone(int count) => Pick(
        $"{count:N0} 件の名前を変更しました",
        $"Renamed {count:N0} {Plural(count, "item", "items")}");

    // ------------------------------------------------------------------ フォルダの作成 (#50)

    /// <summary>新しく作るフォルダの仮の名前。エクスプローラーに合わせる。</summary>
    public static string NewFolderName => Pick("新しいフォルダー", "New folder");

    public static string CreatingFolder => Pick("フォルダを作っています…", "Creating the folder…");

    public static string CreateFolderFailed(string reason) => Pick(
        $"フォルダを作れませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The folder could not be created.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string FolderCreated(string name) => Pick(
        $"「{name}」を作りました", $"Created \"{name}\"");

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
        $"「{archiveName}」に付けるパスワードを入力してください。{Environment.NewLine}{Environment.NewLine}"
        + $"入れたファイルは AES-256 で暗号化されます。{Environment.NewLine}"
        + "パスワードを忘れると中身は取り出せません。",
        $"Enter the password to set on \"{archiveName}\".{Environment.NewLine}{Environment.NewLine}"
        + $"Files in it will be encrypted with AES-256.{Environment.NewLine}"
        + "If you forget the password, the contents cannot be recovered.");

    public static string ChangePasswordPrompt(string archiveName) => Pick(
        $"「{archiveName}」の新しいパスワードを入力してください。{Environment.NewLine}{Environment.NewLine}"
        + "空のままにするとパスワードを外します。",
        $"Enter the new password for \"{archiveName}\".{Environment.NewLine}{Environment.NewLine}"
        + "Leave it empty to remove the password.");

    public static string NoPasswordSet => Pick(
        "この書庫にパスワードは付いていません", "This archive has no password");

    public static string PasswordUnchanged => Pick(
        "パスワードは変わっていません", "The password is unchanged");

    public static string PasswordRemoved => Pick(
        "パスワードを外しました", "The password was removed");

    public static string PasswordSet => Pick(
        "パスワードを設定しました", "The password was set");

    public static string PasswordSetAes => Pick(
        "パスワードを設定しました (AES-256)", "The password was set (AES-256)");

    public static string RemovingPassword => Pick(
        "パスワードを外しています…", "Removing the password…");

    public static string ApplyingPassword => Pick(
        "パスワードを付けて書庫を作り直しています…",
        "Rebuilding the archive with the password…");

    public static string Rebuilding(int done) => Pick(
        $"書庫を作り直しています… ({done:N0} 件)",
        $"Rebuilding the archive… ({done:N0})");

    public static string ChangePasswordFailed(string reason) => Pick(
        $"パスワードを変更できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}元の書庫は変更していません。",
        $"The password could not be changed.{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}The original archive was left untouched.");

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
        + $"フォルダの中身を含めて {affected:N0} 個のファイルが削除されます。",
        $"{Environment.NewLine}{Environment.NewLine}"
        + $"{affected:N0} {Plural(affected, "file", "files")} will be deleted, "
        + "including the contents of the folders.");

    public static string Deleting => Pick("削除しています…", "Deleting…");

    public static string DeleteCancelled => Pick("削除を中断しました", "Deleting was stopped");

    public static string DeleteCancelledDetail => Pick(
        $"削除を中断しました。{Environment.NewLine}{Environment.NewLine}書庫は変更していません。",
        $"Deleting was stopped.{Environment.NewLine}{Environment.NewLine}"
        + "The archive was left untouched.");

    public static string DeleteDone(int count) => Pick(
        $"{count:N0} 個の項目を削除しました",
        $"Deleted {count:N0} {Plural(count, "item", "items")}");

    public static string DeleteFailed(string reason) => Pick(
        $"削除できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}元の書庫は変更していません。",
        $"The items could not be deleted.{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}The original archive was left untouched.");

    // ------------------------------------------------------------------ 追加

    public static string AddDialogTitle => Pick(
        "書庫に追加するファイルを選択", "Select the files to add to the archive");

    public static string NoArchiveToAddTo => Pick(
        $"追加先の書庫がありません。{Environment.NewLine}{Environment.NewLine}"
        + "先に書庫を開くか、タブの右の + で作ってください。",
        $"There is no archive to add to.{Environment.NewLine}{Environment.NewLine}"
        + "Open an archive first, or create one with the + next to the tabs.");

    public static string FormatIsReadOnly(string format) => Pick(
        $"{format} 書庫にはファイルを追加できません。{Environment.NewLine}{Environment.NewLine}"
        + "この形式は読み取りのみに対応しています。",
        $"Files cannot be added to {format} archives.{Environment.NewLine}{Environment.NewLine}"
        + "This format is supported for reading only.");

    public static string ConfirmReplace(int count, string preview, string more) => Pick(
        $"同じ名前の項目が書庫内に {count:N0} 件あります。置き換えますか?"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "「いいえ」を選ぶと、それらは書庫内のまま残します。",
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
        $"書庫に追加できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}元の書庫は変更していません。",
        $"The files could not be added to the archive.{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}The original archive was left untouched.");

    public static string AddCancelled => Pick("追加を中断しました", "Adding was stopped");

    public static string AddCancelledDetail => Pick(
        $"追加を中断しました。{Environment.NewLine}{Environment.NewLine}"
        + "書庫は変更していません。作業用の複製に対して処理していたため、"
        + $"{Environment.NewLine}中断しても元の書庫はそのまま残ります。",
        $"Adding was stopped.{Environment.NewLine}{Environment.NewLine}"
        + "The archive was left untouched. The work was done on a working copy,"
        + $"{Environment.NewLine}so stopping leaves the original archive as it was.");

    public static string AddedFilesLine(int count) => Pick(
        $"追加したファイル: {count:N0} 個",
        $"Files added: {count:N0}");

    public static string ReplacedFilesLine(int count) => Pick(
        $"置き換えたファイル: {count:N0} 個",
        $"Files replaced: {count:N0}");

    public static string KeptFilesLine(int count) => Pick(
        $"置き換えず残したファイル: {count:N0} 個",
        $"Files kept instead of replaced: {count:N0}");

    public static string FailedFilesLine(int count) => Pick(
        $"追加できなかったファイル: {count:N0} 個",
        $"Files that could not be added: {count:N0}");

    public static string AddDone(int count) => Pick(
        $"{count:N0} 個のファイルを追加しました",
        $"Added {count:N0} {Plural(count, "file", "files")}");

    // ------------------------------------------------------------------ 既定のアプリで開く (#12, #16)

    public static string OpenedReadOnly(string name, string format) => Pick(
        $"{name} を開きました ({format} は読み取りのみのため、書き換えても書庫には戻りません)",
        $"Opened {name} ({format} is read-only, so any changes will not go back into the archive)");

    public static string OpenedWatching(string name) => Pick(
        $"{name} を開きました。保存すると書庫へ反映するか尋ねます",
        $"Opened {name}. When you save it, you will be asked whether to put it back.");

    public static string ConfirmApplyEdit(string entryPath) => Pick(
        $"{entryPath}{Environment.NewLine}{Environment.NewLine}"
        + $"編集されました。書庫に反映しますか?{Environment.NewLine}{Environment.NewLine}"
        + "「いいえ」を選んでも編集内容は残ります。アプリを終了するときに改めて尋ねます。",
        $"{entryPath}{Environment.NewLine}{Environment.NewLine}"
        + $"This file was edited. Put it back into the archive?{Environment.NewLine}{Environment.NewLine}"
        + "Choosing No keeps your edits; you will be asked again when you quit.");

    public static string ApplyingEdit(string name) => Pick(
        $"{name} を書庫に反映しています…", $"Putting {name} back into the archive…");

    public static string ApplyEditFailed(string entryPath, string reason) => Pick(
        $"書庫に反映できませんでした。{Environment.NewLine}{Environment.NewLine}"
        + $"{entryPath}{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The file could not be put back into the archive.{Environment.NewLine}{Environment.NewLine}"
        + $"{entryPath}{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ApplyEditCancelled => Pick("反映を中断しました", "Putting it back was stopped");

    public static string ApplyEditDone(string name) => Pick(
        $"{name} を書庫に反映しました", $"Put {name} back into the archive");

    public static string ConfirmPendingEdits(string names) => Pick(
        $"書庫に反映していない編集があります。{Environment.NewLine}{Environment.NewLine}{names}"
        + $"{Environment.NewLine}{Environment.NewLine}反映してから終了しますか?{Environment.NewLine}"
        + "「いいえ」を選ぶと編集内容は失われます。",
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
        + $"このファイルは開くと実行されます。{Environment.NewLine}"
        + $"出所の分からない書庫の場合は開かないでください。{Environment.NewLine}{Environment.NewLine}"
        + "続けますか?",
        $"{fileName}{Environment.NewLine}{Environment.NewLine}"
        + $"Opening this file will run it.{Environment.NewLine}"
        + $"Do not open it if you are unsure where the archive came from."
        + $"{Environment.NewLine}{Environment.NewLine}Continue?");

    public static string TempUnavailable => Pick(
        "一時フォルダを使えないため、ファイルを開けません",
        "Files cannot be opened because the temporary folder is unavailable");

    public static string TempUnavailableDetail(string root, string reason) => Pick(
        $"ファイルを開くための一時フォルダを用意できませんでした。{Environment.NewLine}"
        + $"「展開」で場所を指定すれば取り出せます。{Environment.NewLine}{Environment.NewLine}"
        + $"{root}{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The temporary folder used to open files could not be prepared.{Environment.NewLine}"
        + $"You can still get the files out with Extract.{Environment.NewLine}{Environment.NewLine}"
        + $"{root}{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ExtractingOne(string name) => Pick(
        $"{name} を取り出しています…", $"Extracting {name}…");

    public static string ExtractOneCancelled => Pick(
        "取り出しを中断しました", "Extracting was stopped");

    public static string ExtractOneFailedReason => Pick(
        "書庫から取り出せませんでした。", "It could not be extracted from the archive.");

    public static string OpenEntryFailed(string name, string reason) => Pick(
        $"{name} を開けませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"{name} could not be opened.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string OpenedWithDefaultApp(string fileName) => Pick(
        $"{fileName} を既定のアプリで開きました",
        $"Opened {fileName} with its default app");

    public static string OpenLaunchCancelled => Pick(
        "開くのを取り消しました", "Opening was cancelled");

    public static string DefaultAppFailed(string reason) => Pick(
        $"既定のアプリで開けませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"It could not be opened with the default app.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ChooseAppPrompt(string fileName) => Pick(
        $"{fileName} を開くアプリを選んでください",
        $"Choose an app to open {fileName}");

    public static string NoAppFound(string reason) => Pick(
        $"このファイルを開けるアプリが見つかりませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"No app was found that can open this file.{Environment.NewLine}{Environment.NewLine}{reason}");

    // ------------------------------------------------------------------ ドラッグでの取り出し (#17)

    public static string NothingToExtract => Pick(
        "取り出せるファイルがありません", "There are no files to extract");

    public static string DragCancelled => Pick(
        "取り出しを取りやめました", "Extracting was called off");

    public static string ExtractingCount(int count) => Pick(
        $"{count:N0} 件を取り出しています…", $"Extracting {count:N0}…");

    public static string DragExtractFailed(string reason) => Pick(
        $"取り出しに失敗しました。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Extracting failed.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string DragCannotStart => Pick(
        "取り出せませんでした。ドラッグを開始できません",
        "Nothing could be extracted, so the drag cannot start");

    public static string DragExtracted(int count) => Pick(
        $"{count:N0} 件を取り出しました",
        $"Extracted {count:N0} {Plural(count, "item", "items")}");

    public static string DragFailed => Pick(
        "ドラッグを完了できませんでした", "The drag could not be completed");

    public static string DragTooLarge(
        int fileLimit, long megabyteLimit, int count, long megabytes) => Pick(
        $"ドラッグで取り出せるのは {fileLimit:N0} 件 / {megabyteLimit:N0}MB までです。"
        + $"{Environment.NewLine}選択されているのは {count:N0} 件 / {megabytes:N0}MB です。"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "ドラッグでは取り出しが終わるまで操作を受け付けられないため、"
        + $"{Environment.NewLine}この量では「展開」を使ってください。中断もできます。",
        $"Dragging can extract up to {fileLimit:N0} items / {megabyteLimit:N0}MB."
        + $"{Environment.NewLine}You have selected {count:N0} items / {megabytes:N0}MB."
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "A drag cannot be interrupted until it finishes,"
        + $"{Environment.NewLine}so use Extract for this much. That can be stopped.");

    // ------------------------------------------------------------------ 書庫内の移動 (#43)

    public static string MoveConflict(string names) => Pick(
        $"移動先に同じ名前の項目があります。{Environment.NewLine}{Environment.NewLine}{names}"
        + $"{Environment.NewLine}{Environment.NewLine}名前を変えてから移動してください。",
        $"The destination already contains items with the same name."
        + $"{Environment.NewLine}{Environment.NewLine}{names}"
        + $"{Environment.NewLine}{Environment.NewLine}Rename them before moving.");

    public static string Moving(int done) => Pick(
        $"移動しています… ({done:N0} 件)", $"Moving… ({done:N0})");

    public static string MoveFailed(string reason) => Pick(
        $"移動できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}元の書庫は変更していません。",
        $"The items could not be moved.{Environment.NewLine}{Environment.NewLine}{reason}"
        + $"{Environment.NewLine}{Environment.NewLine}The original archive was left untouched.");

    public static string MoveCancelled => Pick("移動を中断しました", "Moving was stopped");

    public static string MoveDone(int count) => Pick(
        $"{count:N0} 件を移動しました",
        $"Moved {count:N0} {Plural(count, "item", "items")}");

    // ------------------------------------------------------------------ 展開

    public static string ExtractSelectedTitle => Pick(
        "選択した項目の展開先を選択", "Select where to extract the selected items");

    public static string ExtractFolderTitle(string name) => Pick(
        $"「{name}」の展開先を選択", $"Select where to extract \"{name}\"");

    public static string ExtractAllTitle => Pick(
        "書庫全体の展開先を選択", "Select where to extract the whole archive");

    public static string ConfirmOverwrite(int count, string preview, string more) => Pick(
        $"展開先に同じ名前のファイルが {count:N0} 件あります。上書きしますか?"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "「いいえ」を選ぶと、それらは展開せずに残します。",
        $"The destination already has {count:N0} {Plural(count, "file", "files")} "
        + $"with the same name. Overwrite {Plural(count, "it", "them")}?"
        + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
        + $"{Environment.NewLine}{Environment.NewLine}"
        + "Choosing No leaves those files alone and skips them.");

    public static string ExtractCalledOff => Pick(
        "展開を取りやめました", "Extracting was called off");

    public static string Extracting(string name) => Pick(
        $"展開中: {name}", $"Extracting: {name}");

    public static string ExtractFailed(string reason) => Pick(
        $"展開に失敗しました。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"Extracting failed.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string ExtractCancelledStatus(int extracted) => Pick(
        $"展開を中断しました({extracted:N0} 個展開済み)",
        $"Extracting was stopped ({extracted:N0} extracted)");

    public static string ExtractDone(int extracted) => Pick(
        $"{extracted:N0} 個のファイルを展開しました",
        $"Extracted {extracted:N0} {Plural(extracted, "file", "files")}");

    public static string ExtractCancelledLine1 => Pick(
        "展開を中断しました。", "Extracting was stopped.");

    public static string ExtractCancelledLine2 => Pick(
        "中断までに展開したファイルはそのまま残してあります。",
        "The files extracted before it stopped were left in place.");

    public static string ExtractCancelledLine3 => Pick(
        "書きかけだったファイルは削除しました。",
        "The file being written at that moment was deleted.");

    public static string ExtractDestinationLine(string destination) => Pick(
        $"展開先: {destination}", $"Destination: {destination}");

    public static string ExtractedFilesLine(int count) => Pick(
        $"展開したファイル: {count:N0} 個", $"Files extracted: {count:N0}");

    public static string SkippedFilesLine(int count) => Pick(
        $"上書きせず残したファイル: {count:N0} 個",
        $"Files skipped instead of overwritten: {count:N0}");

    public static string RejectedFilesLine(int count) => Pick(
        $"安全でないパスのため展開しなかったファイル: {count:N0} 個",
        $"Files not extracted because of an unsafe path: {count:N0}");

    public static string RejectedFilesDetail => Pick(
        "展開先の外に書き出そうとするエントリが含まれていました。",
        "The archive contained entries that would write outside the destination.");

    public static string NotWrittenFilesLine(int count) => Pick(
        $"書き出せなかったファイル: {count:N0} 個",
        $"Files that could not be written: {count:N0}");

    // ------------------------------------------------------------------ 書庫検査 (#53)

    public static string Inspect => Pick("検査", "Inspect");

    public static string InspectTooltip => Pick(
        "この書庫が壊れていないか、危ない中身が入っていないかを調べる",
        "Check this archive for damage and for content that could cause trouble");

    /// <summary>検査中のステータスバー。区切りごとに何をしているかを出す。</summary>
    public static string Inspecting(InspectionPhase phase, string name) => phase switch
    {
        InspectionPhase.Structure => Pick(
            "検査中: 索引とヘッダを照合しています",
            "Inspecting: comparing the index against the entry headers"),

        InspectionPhase.Safety => Pick(
            "検査中: 名前と大きさを調べています",
            "Inspecting: checking names and sizes"),

        _ => name.Length == 0
            ? Pick("検査中: 中身を読んでいます", "Inspecting: reading the contents")
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
        "行った検査: 構造(索引・ヘッダ・CRCの照合)、安全性(パス・名前・圧縮率)、マルウェア(AMSI)",
        "Checks performed: structure (index, headers, CRC), "
        + "safety (paths, names, ratios), malware (AMSI)");

    public static string InspectionChecksLineWithoutMalware => Pick(
        "行った検査: 構造(索引・ヘッダ・CRCの照合)、安全性(パス・名前・圧縮率)",
        "Checks performed: structure (index, headers, CRC), safety (paths, names, ratios)");

    public static string InspectionContentsLine(int checkedCount, int fileCount) => Pick(
        $"ファイル {fileCount:N0} 件のうち {checkedCount:N0} 件は中身まで読んで確かめました。",
        $"Read and verified the contents of {checkedCount:N0} of {fileCount:N0} "
        + $"{Plural(fileCount, "file", "files")}.");

    public static string InspectionMalwareLine(int scanned) => Pick(
        $"うち {scanned:N0} 件を対策ソフトの判定に掛けました。書庫ファイルそのものも渡しています。",
        $"Of those, {scanned:N0} {Plural(scanned, "was", "were")} handed to the antimalware "
        + "service. The archive file itself was handed over as well.");

    /// <summary>マルウェア検査が使えなかったことを、黙って省かずに出す (#56、#57)。</summary>
    public static string InspectionMalwareUnavailable => Pick(
        "マルウェア検査は行えませんでした。この環境の対策ソフトは、Windows の検査の口 (AMSI) "
        + "に応じていません。ほかの検査は行っています。",
        "The malware check could not run: the antimalware software on this machine does not "
        + "answer Windows' scan interface (AMSI). The other checks did run.");

    public static string InspectionCancelledLine => Pick(
        "検査は途中で中断されました。ここに出ているのは、中断までに調べた範囲の結果です。",
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
            "中身が壊れています。書庫に記録された照合値と一致しません",
            "The contents are damaged: they do not match the checksum recorded in the archive"),

        InspectionIssue.HeaderMismatch => Pick(
            $"書庫の索引と項目の見出しが食い違っています{Paren(detail)}",
            $"The archive index and the entry header disagree{Paren(detail)}"),

        InspectionIssue.Truncated => Pick(
            "書庫が途中で切れています。記録されている位置まで中身がありません",
            "The archive is cut short: it ends before the point its own index refers to"),

        InspectionIssue.TrailingData => Pick(
            $"書庫の終わりのあとに、書庫ではないデータが {detail} バイト続いています",
            $"{detail} bytes that are not part of the archive follow its end"),

        InspectionIssue.UnsupportedMethod => Pick(
            $"対応していない圧縮方式のため取り出せません{Paren(detail)}",
            $"Stored with a compression method Expzip cannot read{Paren(detail)}"),

        InspectionIssue.DuplicateName => Pick(
            "同じ名前の項目が2つ以上あります。展開すると片方しか残りません",
            "More than one item has this name. Extracting leaves only one of them."),

        InspectionIssue.Unreadable => Pick(
            $"中身を読み出せませんでした{Paren(detail)}",
            $"The contents could not be read{Paren(detail)}"),

        InspectionIssue.EncryptedNotChecked => Pick(
            "パスワードが分からないため、中身を調べられませんでした",
            "The contents could not be checked: the password is not known"),

        InspectionIssue.EscapingPath => Pick(
            $"展開先の外へ書き出そうとするパスです{Paren(detail)}",
            $"This path would write outside the folder you extract into{Paren(detail)}"),

        InspectionIssue.SuspiciousPath => Pick(
            $"通常の書庫にはあり得ない形のパスです{Paren(detail)}",
            $"This path is not shaped like anything a normal archive holds{Paren(detail)}"),

        InspectionIssue.ReservedName => Pick(
            $"Windows が装置の名前として扱うため、この名前では作れません{Paren(detail)}",
            $"Windows treats this as a device name, so the file cannot be created{Paren(detail)}"),

        InspectionIssue.TrailingSpaceOrDot => Pick(
            $"末尾が空白かピリオドのため、Windows ではこの名前で作れません{Paren(detail)}",
            $"Windows cannot create this name: it ends with a space or a period{Paren(detail)}"),

        InspectionIssue.ControlCharacter => Pick(
            "名前に制御文字が入っています。画面に見えている名前と実際の名前が違います",
            "The name contains control characters, so what you see is not the real name"),

        InspectionIssue.InvalidCharacter => Pick(
            $"Windows のファイル名に使えない文字が入っています{Paren(detail)}",
            $"The name contains characters Windows does not allow in a file name{Paren(detail)}"),

        InspectionIssue.CaseCollision => Pick(
            $"大文字小文字だけが違う項目があります{Paren(detail)}。展開すると片方が失われます",
            $"Another item differs only in letter case{Paren(detail)}. "
            + "Extracting loses one of them."),

        InspectionIssue.BidiOverride => Pick(
            "文字の向きを変える記号で拡張子を偽装しています。見えている拡張子と実際の拡張子が違います",
            "A text-direction override disguises the extension: "
            + "what you see is not the real extension"),

        InspectionIssue.HighRatio => Pick(
            $"展開すると {detail} 倍に膨らみます",
            $"This expands to {detail} times its stored size"),

        InspectionIssue.ZipBomb => Pick(
            $"書庫全体が展開すると {detail} 倍に膨らみます。展開先の空きに気を付けてください",
            $"The whole archive expands to {detail} times its size. "
            + "Watch the free space where you extract it."),

        InspectionIssue.ExecutableExtension => Pick(
            $"開くと、中身を見るのではなくそのまま実行される種類のファイルです{Paren(detail)}",
            $"Opening this runs it instead of showing its contents{Paren(detail)}"),

        InspectionIssue.MalwareDetected => Pick(
            "対策ソフトが問題のあるものとして検出しました",
            "The antimalware service flagged this"),

        InspectionIssue.TooLargeToScan => Pick(
            $"大きすぎて一度に渡せないため、マルウェア検査を行えませんでした{Paren(detail)}",
            $"Too large to hand over in one piece, so the malware check was skipped{Paren(detail)}"),

        _ => string.Empty,
    };

    // ------------------------------------------------------------------ ファイルの分割 (#59)

    public static string Split => Pick("分割", "Split");

    public static string SplitTooltip => Pick(
        "大きなファイルを決まった大きさに分ける。つなぎ直すプログラムも一緒に作る",
        "Split a large file into fixed-size pieces, with a program to put them back together");

    public static string SplitTitle => Pick("ファイルの分割", "Split a file");

    public static string SplitSourceLabel => Pick("分けるもの", "File");

    public static string SplitDestinationLabel => Pick("置き場", "Put pieces in");

    public static string SplitSizeLabel => Pick("1つの大きさ", "Piece size");

    public static string SplitBrowse => Pick("参照", "Browse");

    public static string SplitStart => Pick("分割", "Split");

    public static string SplitCancel => Pick("キャンセル", "Cancel");

    public static string SplitUnitKilobytes => "KB";

    public static string SplitUnitMegabytes => "MB";

    public static string SplitUnitGigabytes => "GB";

    public static string SplitSourceTitle => Pick(
        "分けるファイルを選択", "Choose the file to split");

    public static string SplitDestinationTitle => Pick(
        "断片の置き場を選択", "Choose where to put the pieces");

    public static string SplitAnyFile => Pick(
        "すべてのファイル|*.*", "All files|*.*");

    public static string SplitSourceMissing => Pick(
        "分けるファイルを選んでください。", "Choose the file you want to split.");

    public static string SplitDestinationMissing => Pick(
        "断片の置き場を選んでください。", "Choose where the pieces should go.");

    public static string SplitTooSmall(long minimum) => Pick(
        $"1つの大きさは {minimum / 1024:N0} KB 以上にしてください。",
        $"Each piece must be at least {minimum / 1024:N0} KB.");

    /// <summary>分割しても 1 つにしかならないときの断り。</summary>
    public static string SplitNotNeeded => Pick(
        "分割の必要はありません。1つの大きさが元のファイルより大きくなっています。",
        "No need to split: each piece would be larger than the file itself.");

    public static string SplitPreview(int parts) => Pick(
        $"{parts:N0} 個の断片と、つなぎ直すプログラムを1つ作ります。元のファイルは残ります。",
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
        + $"置き場: {destination}{Environment.NewLine}{Environment.NewLine}"
        + $"元に戻すときは、断片をすべて同じ場所に置いて「{joiner}」を実行してください。"
        + $"中身が元と同じであることも確かめます。{Environment.NewLine}"
        + "7-Zip など他のソフトでもつなげられる形にしてあります。",
        $"Split into {parts:N0} {Plural(parts, "piece", "pieces")}."
        + $"{Environment.NewLine}{Environment.NewLine}"
        + $"Location: {destination}{Environment.NewLine}{Environment.NewLine}"
        + $"To put it back together, keep every piece in one folder and run \"{joiner}\". "
        + $"It checks that the result matches the original.{Environment.NewLine}"
        + "The pieces are in the usual format, so other tools such as 7-Zip can join them too.");

    public static string SplitCancelled => Pick(
        "分割を中断しました。書きかけの断片は削除しました。",
        "Splitting was stopped. The half-written pieces were deleted.");

    public static string SplitFailed(string reason) => Pick(
        $"分割できませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
        $"The file could not be split.{Environment.NewLine}{Environment.NewLine}{reason}");

    public static string SplitSourceShrank => Pick(
        "分割中に元のファイルが読めなくなりました。",
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
        $"一時ファイルの置き場を作れませんでした。{Environment.NewLine}{root}",
        $"Could not create a place to put temporary files.{Environment.NewLine}{root}");

    /// <summary>「この名前は この理由で駄目だった」の1行。</summary>
    public static string FailureLine(string name, string reason) => Pick(
        $"  {name} … {reason}", $"  {name} — {reason}");

    public static string More(int count) => Pick(
        $"{Environment.NewLine}  ほか {count:N0} 件",
        $"{Environment.NewLine}  and {count:N0} more");

    public static string Stopping => Pick("中断しています…", "Stopping…");

    /// <summary>外での書き換えに追随できなかったことを知らせる (#64)。</summary>
    public static string ReloadFailed(string name, string reason) => Pick(
        $"{name} を読み直せませんでした ({reason})。表示は書き換えられる前のままです",
        $"Could not reload {name} ({reason}). The list still shows the earlier contents.");

    /// <summary>外で書き換わった書庫を読み直したことを知らせる (#64)。</summary>
    public static string ReloadedAfterExternalChange(string name) => Pick(
        $"{name} が外で書き換えられたため、最新の内容に更新しました",
        $"{name} changed outside Expzip, so the list was refreshed.");

    public static string RecoveredFromError => Pick(
        "処理を中断しました", "The operation was stopped");

    public static string UnhandledError(string type, string message) => Pick(
        $"処理中に問題が起きました。{Environment.NewLine}"
        + $"書庫は変更していません。{Environment.NewLine}{Environment.NewLine}"
        + $"{type}{Environment.NewLine}{message}",
        $"Something went wrong during the operation.{Environment.NewLine}"
        + $"The archive was left untouched.{Environment.NewLine}{Environment.NewLine}"
        + $"{type}{Environment.NewLine}{message}");

    /// <summary>書庫の形式の名前。ZIP や 7z は訳さない。</summary>
    public static string FormatName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Tar => "tar",
        _ => FormatUnknown,
    };
}
