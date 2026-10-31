using System.IO;
using System.Text;

namespace Expzip.CommandLine;

/// <summary>
/// 一覧ファイルの読み方 (#316)。1 行に「ソース,書庫」を書く。
/// </summary>
/// <remarks>
/// Windows のファイル名にはカンマを使えるので、カンマを含むパスは <c>"</c> で囲む (CSV の書き方)。
/// <c>"</c> はファイル名に使えないので、<c>""</c> で <c>"</c> を表す書き方は要らない。
/// 1 行で完結させる。パスに改行は入らない。
/// </remarks>
internal static class CompressList
{
    /// <summary>
    /// 中身を文字列にする。UTF-8 として読み、正しくなければ Shift_JIS として読む。
    /// </summary>
    /// <remarks>
    /// メモ帳で作れば UTF-8、Excel で CSV に保存すれば Shift_JIS になることがある。
    /// CP932 は <see cref="CodePagesEncodingProvider"/> を登録してから呼ぶ。
    /// </remarks>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    /// <summary>行に分ける。空の行は飛ばし、行番号 (1 から) を添える。</summary>
    public static IEnumerable<(int Line, string Text)> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim(' ', '\t').Length > 0)
            {
                yield return (i + 1, lines[i]);
            }
        }
    }

    /// <summary>
    /// 1 行をカンマで分ける。
    /// </summary>
    /// <returns>分けたもの。<c>"</c> の使い方が正しくなければ <see langword="null"/>。</returns>
    public static List<string>? SplitFields(string line)
    {
        var fields = new List<string>();
        var i = 0;

        while (true)
        {
            i = SkipBlanks(line, i);

            if (i < line.Length && line[i] == '"')
            {
                var close = line.IndexOf('"', i + 1);
                if (close < 0)
                {
                    return null;
                }

                fields.Add(line[(i + 1)..close]);
                i = SkipBlanks(line, close + 1);
                if (i < line.Length && line[i] != ',')
                {
                    return null;
                }
            }
            else
            {
                var comma = line.IndexOf(',', i);
                var end = comma < 0 ? line.Length : comma;
                var field = line[i..end].TrimEnd(' ', '\t');
                if (field.Contains('"'))
                {
                    return null;
                }

                fields.Add(field);
                i = end;
            }

            if (i >= line.Length)
            {
                return fields;
            }

            i++;
        }
    }

    private static int SkipBlanks(string line, int start)
    {
        var i = start;
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }
}
