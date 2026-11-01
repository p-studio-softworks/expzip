/*
 * 出所の印 (Zone.Identifier) を読み書きする。自己解凍の取り出し部と結合用のプログラムで共通 (#325)。
 *
 * 読み方は Expzip 本体の MarkOfTheWeb と同じ。確かな書き方だけを「読める印」とし、それ以外は
 * インターネットの印に置き換える。Windows の中で印を読むものは 1 つとは限らないので、広く読まない。
 *
 * 使う前に Append を定義しておく。C ランタイムは使わない。読み込む DLL も増やさない (#185)。
 */

/* path の領域に、~12345678.tmp と :Zone.Identifier を足せる大きさ */
#define ZONE_PATH_SIZE (MAX_PATH * 2 + 48)
#define ZONE_MAX 4096
#define ZONE_INTERNET 3

static const WCHAR ZONE_STREAM[] = L":Zone.Identifier";
static const char ZONE_SAMPLE[] = "[ZoneTransfer]\r\nZoneId=3\r\n";
static char g_zone[ZONE_MAX];
static DWORD g_zoneLength;
static int g_zoneRank = -1;
static int g_zoneReadable;

/* 印のストリームを開くパスを作る。上限の無い形は名前を整えないので、先に整える */
static int ZonePath(const WCHAR *path, WCHAR *stream)
{
    WCHAR full[ZONE_PATH_SIZE];
    DWORD fullLength = GetFullPathNameW(path, ZONE_PATH_SIZE - 32, full, NULL);

    /* 領域に入らないとき、戻り値は必要な大きさで、full には何も書かれない */
    if (fullLength == 0 || fullLength >= ZONE_PATH_SIZE - 32)
    {
        return 0;
    }

    stream[0] = 0;

    if (full[0] == L'\\' && full[1] == L'\\' && full[2] == L'?' && full[3] == L'\\')
    {
        /* 既に上限の無い形 */
        Append(stream, full);
    }
    else if (full[0] == L'\\' && full[1] == L'\\')
    {
        Append(stream, L"\\\\?\\UNC\\");
        Append(stream, full + 2);
    }
    else
    {
        Append(stream, L"\\\\?\\");
        Append(stream, full);
    }

    Append(stream, ZONE_STREAM);
    return 1;
}

static char ZoneLower(char c)
{
    return (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
}

/* 行が word と同じか。ignoreCase なら大文字小文字を区別しない */
static int ZoneSame(const char *line, DWORD length, const char *word, int ignoreCase)
{
    DWORD i;

    for (i = 0; i < length; i++)
    {
        if (word[i] == 0 || (ignoreCase ? ZoneLower(line[i]) != ZoneLower(word[i]) : line[i] != word[i]))
        {
            return 0;
        }
    }

    return word[length] == 0;
}

/* 印字できる ASCII だけか */
static int ZonePrintable(const char *text, DWORD length)
{
    DWORD i;

    for (i = 0; i < length; i++)
    {
        unsigned char c = (unsigned char)text[i];

        if (c < 0x20 || c > 0x7E)
        {
            return 0;
        }
    }

    return 1;
}

/*
 * 印の区分の番号を読む。読めなければ -1。Expzip 本体の MarkOfTheWeb.ParseZone と同じ規則:
 * - NUL は最後の 1 バイトだけ (エクスプローラーが付ける)。CR は LF の前だけ
 * - どの行も、空行、[節の名前]、名前=値 のどれか。節の行と名前は印字できる ASCII だけ。節の行は空白を含まない。名前は空白で始まらず、空でもない
 * - 値は制御文字を含まない。ASCII 以外のバイトを書けるのは、ZoneId の行より後の値だけ
 * - [ZoneTransfer] の節が大文字小文字まで同じで 1 つだけあり、その中に ZoneId=数字 1 桁 (0 から 4) が 1 行だけある
 * - 大文字小文字や空白が違う ZoneId の行は読めない
 */
static int ZoneParse(const char *zone, DWORD length)
{
    DWORD at = 0;
    int inSection = 0;
    int sections = 0;
    int found = -1;

    if (length > 0 && zone[length - 1] == 0)
    {
        length--;
    }

    while (at < length)
    {
        DWORD start = at;
        DWORD end;
        DWORD eq;
        DWORD last;
        DWORD i;

        while (at < length && zone[at] != '\n')
        {
            if (zone[at] == 0 || (zone[at] == '\r' && !(at + 1 < length && zone[at + 1] == '\n')))
            {
                return -1;
            }

            at++;
        }

        end = at;
        if (end > start && zone[end - 1] == '\r')
        {
            end--;
        }

        at++;

        if (end == start)
        {
            continue;
        }

        if (zone[start] == '[')
        {
            /* 内側の空白は Windows が除いて読むので、[ ZoneTransfer ] も同じ節になる */
            for (i = start; i < end && zone[i] != ']'; i++)
            {
                if (zone[i] == ' ')
                {
                    return -1;
                }
            }

            if (i != end - 1 || !ZonePrintable(zone + start, end - start))
            {
                return -1;
            }

            inSection = 0;

            if (ZoneSame(zone + start, end - start, "[ZoneTransfer]", 1))
            {
                if (!ZoneSame(zone + start, end - start, "[ZoneTransfer]", 0) || ++sections > 1)
                {
                    return -1;
                }

                inSection = 1;
            }

            continue;
        }

        for (eq = start; eq < end && zone[eq] != '='; eq++)
        {
        }

        if (eq == end || eq == start || zone[start] == ' ' || !ZonePrintable(zone + start, eq - start))
        {
            return -1;
        }

        for (i = eq + 1; i < end; i++)
        {
            unsigned char c = (unsigned char)zone[i];

            if (c < 0x20 || c == 0x7F || (c > 0x7F && found < 0))
            {
                return -1;
            }
        }

        if (!inSection)
        {
            continue;
        }

        /* 名前の後ろの空白を除くと ZoneId に見える行 */
        last = eq;
        while (last > start && zone[last - 1] == ' ')
        {
            last--;
        }

        if (!ZoneSame(zone + start, last - start, "ZoneId", 1))
        {
            continue;
        }

        if (found >= 0 || last != eq || !ZoneSame(zone + start, last - start, "ZoneId", 0)
            || end != eq + 2 || zone[eq + 1] < '0' || zone[eq + 1] > '4')
        {
            return -1;
        }

        found = zone[eq + 1] - '0';
    }

    return sections == 1 ? found : -1;
}

/* 控えた印より強ければ控える。同じ強さなら読める印を優先する */
static void ZoneKeep(const char *zone, DWORD length, int rank, int readable)
{
    DWORD i;

    if (rank < g_zoneRank || (rank == g_zoneRank && (g_zoneReadable || !readable)))
    {
        return;
    }

    for (i = 0; i < length; i++)
    {
        /* C ランタイムの memcpy に置き換えられないようにする */
        ((volatile char *)g_zone)[i] = zone[i];
    }

    g_zoneLength = length;
    g_zoneRank = rank;
    g_zoneReadable = readable;
}

/*
 * ファイルの印を読んで控える (#321、#325)。USB メモリーを通ると、一部の分割ファイルだけ印が消えることがある。
 * 1 個でも付いていれば、付いているものとして扱う。印が無い、その場所が印に対応していないとき以外の失敗は、
 * インターネットから来たものとして扱う。別のプログラムが開いたままにして、印を読ませないようにできるため。
 */
static void ZoneTake(const WCHAR *path)
{
    static char read[ZONE_MAX + 1];
    WCHAR stream[ZONE_PATH_SIZE];
    HANDLE h;
    DWORD got = 0;
    DWORD total = 0;
    int zone = -1;

    if (!ZonePath(path, stream))
    {
        ZoneKeep(ZONE_SAMPLE, sizeof(ZONE_SAMPLE) - 1, ZONE_INTERNET, 0);
        return;
    }

    h = CreateFileW(stream, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, 0, NULL);

    if (h == INVALID_HANDLE_VALUE)
    {
        DWORD error = GetLastError();

        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND || error == ERROR_INVALID_NAME
            || error == ERROR_INVALID_PARAMETER || error == ERROR_NOT_SUPPORTED)
        {
            return;
        }
    }
    else
    {
        while (total < sizeof(read) && ReadFile(h, read + total, (DWORD)sizeof(read) - total, &got, NULL) && got > 0)
        {
            total += got;
        }

        CloseHandle(h);

        if (total <= ZONE_MAX)
        {
            zone = ZoneParse(read, total);
        }
    }

    if (zone >= 0)
    {
        ZoneKeep(read, total, zone, 1);
    }
    else
    {
        ZoneKeep(ZONE_SAMPLE, sizeof(ZONE_SAMPLE) - 1, ZONE_INTERNET, 0);
    }
}

/* 印を付ける。付けられないドライブでも、処理は続ける */
static void ZoneApply(const WCHAR *path)
{
    WCHAR stream[ZONE_PATH_SIZE];
    HANDLE h;
    DWORD wrote;

    if (g_zoneRank < 0 || !ZonePath(path, stream))
    {
        return;
    }

    h = CreateFileW(stream, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);

    if (h != INVALID_HANDLE_VALUE)
    {
        WriteFile(h, g_zone, g_zoneLength, &wrote, NULL);
        CloseHandle(h);
    }
}
