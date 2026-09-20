// 画面に出る文言を、本物を呼び出して一覧にする (#118)。
//
//     dotnet run --project tools/stringdump              一覧を作り直す
//     dotnet run --project tools/stringdump -- --check    一覧とずれていないか見る
//
// ソースを読んで推し量るのではなく、ビルドした Expzip.dll の中の本物を呼ぶ。
// switch 式も三項演算子も改行の差し込みも、画面に出るのと同じ形になる。
//
// 件数や名前を受け取るものには、引数の名前から見本を入れる。見本は ⟦ ⟧ で囲む。
// 数を 0 や 1 にすると形が変わるもの (「1 個」など) は、その形も並べる。
// 数字が変わっただけのものは並べない。
//
// **出来上がりの tools/stringdump/strings.txt はリポジトリに入れてある。**
// 文言を直したらこれも作り直す。差分を見れば、直すつもりの無かった文言まで
// 変わっていないかが分かる。画面を操作するテストは、その場面を開けるものしか
// 見られないが、こちらは全部を数秒で見られる。
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

var check = args.Contains("--check");
var rest = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

var repo = FindRepository();
if (repo is null)
{
    Console.Error.WriteLine("リポジトリが見つかりません (Expzip.slnx のあるフォルダーから走らせてください)");
    return 2;
}

var dll = rest.Length > 0 ? Path.GetFullPath(rest[0]) : FindAssembly(repo);
if (dll is null || !File.Exists(dll))
{
    Console.Error.WriteLine("Expzip.dll が見つかりません。先に build.cmd -Debug を走らせてください");
    return 2;
}

var output = rest.Length > 1
    ? Path.GetFullPath(rest[1])
    : Path.Combine(repo, "tools", "stringdump", "strings.txt");

const BindingFlags Flags =
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

var assembly = Assembly.LoadFrom(dll);
var table = assembly.GetType("Expzip.Localization.Strings", throwOnError: true)!;
var language = table.GetProperty("Language", Flags)!;
var japanese = Enum.Parse(language.PropertyType, "Japanese");
var english = Enum.Parse(language.PropertyType, "English");
var nullability = new NullabilityInfoContext();

// 表の中の道具で、画面に出る文言ではないもの
string[] skip = ["Pick", "ToSettingValue", "Resolve", "FromSystem"];

// 呼び出し元が別の文言を組み立てて渡すもの。本物の組み立て方に合わせる
var specific = new Dictionary<string, Func<object?>>
{
    ["ConfirmDelete.more"] = () => Nested("More", 7),
    ["ConfirmOverwrite.more"] = () => Nested("More", 7),
    ["ConfirmReplace.more"] = () => Nested("More", 7),
    ["ConfirmDelete.detail"] = () => Nested("DeleteFolderDetail", 12),
    ["FileCount.limits"] = () => Nested("LimitEncryptedAes"),
    ["RuleTreeBreakDetail.heading"] = () => Nested("RuleTreeBreakTooltip"),
    ["RuleTreeBreakDetail.items"] = () =>
        Nested("RuleTreeBreakItem", "資料/報告書.PDF", "拡張子は小文字にする")
        + Environment.NewLine
        + Nested("RuleTreeBreakItem", "資料/新しいフォルダ", "フォルダ名に空白を入れない"),
    ["InspectionMessage.detail"] = () => "画像/2024/写真.jpg",
    ["EntryRowName.suspicious"] = () => Nested("MarkSuspiciousPath"),
    ["EntryRowName.rule"] = () => Nested("MarkRuleBreak"),
    ["EntryRowName.encrypted"] = () => Nested("MarkEncrypted"),
    ["FindingRowName.severity"] = () => Nested(
        "SeverityName",
        Enum.Parse(assembly.GetType("Expzip.Inspection.InspectionSeverity", throwOnError: true)!, "Danger")),
};

// 引数の名前ごとの見本。呼び出し元で実際に渡っているものに寄せてある
var strings = new Dictionary<string, string>
{
    ["name"] = "報告書.pdf",
    ["path"] = @"C:\Users\user\Desktop\資料.zip",
    ["archiveName"] = "資料.zip",
    ["parentName"] = "外側.zip",
    ["entryPath"] = "画像/2024/写真.jpg",
    ["target"] = "画像/2024/写真.jpg",
    ["fileName"] = "資料.zip",
    ["preview"] = "  報告書.pdf" + Environment.NewLine + "  写真.jpg" + Environment.NewLine + "  メモ.txt",
    ["names"] = "  報告書.pdf" + Environment.NewLine + "  写真.jpg",
    ["format"] = "7z",
    ["from"] = "見本.zip",
    ["root"] = @"C:\Users\user\AppData\Local\Temp\Expzip",
    ["destination"] = @"C:\Users\user\Desktop\資料",
    // 分けた元の名前に .exe を足したもの (FileSplitter: target + ".exe")
    ["joiner"] = "資料.zip.exe",
    ["revision"] = "0123abc",
    ["version"] = "0.1.0",
    ["architecture"] = "x64",
    ["os"] = "Microsoft Windows 10.0.26200",
    ["runtime"] = ".NET 10.0.11",
    ["type"] = "IOException",
    ["message"] = "The process cannot access the file because it is being used by another process.",
    ["model"] = "gemini-2.5-flash",
    ["sourceName"] = @"..\..\Windows\System32\evil.dll",
    ["resource"] = "Expzip.Resources.THIRD-PARTY-NOTICES.txt",
    ["rules"] = "ルート直下に README.md を置く" + Environment.NewLine + "拡張子は小文字にする",
    ["what"] = "ルート直下に README.md を置く / 拡張子は小文字にする",
    ["heading"] = "見出し",
    ["items"] = "項目",
    ["more"] = "",
    ["detail"] = "",
    ["limits"] = "",
};

var ints = new Dictionary<string, int>
{
    ["count"] = 3, ["total"] = 120, ["done"] = 45, ["seconds"] = 12, ["parts"] = 4, ["used"] = 2,
    ["extracted"] = 10, ["broken"] = 1, ["warning"] = 2, ["danger"] = 1, ["unusable"] = 1,
    ["unchecked_"] = 2, ["status"] = 429, ["scanned"] = 250, ["rules"] = 5, ["places"] = 3,
    ["omitted"] = 38, ["missing"] = 2, ["folders"] = 6, ["files"] = 42, ["fileCount"] = 42,
    ["checkedCount"] = 3, ["bytes"] = 18432, ["affected"] = 12,
};

var longs = new Dictionary<string, long>
{
    ["totalBytes"] = 52_428_800, ["total"] = 52_428_800, ["minimum"] = 1_048_576,
    ["length"] = 1_048_576, ["compressed"] = 20_971_520,
};

// 空で渡ってくることがあるもの。空のときの形も見る
string[] optional = ["more", "detail", "limits"];

var results = new SortedDictionary<string, List<Variant>>(StringComparer.Ordinal);
var failures = new List<string>();

foreach (var property in table.GetProperties(Flags))
{
    if (property.PropertyType != typeof(string) || property.GetMethod is null
        || property.GetIndexParameters().Length > 0)
    {
        continue;
    }

    var ja = Invoke(() => property.GetValue(null), japanese);
    var en = Invoke(() => property.GetValue(null), english);
    Add(property.Name, [new Variant("", ja.Text, en.Text, ja.Error ?? en.Error)]);
}

foreach (var method in table.GetMethods(Flags))
{
    if (method.ReturnType != typeof(string) || method.IsSpecialName || method.IsGenericMethod
        || skip.Contains(method.Name) || method.Name.Contains('<'))
    {
        continue;
    }

    var parameters = method.GetParameters();
    var specs = new List<(int Index, object? Value)?> { null };

    for (var i = 0; i < parameters.Length; i++)
    {
        foreach (var alternate in Alternates(parameters[i]))
        {
            specs.Add((i, alternate));
        }
    }

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var variants = new List<Variant>();

    foreach (var spec in specs)
    {
        var jaArgs = Arguments(method, parameters, spec, japanese);
        var ja = Invoke(() => method.Invoke(null, jaArgs), japanese);
        var enArgs = Arguments(method, parameters, spec, english);
        var en = Invoke(() => method.Invoke(null, enArgs), english);

        // 数字や差し込みが変わっただけなら、同じ形として並べない
        var fresh = seen.Add(Normalize(ja.Text) + "\u0001" + Normalize(en.Text));
        if (spec is not null && !fresh)
        {
            continue;
        }

        variants.Add(new Variant(Display(parameters, jaArgs), ja.Text, en.Text, ja.Error ?? en.Error));

        if (variants.Count >= 24)
        {
            break;
        }
    }

    Add(method.Name, variants);
}

var page = new StringBuilder();
page.AppendLine("# Expzip の文言");
page.AppendLine("#");
page.AppendLine("# tools/stringdump が作る。手で直さない。");
page.AppendLine("# ⟦ ⟧ は差し込まれた見本。件数や名前は見本の値であって、決まった値ではない。");
page.AppendLine();

foreach (var (name, variants) in results)
{
    foreach (var variant in variants)
    {
        page.AppendLine(variant.Args.Length == 0 ? name : name + " (" + variant.Args + ")");

        if (variant.Error is not null)
        {
            page.AppendLine("  !! " + variant.Error);
            continue;
        }

        Write("ja", variant.Ja);
        Write("en", variant.En);
    }

    page.AppendLine();
}

var made = page.ToString().Replace("\r\n", "\n");
var total = results.Values.Sum(v => v.Count);

if (check)
{
    var had = File.Exists(output) ? File.ReadAllText(output).Replace("\r\n", "\n") : "";

    if (had == made)
    {
        Console.WriteLine($"一覧と同じです (文言 {results.Count} 個 / 形 {total} 個)");
        return 0;
    }

    Console.Error.WriteLine("文言が一覧とずれています。dotnet run --project tools/stringdump で作り直してください");
    return 1;
}

// 出来上がりはリポジトリに入れる。改行はほかのファイルと同じ CRLF にする
File.WriteAllText(output, made.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
Console.WriteLine($"書きました: {output}");
Console.WriteLine($"文言 {results.Count} 個 / 形 {total} 個 / 呼べなかったもの {failures.Count} 個");

foreach (var name in failures)
{
    Console.WriteLine("  × " + name);
}

return failures.Count == 0 ? 0 : 1;

// ------------------------------------------------------------------ 道具

void Write(string tag, string text)
{
    var lines = text.Replace("\r\n", "\n").Split('\n');
    page.AppendLine("  " + tag + "  " + lines[0]);

    foreach (var line in lines.Skip(1))
    {
        page.AppendLine("      | " + line);
    }
}

void Add(string name, List<Variant> variants)
{
    if (variants.Any(v => v.Error is not null))
    {
        failures.Add(name);
    }

    if (results.TryGetValue(name, out var existing))
    {
        existing.AddRange(variants);
        return;
    }

    results[name] = variants;
}

object? Nested(string member, params object?[] values)
{
    if (values.Length == 0 && table.GetProperty(member, Flags) is { } property)
    {
        return property.GetValue(null);
    }

    var method = table.GetMethods(Flags)
        .First(m => m.Name == member && m.GetParameters().Length == values.Length);
    return method.Invoke(null, values);
}

object?[] Arguments(MethodInfo method, ParameterInfo[] parameters, (int Index, object? Value)? spec, object lang)
{
    // 見本の中に表の文言を使うものがあるので、言語を先に切り替える
    language.SetValue(null, lang);
    var values = new object?[parameters.Length];

    for (var i = 0; i < parameters.Length; i++)
    {
        values[i] = spec is { } s && s.Index == i ? s.Value : Sample(method, parameters[i]);
    }

    return values;
}

object? Sample(MethodInfo method, ParameterInfo parameter)
{
    var name = parameter.Name ?? "";
    var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

    if (specific.TryGetValue(method.Name + "." + name, out var make))
    {
        return Mark(make() as string ?? "");
    }

    if (type == typeof(string))
    {
        // 失敗の理由は、言い直す仕組みを通したものが渡る (#106)
        if (name == "reason" && table.GetMethod("Reason", Flags, [typeof(Exception)]) is { } reason)
        {
            return Mark((string)reason.Invoke(null, [new UnauthorizedAccessException(
                @"Access to the path 'C:\Users\user\Desktop\資料.zip' is denied.")])!);
        }

        return Mark(strings.TryGetValue(name, out var text) ? text : "見本");
    }

    if (type == typeof(int))
    {
        return ints.TryGetValue(name, out var number) ? number : 3;
    }

    if (type == typeof(long))
    {
        return longs.TryGetValue(name, out var size) ? size : 1_048_576L;
    }

    if (type == typeof(char))
    {
        return '*';
    }

    if (type == typeof(TimeSpan))
    {
        return TimeSpan.FromSeconds(83);
    }

    if (type == typeof(DateTimeOffset))
    {
        return new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.FromHours(9));
    }

    if (type == typeof(bool))
    {
        return false;
    }

    if (type == typeof(Exception))
    {
        return new UnauthorizedAccessException(@"Access to the path 'C:\Users\user\Desktop\資料.zip' is denied.");
    }

    if (type.IsEnum)
    {
        return Enum.GetValues(type).GetValue(0);
    }

    return null;
}

IEnumerable<object?> Alternates(ParameterInfo parameter)
{
    var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

    if (type == typeof(int))
    {
        yield return 0;
        yield return 1;
    }
    else if (type == typeof(long))
    {
        yield return 0L;
    }
    else if (type == typeof(bool))
    {
        yield return true;
    }
    else if (type == typeof(Exception))
    {
        // 言い直す 7 つの理由を、ひととおり通す (#106)
        yield return new UnauthorizedAccessException("Access to the path is denied.");
        yield return new FileNotFoundException(@"Could not find file 'C:\Users\user\Desktop\資料.zip'.");
        yield return new IOException(
            @"The process cannot access the file 'C:\Users\user\Desktop\資料.zip' because it is being used by another process.",
            unchecked((int)0x80070020));
        yield return new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        yield return new PathTooLongException("The path is too long.");
        yield return new InvalidDataException("Found invalid data while decoding.");
        yield return new System.Net.Http.HttpRequestException("No such host is known.");
        yield return new NotSupportedException("Specified method is not supported.");
    }
    else if (type.IsEnum)
    {
        foreach (var value in Enum.GetValues(type).Cast<object>().Skip(1))
        {
            yield return value;
        }
    }
    else if (type == typeof(string))
    {
        if (optional.Contains(parameter.Name))
        {
            yield return "";
        }

        if (nullability.Create(parameter).WriteState == NullabilityState.Nullable)
        {
            yield return null;
        }
    }
}

(string Text, string? Error) Invoke(Func<object?> call, object lang)
{
    language.SetValue(null, lang);

    try
    {
        return (((call() as string) ?? "").Replace("\r\n", "\n"), null);
    }
    catch (TargetInvocationException ex)
    {
        return ("", ex.InnerException?.Message ?? ex.Message);
    }
    catch (Exception ex)
    {
        return ("", ex.Message);
    }
}

static string? FindRepository()
{
    var here = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (here is not null)
    {
        if (File.Exists(Path.Combine(here.FullName, "Expzip.slnx")))
        {
            return here.FullName;
        }

        here = here.Parent;
    }

    return null;
}

static string? FindAssembly(string repo)
{
    var bin = Path.Combine(repo, "src", "Expzip", "bin");

    return Directory.Exists(bin)
        ? Directory.EnumerateFiles(bin, "Expzip.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
        : null;
}

static string Mark(string text) => text.Length == 0 ? text : "⟦" + text + "⟧";

static string Normalize(string text)
    => Regex.Replace(Regex.Replace(text, "⟦[^⟦⟧]*⟧", "⟦⟧"), @"\d[\d,.]*", "#");

static string Display(ParameterInfo[] parameters, object?[] values)
{
    var shown = new List<string>();

    for (var i = 0; i < parameters.Length; i++)
    {
        var text = values[i] switch
        {
            null => "(なし)",
            "" => "(空)",
            string s => s.Replace("⟦", "").Replace("⟧", "").Replace("\r\n", " ⏎ "),
            DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            TimeSpan t => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString() ?? "",
        };

        shown.Add((parameters[i].Name ?? i.ToString(CultureInfo.InvariantCulture)) + ": " + text);
    }

    return string.Join(", ", shown);
}

internal sealed record Variant(string Args, string Ja, string En, string? Error);
