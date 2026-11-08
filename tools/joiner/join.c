/*
 * 分割された断片を結合して元のファイルに戻す、小さな Win32 プログラム (#60)。
 *
 * Expzip はこの実行ファイルの後ろに「元の名前・断片の数・大きさ・CRC」を書き足して
 * 配る。起動すると自分自身の末尾からそれを読み、隣にある .001 .002 ... を順に
 * 結合して元のファイルを作り、CRC で確かめる。
 *
 * C ランタイムを使わない。使うと 100KB を超えてしまい、分割のたびに書き出す
 * ものとしては大きすぎるため。かわりに Win32 API だけで書く。
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* 末尾に書き足す印。これがあれば分割情報が続いている */
#define FOOTER_MAGIC "EXPZSPLT"
#define FOOTER_SIZE 32

/* まとめて読み書きする大きさ */
#define CHUNK (1024 * 1024)

static unsigned char g_chunk[CHUNK];
static unsigned long g_crcTable[256];

/* CRC を使うので memset/memcpy をコンパイラに任せられない。自前で置く */
#pragma function(memset)
void *memset(void *dst, int value, size_t count)
{
    unsigned char *p = (unsigned char *)dst;
    while (count--)
    {
        *p++ = (unsigned char)value;
    }
    return dst;
}

static void BuildCrcTable(void)
{
    unsigned long i, j, c;
    for (i = 0; i < 256; i++)
    {
        c = i;
        for (j = 0; j < 8; j++)
        {
            c = (c & 1) ? (0xEDB88320UL ^ (c >> 1)) : (c >> 1);
        }
        g_crcTable[i] = c;
    }
}

static unsigned long UpdateCrc(unsigned long crc, const unsigned char *buffer, DWORD length)
{
    DWORD i;
    for (i = 0; i < length; i++)
    {
        crc = g_crcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
    }
    return crc;
}

static int Length(const WCHAR *s)
{
    int n = 0;
    while (s[n])
    {
        n++;
    }
    return n;
}

static void Append(WCHAR *dst, const WCHAR *src)
{
    int n = Length(dst);
    int i = 0;
    while (src[i])
    {
        dst[n + i] = src[i];
        i++;
    }
    dst[n + i] = 0;
}

/* 断片の番号。7-Zip と同じく最低3桁 */
static void AppendNumber(WCHAR *dst, int value)
{
    WCHAR digits[16];
    int n = 0;
    int i;
    int at = Length(dst);

    while (value > 0)
    {
        digits[n++] = (WCHAR)(L'0' + (value % 10));
        value /= 10;
    }
    while (n < 3)
    {
        digits[n++] = L'0';
    }
    for (i = 0; i < n; i++)
    {
        dst[at + i] = digits[n - 1 - i];
    }
    dst[at + n] = 0;
}

/* 黙って動かすように言われているか (自動での確認や、まとめ処理のため) */
static int g_quiet = 0;

static void Say(const WCHAR *text, const WCHAR *caption, UINT icon)
{
    if (!g_quiet)
    {
        MessageBoxW(NULL, text, caption, MB_OK | icon);
    }
}

/* 引数に -q があるかだけを見る。C ランタイムが無いので自分で探す */
static int WantsQuiet(void)
{
    const WCHAR *cmd = GetCommandLineW();
    int i;
    for (i = 0; cmd[i]; i++)
    {
        if (cmd[i] == L'-' && cmd[i + 1] == L'q'
            && (cmd[i + 2] == 0 || cmd[i + 2] == L' '))
        {
            return 1;
        }
    }
    return 0;
}

static unsigned long ReadU32(const unsigned char *p)
{
    return (unsigned long)p[0] | ((unsigned long)p[1] << 8)
        | ((unsigned long)p[2] << 16) | ((unsigned long)p[3] << 24);
}

static unsigned __int64 ReadU64(const unsigned char *p)
{
    return (unsigned __int64)ReadU32(p) | ((unsigned __int64)ReadU32(p + 4) << 32);
}

#include "..\sfx\zone.h"

/* フォルダーと名前を足せる大きさ。~12345678.tmp、番号、:Zone.Identifier の分も取る */
#define PATH_SIZE ZONE_PATH_SIZE

/*
 * 同じフォルダーに一時ファイルを作る。既にあるファイルには書かない (CREATE_NEW)。
 * GUID の関数は読み込む DLL が増えるので使わない (#185)。
 */
static HANDLE OpenTemporary(const WCHAR *folder, WCHAR *temporary)
{
    static const WCHAR HEX[] = L"0123456789abcdef";
    LARGE_INTEGER now;
    unsigned long seed;
    int attempt;

    QueryPerformanceCounter(&now);
    seed = now.LowPart ^ (GetCurrentProcessId() << 16);

    for (attempt = 0; attempt < 64; attempt++)
    {
        HANDLE h;
        WCHAR digits[9];
        int i;
        unsigned long value = seed + (unsigned long)attempt;

        for (i = 0; i < 8; i++)
        {
            digits[i] = HEX[(value >> (28 - i * 4)) & 15];
        }

        digits[8] = 0;
        temporary[0] = 0;
        Append(temporary, folder);
        Append(temporary, L"~");
        Append(temporary, digits);
        Append(temporary, L".tmp");

        h = CreateFileW(temporary, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);

        if (h != INVALID_HANDLE_VALUE || GetLastError() != ERROR_FILE_EXISTS)
        {
            return h;
        }
    }

    return INVALID_HANDLE_VALUE;
}

int WINAPI WinMainCRTStartup(void)
{
    WCHAR self[MAX_PATH];
    WCHAR folder[MAX_PATH];
    WCHAR target[PATH_SIZE];
    WCHAR part[PATH_SIZE];
    WCHAR temporary[PATH_SIZE];
    WCHAR name[MAX_PATH];
    unsigned char footer[FOOTER_SIZE];
    HANDLE h, out;
    LARGE_INTEGER seek;
    DWORD got, wrote;
    unsigned long count, nameLength, wantCrc, crc;
    unsigned __int64 wantSize, total;
    int i, cut;

    g_quiet = WantsQuiet();
    BuildCrcTable();

    if (GetModuleFileNameW(NULL, self, MAX_PATH) == 0)
    {
        Say(L"結合用のプログラムの場所を取得できませんでした。", L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    h = CreateFileW(self, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        Say(L"結合用のプログラムを読み込めませんでした。", L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    seek.QuadPart = -(LONGLONG)FOOTER_SIZE;
    SetFilePointerEx(h, seek, NULL, FILE_END);
    if (!ReadFile(h, footer, FOOTER_SIZE, &got, NULL) || got != FOOTER_SIZE)
    {
        CloseHandle(h);
        Say(L"分割の情報を読み込めませんでした。", L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    for (i = 0; i < 8; i++)
    {
        if (footer[i] != (unsigned char)FOOTER_MAGIC[i])
        {
            CloseHandle(h);
            Say(L"分割の情報が見つかりません。", L"ファイルの結合", MB_ICONERROR);
            ExitProcess(1);
        }
    }

    count = ReadU32(footer + 12);
    wantSize = ReadU64(footer + 16);
    wantCrc = ReadU32(footer + 24);
    nameLength = ReadU32(footer + 28);

    if (nameLength == 0 || nameLength >= MAX_PATH || count == 0)
    {
        CloseHandle(h);
        Say(L"分割の情報が壊れています。", L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    seek.QuadPart = -(LONGLONG)(FOOTER_SIZE + (nameLength * 2));
    SetFilePointerEx(h, seek, NULL, FILE_END);
    if (!ReadFile(h, name, nameLength * 2, &got, NULL) || got != nameLength * 2)
    {
        CloseHandle(h);
        Say(L"元のファイル名を読み込めませんでした。", L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }
    name[nameLength] = 0;
    CloseHandle(h);

    /* 自分と同じ場所を探す */
    for (i = 0; self[i]; i++)
    {
        folder[i] = self[i];
    }
    folder[i] = 0;
    cut = 0;
    for (i = 0; folder[i]; i++)
    {
        if (folder[i] == L'\\')
        {
            cut = i + 1;
        }
    }
    folder[cut] = 0;

    target[0] = 0;
    Append(target, folder);
    Append(target, name);

    /* 長い結合のあとで断らないように、先に確かめる。置き換えるときにも確かめる */
    if (GetFileAttributesW(target) != INVALID_FILE_ATTRIBUTES)
    {
        Say(L"ファイルを結合できませんでした。既に同じ名前のファイルが存在する可能性があります。",
            L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    /* 分割ファイルの全部と、自分に付いている MotW を見る。書き始める前に一時ファイルへ付ける */
    ZoneTake(self);

    for (i = 1; (unsigned long)i <= count; i++)
    {
        part[0] = 0;
        Append(part, target);
        Append(part, L".");
        AppendNumber(part, i);
        ZoneTake(part);
    }

    /* 書き終えて確かめるまで、本来の名前は使わない。止められても、書きかけが本来の名前で残らない (#321) */
    out = OpenTemporary(folder, temporary);
    if (out == INVALID_HANDLE_VALUE)
    {
        Say(L"ファイルを結合できませんでした。既に同じ名前のファイルが存在する可能性があります。",
            L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    ZoneApply(temporary);

    crc = 0xFFFFFFFFUL;
    total = 0;

    for (i = 1; (unsigned long)i <= count; i++)
    {
        part[0] = 0;
        Append(part, target);
        Append(part, L".");
        AppendNumber(part, i);

        h = CreateFileW(part, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE)
        {
            CloseHandle(out);
            DeleteFileW(temporary);
            Say(L"分割ファイルが足りません。すべての分割ファイルを同じフォルダーに置いてから、もう一度実行してください。",
                L"ファイルの結合", MB_ICONERROR);
            ExitProcess(1);
        }

        for (;;)
        {
            if (!ReadFile(h, g_chunk, CHUNK, &got, NULL) || got == 0)
            {
                break;
            }
            if (!WriteFile(out, g_chunk, got, &wrote, NULL) || wrote != got)
            {
                CloseHandle(h);
                CloseHandle(out);
                DeleteFileW(temporary);
                Say(L"書き込みに失敗しました。ディスクの空き容量を確認してください。", L"ファイルの結合", MB_ICONERROR);
                ExitProcess(1);
            }
            crc = UpdateCrc(crc, g_chunk, got);
            total += got;
        }

        CloseHandle(h);
    }

    CloseHandle(out);
    crc ^= 0xFFFFFFFFUL;

    if (total != wantSize || crc != wantCrc)
    {
        DeleteFileW(temporary);
        Say(L"結合したファイルが元のファイルと一致しませんでした。分割ファイルが壊れているか、揃っていない可能性があります。",
            L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    /* 上書きの指定はしない。結合の間に同じ名前ができていたら、元のファイルを残す */
    if (!MoveFileExW(temporary, target, 0))
    {
        DeleteFileW(temporary);
        Say(L"ファイルを結合できませんでした。既に同じ名前のファイルが存在する可能性があります。",
            L"ファイルの結合", MB_ICONERROR);
        ExitProcess(1);
    }

    Say(L"結合が完了し、元のファイルと一致することを確認しました。", L"ファイルの結合", MB_ICONINFORMATION);
    ExitProcess(0);
}
