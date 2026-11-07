using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Hakobune.Core.Import;

/// <summary>
/// FFFTP が保存したパスワードを読む。FFFTP はマスターパスワード（決めていなければ「DefaultPassword」）から鍵を作り、
/// AES-256 で暗号化して「0C」で始まる文字にしている（古い版の「0A」「0B」、印の無いものも読める）。
/// </summary>
public static class FfftpPasswords
{
    /// <summary>マスターパスワードを決めていないときに FFFTP が使うもの。</summary>
    public const string DefaultMasterPassword = "DefaultPassword";

    private const int MaxMasterBytes = 128;

    // 鍵を作るときに足す、決まった塩（FFFTP の registry.cpp と同じ）
    private static readonly byte[] FirstSalt = Encoding.ASCII.GetBytes(">g^r=@N7=//z<[`:");
    private static readonly byte[] SecondSalt = Encoding.ASCII.GetBytes("VG77dO1#EyC]$|C@");

    /// <summary>
    /// 保存されたパスワードを戻す。マスターパスワードが違う、読めない形などで戻せなければ null。
    /// <paramref name="masterPassword"/> が null か空なら、決めていないときのもの。
    /// </summary>
    public static string? Decode(string? stored, string? masterPassword)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        var key = MasterKey(masterPassword);
        byte[]? bytes;
        if (stored[0] is >= '\x40' and <= '\x7F')
            bytes = DecodeOriginal(stored);
        else if (stored.StartsWith("0A", StringComparison.Ordinal))
            bytes = DecodeOriginal(stored[2..]);
        else if (stored.StartsWith("0B", StringComparison.Ordinal))
            bytes = Xor(DecodeOriginal(stored[2..]), key);
        else if (stored.StartsWith("0C", StringComparison.Ordinal))
            bytes = DecodeAes(stored[2..], key);
        else
            bytes = null;
        if (bytes is null)
            return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // マスターパスワードが違うと、でたらめなバイト列になる
            return null;
        }
    }

    /// <summary>
    /// マスターパスワードが合っているか（設定の CredentialCheck と比べる）。確かめる値が無ければ、どれでも合っていることにする。
    /// </summary>
    public static bool CheckMasterPassword(FfftpSettings settings, string? masterPassword)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var check = settings.GetString("FFFTP", "CredentialCheck");
        if (string.IsNullOrEmpty(check))
            return true;
        var key = MasterKey(masterPassword);
        var span = new List<byte>(key) { 0 };
        if (settings.GetInt("FFFTP", "CredentialSalt") is { } salt)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, salt);
            span.AddRange(bytes);
        }
        return string.Equals(HashText(SHA1.HashData(span.ToArray())), check, StringComparison.Ordinal);
    }

    // マスターパスワードの UTF-8（128 バイトまで）。空なら決めていないときのもの
    private static byte[] MasterKey(string? masterPassword)
    {
        var bytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(masterPassword) ? DefaultMasterPassword : masterPassword);
        return bytes.Length <= MaxMasterBytes ? bytes : bytes[..MaxMasterBytes];
    }

    // 「0C」: 先頭 32 文字が初期化ベクトル（16 進）、「:」のあと暗号文（16 進）。AES-256-CBC、詰め物なし。平文は NUL で終わる
    private static byte[]? DecodeAes(string body, byte[] key)
    {
        if (body.Length < 34 || body[32] != ':')
            return null;
        byte[] iv, cipher;
        try
        {
            iv = Convert.FromHexString(body.AsSpan(0, 32));
            cipher = Convert.FromHexString(body.AsSpan(33));
        }
        catch (FormatException)
        {
            return null;
        }
        if (cipher.Length == 0 || cipher.Length % 16 != 0)
            return null;
        using var aes = Aes.Create();
        aes.Key = AesKey(key);
        var plain = aes.DecryptCbc(cipher, iv, PaddingMode.None);
        var end = Array.IndexOf(plain, (byte)0);
        return end < 0 ? plain : plain[..end];
    }

    // 鍵: SHA-1 を 2 つ（決まった塩つき）。それぞれの 5 つの 32 ビットの語を、リトルエンディアンで並べた先頭 32 バイト
    private static byte[] AesKey(byte[] key)
    {
        var first = SHA1.HashData(Concat(key, FirstSalt));
        var second = SHA1.HashData(Concat(key, SecondSalt));
        var result = new byte[32];
        for (var word = 0; word < 8; word++)
        {
            var source = word < 5 ? first.AsSpan(word * 4, 4) : second.AsSpan((word - 5) * 4, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(word * 4), BinaryPrimitives.ReadUInt32BigEndian(source));
        }
        return result;
    }

    private static byte[] Concat(byte[] head, byte[] tail)
    {
        var result = new byte[head.Length + tail.Length];
        head.CopyTo(result, 0);
        tail.CopyTo(result, head.Length);
        return result;
    }

    // 古い形（鍵なしの並べ替え）。2 文字で 1 バイト。1 文字目の下の位が奇数なら、詰め物の 1 文字を飛ばす
    private static byte[] DecodeOriginal(string text)
    {
        var bytes = new List<byte>(text.Length / 2);
        var i = 0;
        while (i + 1 < text.Length)
        {
            int first = text[i];
            int second = text[i + 1];
            var value = (byte)((first & 0x0F) | (second << 4));
            var rotate = (first >> 4) & 3;
            bytes.Add((byte)((value >> rotate) | (value << (8 - rotate))));
            i += (first & 1) != 0 ? 3 : 2;
        }
        return [.. bytes];
    }

    private static byte[] Xor(byte[] bytes, byte[] key)
    {
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] ^= key[i % key.Length];
        return bytes;
    }

    // SHA-1 の 20 バイトを、4 ビットずつ「@」〜「O」の 40 文字にする
    private static string HashText(byte[] hash)
    {
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            builder.Append((char)(0x40 | (value >> 4)));
            builder.Append((char)(0x40 | (value & 0x0F)));
        }
        return builder.ToString();
    }
}
