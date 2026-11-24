using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sluice.Core.Profiles;

/// <summary>
/// Proxifier 4 profile password encryption (class CDataCrypt), recovered from Proxifier.exe 4.14:
///
///   stored  = Base64( 0x00, type, payload )
///   type 0 "basic"   : AES-256-CBC, IV = 0, key[i] = i + 3                     → payload = nBlocks, ciphertext
///   type 1 "account" : DPAPI CryptProtectData(UTF-16LE + NUL, description "P")  → payload = DPAPI blob
///   type 2 "master"  : AES-256-CBC, IV = 0, key[i] = i XOR pw[UTF-16LE bytes, cycled over 32]
///   AES plaintext    = FF FF FF FF ‖ UTF-16LE text ‖ 00 00 ‖ padding, nBlocks = ⌈(2n+6)/16⌉, at least 2
///
/// Proxifier stores nBlocks in one byte, so its AES values hold at most 255 blocks (about 2000 characters). Longer
/// values (SSH private keys, #213) use a Sluice extension: nBlocks byte 0, which no valid value has, then nBlocks as a
/// 4-byte little-endian number, then the ciphertext. Values that fit in 255 blocks keep Proxifier's layout, so files
/// stay readable by earlier versions unless they hold such a long secret.
///
/// A profile also stores a hash = Encode("A") in account/master modes ("passwordHash" in a .sluice file), used to
/// check the master password before any tunnel password is decrypted.
/// </summary>
public sealed class ProxifierPasswordCodec : IPasswordCodec
{
    private const string HashPlaintext = "A";
    /// <summary>Upper bound for a long value's block count (16 MB of ciphertext), so a corrupt length can't ask for
    /// an absurd allocation.</summary>
    private const int MaxLongBlocks = 1 << 20;

    private readonly PasswordEncryptionMode _mode;
    private readonly string? _master;

    public ProxifierPasswordCodec(PasswordEncryptionMode mode, string? masterPassword = null)
    {
        if (mode == PasswordEncryptionMode.MasterPassword && string.IsNullOrEmpty(masterPassword))
            throw new ArgumentException("Master password mode requires a master password", nameof(masterPassword));
        _mode = mode;
        _master = masterPassword;
    }

    /// <summary>The &lt;Hash&gt; value for this mode, or null when the mode stores none.</summary>
    public string? Hash => _mode is PasswordEncryptionMode.CurrentUser or PasswordEncryptionMode.MasterPassword
        ? Encode(HashPlaintext)
        : null;

    /// <summary>True when <paramref name="hash"/> decrypts with this codec's key (i.e. the master password is right).</summary>
    public bool VerifyHash(string hash)
    {
        try { return Decode(hash) == HashPlaintext; }
        catch (CryptographicFailure) { return false; }
    }

    public string Encode(string plain)
    {
        if (_mode == PasswordEncryptionMode.Disabled) return plain;

        var text = new byte[(plain.Length + 1) * 2];
        Encoding.Unicode.GetBytes(plain, 0, plain.Length, text, 0);

        byte[] data;
        switch (_mode)
        {
            case PasswordEncryptionMode.CurrentUser:
                var blob = Secrets.Protect(text, "P");
                data = new byte[2 + blob.Length];
                data[1] = 1;
                blob.CopyTo(data, 2);
                break;

            default:
            {
                var type = _mode == PasswordEncryptionMode.MasterPassword ? (byte)2 : (byte)0;
                var blocks = Math.Max(2, (text.Length + 4 + 15) / 16);
                if (blocks > MaxLongBlocks) throw new CryptographicFailure("String is too large.");
                var plainBlock = new byte[blocks * 16];
                plainBlock[0] = plainBlock[1] = plainBlock[2] = plainBlock[3] = 0xFF;
                text.CopyTo(plainBlock, 4);

                using var aes = Aes.Create();
                aes.Key = Key(type, _master);
                var cipher = aes.EncryptCbc(plainBlock, new byte[16], PaddingMode.None);
                if (blocks <= 0xFF)
                {
                    data = new byte[3 + cipher.Length];
                    data[2] = (byte)blocks;
                    cipher.CopyTo(data, 3);
                }
                else
                {
                    data = new byte[7 + cipher.Length];   // data[2] = 0: long form
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(3), blocks);
                    cipher.CopyTo(data, 7);
                }
                data[1] = type;
                break;
            }
        }
        return Convert.ToBase64String(data);
    }

    /// <summary>Decrypt a stored value. The format byte inside the value decides the method.</summary>
    public string Decode(string stored)
    {
        if (_mode == PasswordEncryptionMode.Disabled) return stored;

        var data = Base64Lenient(stored);
        if (data.Length < 2 || data[0] != 0) throw new CryptographicFailure("Unknown format.");

        switch (data[1])
        {
            case 1:
                return FromUtf16Z(Secrets.Unprotect(data[2..]));

            case 0:
            case 2:
            {
                if (data[1] == 2 && string.IsNullOrEmpty(_master))
                    throw new CryptographicFailure("A master password is required.");
                if (data.Length < 3) throw new CryptographicFailure("Incorrect format.");
                int blocks, start;
                if (data[2] != 0)
                {
                    blocks = data[2];
                    start = 3;
                }
                else
                {
                    if (data.Length < 7) throw new CryptographicFailure("Incorrect format.");
                    blocks = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(3));
                    start = 7;
                    if (blocks <= 0xFF || blocks > MaxLongBlocks) throw new CryptographicFailure("Incorrect number of blocks.");
                }
                if (data.Length != start + blocks * 16) throw new CryptographicFailure("Incorrect number of blocks.");

                using var aes = Aes.Create();
                aes.Key = Key(data[1], _master);
                var plain = aes.DecryptCbc(data.AsSpan(start), new byte[16], PaddingMode.None);
                if (plain[0] != 0xFF || plain[1] != 0xFF || plain[2] != 0xFF || plain[3] != 0xFF)
                    throw new CryptographicFailure("Wrong password.");
                return FromUtf16Z(plain.AsSpan(4));
            }

            default:
                throw new CryptographicFailure("Unsupported format.");
        }
    }

    /// <summary>CDataCrypt key setup.</summary>
    internal static byte[] Key(byte type, string? master)
    {
        var key = new byte[32];
        if (type != 0 && !string.IsNullOrEmpty(master))
        {
            for (var i = 0; i < 32; i++) key[i] = (byte)i;
            var pw = Encoding.Unicode.GetBytes(master);
            for (var j = 0; j < pw.Length; j++) key[j % 32] ^= pw[j];
        }
        else
        {
            for (var i = 0; i < 32; i++) key[i] = (byte)(i + 3);
        }
        return key;
    }

    private static string FromUtf16Z(ReadOnlySpan<byte> bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(bytes[..(bytes.Length & ~1)]);
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    /// <summary>
    /// Proxifier's encoder reads past the end of the buffer for the final group, so the unused low
    /// bits of the last Base64 digit may be non-zero; decode without rejecting them.
    /// </summary>
    internal static byte[] Base64Lenient(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var output = new List<byte>(s.Length * 3 / 4);
        int acc = 0, bits = 0;
        foreach (var c in s)
        {
            if (c == '=') break;
            var v = Alphabet.IndexOf(c);
            if (v < 0)
            {
                if (char.IsWhiteSpace(c)) continue;
                throw new CryptographicFailure("Incorrect format.");
            }
            acc = (acc << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(acc >> bits));
                acc &= (1 << bits) - 1;
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// The account protection for "account" mode: the platform's <see cref="ISecretProtector"/> when it has one
    /// (docs/architecture/platforms.md §6.3), otherwise the DPAPI code still in Core until #55 moves it.
    /// </summary>
    private static class Secrets
    {
        public static byte[] Protect(byte[] data, string? description) =>
            Platform.PlatformServices.Current.SecretProtector is { } p ? p.Protect(data, description) : Dpapi.Protect(data, description);

        public static byte[] Unprotect(byte[] data) =>
            Platform.PlatformServices.Current.SecretProtector is { } p ? p.Unprotect(data) : Dpapi.Unprotect(data);
    }
}
