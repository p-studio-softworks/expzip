/*
 * ZIP の自己解凍書庫のスタブ (#29)。
 *
 * Expzip はこの実行ファイルの後ろに ZIP をそのまま繋いで配る。起動すると
 * 自分自身の末尾から ZIP の終端レコードを探し、中身を取り出す。
 *
 * 前に付いた分だけ位置がずれるが、ずれ幅は終端レコードから求まる。中央
 * ディレクトリは終端レコードの直前で終わっているので、
 * (終端レコードの位置 - 中央ディレクトリの大きさ) が本当の開始位置になる。
 * 読み取り側 (#32) と同じ計算。
 *
 * C ランタイムを使わない。使うと 100KB を超えてしまうため。伸長器も自前で
 * 持つ。Windows は deflate を配っていない (System32\archiveint.dll に実体は
 * あるが internal で、古い環境には無い)。7-Zip なども同じように内蔵している。
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define WINDOW_BITS 15
#define WINDOW_SIZE (1 << WINDOW_BITS)
#define WINDOW_MASK (WINDOW_SIZE - 1)

/* 読み書きの塊。大きすぎると常駐が増えるだけなので控えめにする */
#define IN_CHUNK (64 * 1024)

/* 終端レコードを探す範囲。書庫のコメントは最大 65535 バイト */
#define EOCD_SEARCH (65535 + 22)

static const WCHAR *TITLE = L"自己解凍書庫";

/* ------------------------------------------------------------------ 補助の関数 */

/* memset は伸長の表を初期化するのに要る。C ランタイムが無いので自前で置く */
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

static void AppendNumber(WCHAR *dst, unsigned long value)
{
    WCHAR digits[16];
    int n = 0;
    int i;
    int at = Length(dst);

    if (value == 0)
    {
        digits[n++] = L'0';
    }

    while (value > 0)
    {
        digits[n++] = (WCHAR)(L'0' + (value % 10));
        value /= 10;
    }

    for (i = 0; i < n; i++)
    {
        dst[at + i] = digits[n - 1 - i];
    }

    dst[at + n] = 0;
}

static int g_quiet = 0;

static void Say(const WCHAR *text, UINT icon)
{
    if (!g_quiet)
    {
        MessageBoxW(NULL, text, TITLE, MB_OK | icon);
    }
}

static void Fail(const WCHAR *text)
{
    Say(text, MB_ICONERROR);
    ExitProcess(1);
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

static unsigned long ReadU16(const unsigned char *p)
{
    return (unsigned long)p[0] | ((unsigned long)p[1] << 8);
}

static unsigned long ReadU32(const unsigned char *p)
{
    return (unsigned long)p[0] | ((unsigned long)p[1] << 8)
        | ((unsigned long)p[2] << 16) | ((unsigned long)p[3] << 24);
}

/* ------------------------------------------------------------------ CRC */

static unsigned long g_crcTable[256];

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

/* ------------------------------------------------------------------ 出し入れ */

/*
 * 取り出しの最中に持ち回る状態。入りは書庫の中の決まった範囲だけを読み、
 * 出は 32KB の窓に貯めてから書き出す。伸長では少し前に出した中身を
 * 引き写すため、窓はそのまま履歴として使える。
 */
typedef struct
{
    HANDLE source;
    unsigned __int64 left;          /* まだ読んでいない圧縮後のバイト数 */
    unsigned char in[IN_CHUNK];
    DWORD have;                     /* in に入っている量 */
    DWORD at;                       /* in のうち読んだところまで */
    unsigned long hold;             /* まだ使っていないビット */
    int bits;                       /* hold に入っているビット数 */

    HANDLE destination;
    unsigned char window[WINDOW_SIZE];
    DWORD wrote;                    /* 窓に貯まっている量 */
    unsigned long crc;
    unsigned __int64 produced;
    int broken;                     /* 書き出しに失敗した */
} Work;

static int Refill(Work *w)
{
    DWORD want;
    DWORD got;

    if (w->at < w->have)
    {
        return 1;
    }

    if (w->left == 0)
    {
        return 0;
    }

    want = (w->left < IN_CHUNK) ? (DWORD)w->left : IN_CHUNK;

    if (!ReadFile(w->source, w->in, want, &got, NULL) || got == 0)
    {
        return 0;
    }

    w->have = got;
    w->at = 0;
    w->left -= got;
    return 1;
}

static int NextByte(Work *w, unsigned char *value)
{
    if (!Refill(w))
    {
        return 0;
    }

    *value = w->in[w->at++];
    return 1;
}

/* 下位から bits ビット取り出す。deflate はビットを下位から詰める */
static int Bits(Work *w, int need, unsigned long *value)
{
    unsigned char byte;

    while (w->bits < need)
    {
        if (!NextByte(w, &byte))
        {
            return 0;
        }

        w->hold |= (unsigned long)byte << w->bits;
        w->bits += 8;
    }

    *value = w->hold & ((1UL << need) - 1);
    w->hold >>= need;
    w->bits -= need;
    return 1;
}

static int Flush(Work *w)
{
    DWORD wrote;

    if (w->wrote == 0)
    {
        return 1;
    }

    w->crc = UpdateCrc(w->crc, w->window, w->wrote);

    if (!WriteFile(w->destination, w->window, w->wrote, &wrote, NULL)
        || wrote != w->wrote)
    {
        w->broken = 1;
        return 0;
    }

    w->wrote = 0;
    return 1;
}

/*
 * 1バイト出す。窓が一杯になったら書き出す。
 * 窓は履歴でもあるため、書き出したあとも中身はそのまま残しておく。
 */
static int Put(Work *w, unsigned char value)
{
    w->window[(DWORD)(w->produced & WINDOW_MASK)] = value;
    w->produced++;
    w->wrote++;

    if (w->wrote == WINDOW_SIZE)
    {
        /* 窓の頭から順に並んでいるときだけ、そのまま書き出せる */
        DWORD wrote;
        w->crc = UpdateCrc(w->crc, w->window, WINDOW_SIZE);

        if (!WriteFile(w->destination, w->window, WINDOW_SIZE, &wrote, NULL)
            || wrote != WINDOW_SIZE)
        {
            w->broken = 1;
            return 0;
        }

        w->wrote = 0;
    }

    return 1;
}

/* ------------------------------------------------------------------ 伸長 */

/*
 * ハフマン符号の表。長さごとの個数と、長さ順に並べた記号を持つ。
 * 表を引くのではなく1ビットずつ辿るため、小さく書ける。
 */
typedef struct
{
    short count[16];
    short symbol[288];
} Huffman;

static int Construct(Huffman *h, const short *lengths, int count)
{
    int symbol;
    int length;
    int left;
    short offsets[16];

    for (length = 0; length < 16; length++)
    {
        h->count[length] = 0;
    }

    for (symbol = 0; symbol < count; symbol++)
    {
        h->count[lengths[symbol]]++;
    }

    if (h->count[0] == count)
    {
        return 0;   /* 全部が未使用。これも正しい形 */
    }

    /* 符号として成り立っているか (過不足が無いか) を確かめる */
    left = 1;

    for (length = 1; length < 16; length++)
    {
        left <<= 1;
        left -= h->count[length];

        if (left < 0)
        {
            return -1;
        }
    }

    offsets[1] = 0;

    for (length = 1; length < 15; length++)
    {
        offsets[length + 1] = (short)(offsets[length] + h->count[length]);
    }

    for (symbol = 0; symbol < count; symbol++)
    {
        if (lengths[symbol] != 0)
        {
            h->symbol[offsets[lengths[symbol]]++] = (short)symbol;
        }
    }

    return left;
}

static int Decode(Work *w, const Huffman *h, int *symbol)
{
    int length;
    int code = 0;
    int first = 0;
    int index = 0;
    unsigned long bit;

    for (length = 1; length < 16; length++)
    {
        if (!Bits(w, 1, &bit))
        {
            return 0;
        }

        code |= (int)bit;

        if (code - h->count[length] < first)
        {
            *symbol = h->symbol[index + (code - first)];
            return 1;
        }

        index += h->count[length];
        first = (first + h->count[length]) << 1;
        code <<= 1;
    }

    return 0;
}

static const short LENGTH_BASE[29] = {
    3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
    35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
};

static const short LENGTH_EXTRA[29] = {
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
    3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
};

static const short DIST_BASE[30] = {
    1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
    257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577
};

static const short DIST_EXTRA[30] = {
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
    7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
};

static Huffman g_literals;
static Huffman g_distances;

static int Block(Work *w, const Huffman *literals, const Huffman *distances)
{
    int symbol;
    unsigned long extra;
    int length;
    int distance;
    unsigned __int64 from;
    int i;

    for (;;)
    {
        if (!Decode(w, literals, &symbol))
        {
            return 0;
        }

        if (symbol < 256)
        {
            if (!Put(w, (unsigned char)symbol))
            {
                return 0;
            }

            continue;
        }

        if (symbol == 256)
        {
            return 1;
        }

        symbol -= 257;

        if (symbol >= 29)
        {
            return 0;
        }

        if (!Bits(w, LENGTH_EXTRA[symbol], &extra))
        {
            return 0;
        }

        length = LENGTH_BASE[symbol] + (int)extra;

        if (!Decode(w, distances, &symbol) || symbol >= 30)
        {
            return 0;
        }

        if (!Bits(w, DIST_EXTRA[symbol], &extra))
        {
            return 0;
        }

        distance = DIST_BASE[symbol] + (int)extra;

        if ((unsigned __int64)distance > w->produced)
        {
            return 0;   /* まだ出していないところを指している */
        }

        from = w->produced - distance;

        for (i = 0; i < length; i++)
        {
            if (!Put(w, w->window[(DWORD)((from + i) & WINDOW_MASK)]))
            {
                return 0;
            }
        }
    }
}

static int FixedBlock(Work *w)
{
    short lengths[288];
    int i;

    for (i = 0; i < 144; i++)
    {
        lengths[i] = 8;
    }

    for (; i < 256; i++)
    {
        lengths[i] = 9;
    }

    for (; i < 280; i++)
    {
        lengths[i] = 7;
    }

    for (; i < 288; i++)
    {
        lengths[i] = 8;
    }

    if (Construct(&g_literals, lengths, 288) < 0)
    {
        return 0;
    }

    for (i = 0; i < 30; i++)
    {
        lengths[i] = 5;
    }

    /* 距離の表は 30 個で完全ではないが、deflate の決まりでそう定めている */
    Construct(&g_distances, lengths, 30);
    return Block(w, &g_literals, &g_distances);
}

static const short ORDER[19] = {
    16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15
};

static int DynamicBlock(Work *w)
{
    short lengths[288 + 30];
    Huffman codeLengths;
    unsigned long value;
    int literals;
    int distances;
    int codes;
    int index;
    int symbol;
    int previous;
    int repeat;

    if (!Bits(w, 5, &value))
    {
        return 0;
    }

    literals = (int)value + 257;

    if (!Bits(w, 5, &value))
    {
        return 0;
    }

    distances = (int)value + 1;

    if (!Bits(w, 4, &value))
    {
        return 0;
    }

    codes = (int)value + 4;

    if (literals > 286 || distances > 30)
    {
        return 0;
    }

    for (index = 0; index < 19; index++)
    {
        lengths[index] = 0;
    }

    for (index = 0; index < codes; index++)
    {
        if (!Bits(w, 3, &value))
        {
            return 0;
        }

        lengths[ORDER[index]] = (short)value;
    }

    if (Construct(&codeLengths, lengths, 19) != 0)
    {
        return 0;
    }

    index = 0;
    previous = 0;

    while (index < literals + distances)
    {
        if (!Decode(w, &codeLengths, &symbol))
        {
            return 0;
        }

        if (symbol < 16)
        {
            lengths[index++] = (short)symbol;
            previous = symbol;
            continue;
        }

        if (symbol == 16)
        {
            if (index == 0 || !Bits(w, 2, &value))
            {
                return 0;
            }

            repeat = 3 + (int)value;
        }
        else if (symbol == 17)
        {
            if (!Bits(w, 3, &value))
            {
                return 0;
            }

            repeat = 3 + (int)value;
            previous = 0;
        }
        else
        {
            if (!Bits(w, 7, &value))
            {
                return 0;
            }

            repeat = 11 + (int)value;
            previous = 0;
        }

        if (index + repeat > literals + distances)
        {
            return 0;
        }

        while (repeat-- > 0)
        {
            lengths[index++] = (short)previous;
        }
    }

    if (lengths[256] == 0)
    {
        return 0;   /* 終わりの印が無い */
    }

    if (Construct(&g_literals, lengths, literals) < 0)
    {
        return 0;
    }

    if (Construct(&g_distances, lengths + literals, distances) < 0)
    {
        return 0;
    }

    return Block(w, &g_literals, &g_distances);
}

static int StoredBlock(Work *w)
{
    unsigned char header[4];
    unsigned long length;
    unsigned long i;
    unsigned char byte;

    /* 塊の頭までビットを捨てて、バイトの境目に揃える */
    w->hold = 0;
    w->bits = 0;

    for (i = 0; i < 4; i++)
    {
        if (!NextByte(w, &header[i]))
        {
            return 0;
        }
    }

    length = ReadU16(header);

    if (length != (~ReadU16(header + 2) & 0xFFFF))
    {
        return 0;
    }

    for (i = 0; i < length; i++)
    {
        if (!NextByte(w, &byte) || !Put(w, byte))
        {
            return 0;
        }
    }

    return 1;
}

static int Inflate(Work *w)
{
    unsigned long last;
    unsigned long type;

    do
    {
        if (!Bits(w, 1, &last) || !Bits(w, 2, &type))
        {
            return 0;
        }

        if (type == 0)
        {
            if (!StoredBlock(w))
            {
                return 0;
            }
        }
        else if (type == 1)
        {
            if (!FixedBlock(w))
            {
                return 0;
            }
        }
        else if (type == 2)
        {
            if (!DynamicBlock(w))
            {
                return 0;
            }
        }
        else
        {
            return 0;
        }
    } while (!last);

    return Flush(w);
}

/* 無圧縮のエントリ。そのまま写す */
static int Store(Work *w)
{
    unsigned char byte;

    while (w->left > 0 || w->at < w->have)
    {
        if (!NextByte(w, &byte) || !Put(w, byte))
        {
            return 0;
        }
    }

    return Flush(w);
}

/* ------------------------------------------------------------------ 取り出し */

static HANDLE g_self;
static unsigned __int64 g_start;    /* 書庫が始まる位置 */
static Work g_work;

static void Seek(HANDLE h, unsigned __int64 position)
{
    LARGE_INTEGER at;
    at.QuadPart = (LONGLONG)position;
    SetFilePointerEx(h, at, NULL, FILE_BEGIN);
}

/* 途中のフォルダを順に作る */
static void MakeFolders(WCHAR *path)
{
    int i;

    for (i = 0; path[i]; i++)
    {
        if (path[i] == L'\\')
        {
            path[i] = 0;
            CreateDirectoryW(path, NULL);
            path[i] = L'\\';
        }
    }
}

/*
 * 書庫の中の名前が、取り出し先の外を指していないか。
 * 置き場の外へ書かせない (Zip Slip 対策)。読み取り側と同じ考え方。
 */
static int SafeName(const WCHAR *name)
{
    int i;

    if (name[0] == 0 || name[0] == L'\\' || name[0] == L'/')
    {
        return 0;
    }

    for (i = 0; name[i]; i++)
    {
        if (name[i] == L':')
        {
            return 0;
        }

        if (name[i] == L'.' && name[i + 1] == L'.'
            && (name[i + 2] == 0 || name[i + 2] == L'\\' || name[i + 2] == L'/')
            && (i == 0 || name[i - 1] == L'\\' || name[i - 1] == L'/'))
        {
            return 0;
        }
    }

    return 1;
}

int WINAPI WinMainCRTStartup(void)
{
    WCHAR self[MAX_PATH];
    WCHAR destination[MAX_PATH];
    WCHAR path[MAX_PATH * 2];
    WCHAR name[MAX_PATH];
    WCHAR message[512];
    unsigned char *tail;
    unsigned char record[46];
    unsigned char local[30];
    LARGE_INTEGER size;
    unsigned __int64 eocd;
    unsigned __int64 at;
    unsigned long entries = 0;
    unsigned long cdSize = 0;
    unsigned long cdOffset = 0;
    unsigned long done = 0;
    unsigned long failed = 0;
    unsigned long index;
    DWORD got;
    DWORD searchLength;
    int i;
    int cut;

    g_quiet = WantsQuiet();
    BuildCrcTable();

    if (GetModuleFileNameW(NULL, self, MAX_PATH) == 0)
    {
        Fail(L"この自己解凍書庫の場所を取得できませんでした。");
    }

    g_self = CreateFileW(self, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);

    if (g_self == INVALID_HANDLE_VALUE)
    {
        Fail(L"この自己解凍書庫を読み込めませんでした。");
    }

    if (!GetFileSizeEx(g_self, &size))
    {
        Fail(L"この自己解凍書庫のサイズを取得できませんでした。");
    }

    /* 終端レコードを末尾から探す */
    searchLength = (size.QuadPart < EOCD_SEARCH) ? (DWORD)size.QuadPart : EOCD_SEARCH;
    tail = (unsigned char *)VirtualAlloc(NULL, searchLength, MEM_COMMIT, PAGE_READWRITE);

    if (tail == NULL)
    {
        Fail(L"メモリを確保できませんでした。");
    }

    Seek(g_self, (unsigned __int64)size.QuadPart - searchLength);

    if (!ReadFile(g_self, tail, searchLength, &got, NULL) || got != searchLength)
    {
        Fail(L"この自己解凍書庫を読み込めませんでした。");
    }

    eocd = 0;

    for (i = (int)searchLength - 22; i >= 0; i--)
    {
        if (tail[i] == 'P' && tail[i + 1] == 'K' && tail[i + 2] == 5 && tail[i + 3] == 6)
        {
            eocd = (unsigned __int64)size.QuadPart - searchLength + i;
            entries = ReadU16(tail + i + 10);
            cdSize = ReadU32(tail + i + 12);
            cdOffset = ReadU32(tail + i + 16);
            break;
        }
    }

    if (eocd == 0)
    {
        Fail(L"展開するデータが見つかりませんでした。ファイルが壊れている可能性があります。");
    }

    /*
     * ZIP64 は扱わない。値が振り切れている場合、本当の値は別の記録の中にある。
     * 黙って壊れた結果を出すより、はっきり断るほうがよい。
     */
    if (entries == 0xFFFFUL || cdSize == 0xFFFFFFFFUL || cdOffset == 0xFFFFFFFFUL)
    {
        Fail(L"この自己解凍書庫は大きすぎるため、展開できません。");
    }

    /*
     * 前に付いた分だけずれる。中央ディレクトリは終端レコードの直前で
     * 終わっているので、そこから逆算する (読み取り側 #32 と同じ計算)。
     */
    g_start = eocd - cdSize - cdOffset;

    /* 取り出し先は、自分と同じ場所に自分の名前でフォルダを作る */
    for (i = 0; i < MAX_PATH; i++)
    {
        destination[i] = self[i];

        if (self[i] == 0)
        {
            break;
        }
    }

    cut = Length(destination);

    while (cut > 0 && destination[cut - 1] != L'.')
    {
        cut--;
    }

    if (cut > 0)
    {
        destination[cut - 1] = 0;   /* .exe を落とす */
    }

    if (!CreateDirectoryW(destination, NULL)
        && GetLastError() != ERROR_ALREADY_EXISTS)
    {
        Fail(L"展開先のフォルダーを作成できませんでした。");
    }

    at = g_start + cdOffset;

    for (index = 0; index < entries; index++)
    {
        unsigned long method;
        unsigned long flags;
        unsigned long crc;
        unsigned long packed;
        unsigned long unpacked;
        unsigned long nameLength;
        unsigned long extraLength;
        unsigned long commentLength;
        unsigned long localOffset;
        unsigned char raw[MAX_PATH * 3];
        HANDLE out;

        Seek(g_self, at);

        if (!ReadFile(g_self, record, 46, &got, NULL) || got != 46
            || ReadU32(record) != 0x02014B50UL)
        {
            break;
        }

        flags = ReadU16(record + 8);
        method = ReadU16(record + 10);
        crc = ReadU32(record + 16);
        packed = ReadU32(record + 20);
        unpacked = ReadU32(record + 24);
        nameLength = ReadU16(record + 28);
        extraLength = ReadU16(record + 30);
        commentLength = ReadU16(record + 32);
        localOffset = ReadU32(record + 42);

        /* パスワード付きは扱わない。復号を持たせると大きくなりすぎる */
        if (nameLength == 0 || nameLength >= sizeof(raw) || (flags & 0x0001))
        {
            failed++;
            at += 46 + nameLength + extraLength + commentLength;
            continue;
        }

        if (!ReadFile(g_self, raw, nameLength, &got, NULL) || got != nameLength)
        {
            break;
        }

        at += 46 + nameLength + extraLength + commentLength;

        /*
         * 名前の文字コード。11 番目の印が立っていれば UTF-8、そうでなければ
         * その環境の従来のコードページ (日本語環境なら CP932)。
         */
        name[0] = 0;
        i = MultiByteToWideChar((flags & 0x0800) ? CP_UTF8 : CP_ACP, 0,
            (const char *)raw, (int)nameLength, name, MAX_PATH - 1);

        if (i <= 0)
        {
            failed++;
            continue;
        }

        name[i] = 0;

        for (i = 0; name[i]; i++)
        {
            if (name[i] == L'/')
            {
                name[i] = L'\\';
            }
        }

        if (!SafeName(name))
        {
            failed++;
            continue;
        }

        path[0] = 0;
        Append(path, destination);
        Append(path, L"\\");
        Append(path, name);

        /* 名前が \ で終わっていればフォルダ */
        if (name[Length(name) - 1] == L'\\')
        {
            path[Length(path) - 1] = 0;
            MakeFolders(path);
            CreateDirectoryW(path, NULL);
            continue;
        }

        MakeFolders(path);

        Seek(g_self, g_start + localOffset);

        if (!ReadFile(g_self, local, 30, &got, NULL) || got != 30
            || ReadU32(local) != 0x04034B50UL)
        {
            failed++;
            continue;
        }

        /* 中身は、局所ヘッダの名前と拡張領域の後ろから始まる */
        Seek(g_self, g_start + localOffset + 30 + ReadU16(local + 26) + ReadU16(local + 28));

        out = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL, NULL);

        if (out == INVALID_HANDLE_VALUE)
        {
            failed++;
            continue;
        }

        g_work.source = g_self;
        g_work.destination = out;
        g_work.left = packed;
        g_work.have = 0;
        g_work.at = 0;
        g_work.hold = 0;
        g_work.bits = 0;
        g_work.wrote = 0;
        g_work.crc = 0xFFFFFFFFUL;
        g_work.produced = 0;
        g_work.broken = 0;

        i = (method == 0) ? Store(&g_work) : (method == 8 ? Inflate(&g_work) : 0);

        if (i)
        {
            i = Flush(&g_work);
        }

        CloseHandle(out);

        if (!i || (g_work.crc ^ 0xFFFFFFFFUL) != crc
            || g_work.produced != unpacked)
        {
            DeleteFileW(path);
            failed++;
            continue;
        }

        done++;
    }

    CloseHandle(g_self);

    message[0] = 0;
    Append(message, L"展開しました。");
    Append(message, L"\n\n");
    AppendNumber(message, done);
    Append(message, L" 個のファイル\n");
    Append(message, destination);

    if (failed > 0)
    {
        Append(message, L"\n\n展開できなかったファイル: ");
        AppendNumber(message, failed);
        Append(message, L" 個");
        Say(message, MB_ICONWARNING);
        ExitProcess(1);
    }

    Say(message, MB_ICONINFORMATION);
    ExitProcess(0);
    return 0;
}
