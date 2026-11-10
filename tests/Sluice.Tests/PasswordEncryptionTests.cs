using System.Security.Cryptography;
using System.Xml.Linq;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

public class PasswordEncryptionTests
{
    public static TheoryData<PasswordEncryptionMode> AllModes =>
        [PasswordEncryptionMode.Disabled, PasswordEncryptionMode.Basic, PasswordEncryptionMode.CurrentUser, PasswordEncryptionMode.MasterPassword];

    private static ProxifierPasswordCodec Codec(PasswordEncryptionMode m) =>
        new(m, m == PasswordEncryptionMode.MasterPassword ? "correct horse" : null);

    [Theory]
    [MemberData(nameof(AllModes))]
    public void RoundTrip(PasswordEncryptionMode mode)
    {
        var codec = Codec(mode);
        foreach (var pw in new[] { "", "a", "s3cret", "pässwörd ✓", new string('x', 7), new string('y', 256) })
            Assert.Equal(pw, codec.Decode(codec.Encode(pw)));
    }

    [Fact]
    public void Basic_Layout_MatchesProxifier()
    {
        var stored = Convert.FromBase64String(Codec(PasswordEncryptionMode.Basic).Encode("s3cret"));
        // 00, type 0, block count; "s3cret" = 4 + 7*2 = 18 bytes → 2 blocks.
        Assert.Equal(new byte[] { 0, 0, 2 }, stored[..3]);
        Assert.Equal(3 + 32, stored.Length);

        // Decrypt independently with the documented key [3..34] and zero IV.
        using var aes = Aes.Create();
        aes.Key = Enumerable.Range(3, 32).Select(i => (byte)i).ToArray();
        var plain = aes.DecryptCbc(stored.AsSpan(3), new byte[16], PaddingMode.None);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, plain[..4]);
        Assert.Equal("s3cret\0", System.Text.Encoding.Unicode.GetString(plain, 4, 14));
    }

    [Fact]
    public void ShortStrings_UseMinimumTwoBlocks_LongOnesGrow()
    {
        Assert.Equal(2, Convert.FromBase64String(Codec(PasswordEncryptionMode.Basic).Encode(""))[2]);
        // 13 chars → 4 + 28 = 32 bytes → exactly 2 blocks; 14 chars → 34 bytes → 3 blocks.
        Assert.Equal(2, Convert.FromBase64String(Codec(PasswordEncryptionMode.Basic).Encode(new string('a', 13)))[2]);
        Assert.Equal(3, Convert.FromBase64String(Codec(PasswordEncryptionMode.Basic).Encode(new string('a', 14)))[2]);
    }

    [Fact]
    public void Key_Derivation_MatchesDecompiledCDataCrypt()
    {
        Assert.Equal(Enumerable.Range(3, 32).Select(i => (byte)i), ProxifierPasswordCodec.Key(0, null));
        Assert.Equal(Enumerable.Range(3, 32).Select(i => (byte)i), ProxifierPasswordCodec.Key(2, null));   // no password → basic key

        // "AB" → UTF-16LE 41 00 42 00 XORed over 0,1,2,3,...
        var k = ProxifierPasswordCodec.Key(2, "AB");
        Assert.Equal(new byte[] { 0 ^ 0x41, 1, 2 ^ 0x42, 3, 4, 5 }, k[..6]);

        // A 20-char password (40 bytes) wraps around the 32-byte key.
        var pw = new string('Z', 20);
        var wrapped = ProxifierPasswordCodec.Key(2, pw);
        Assert.Equal((byte)(0 ^ 'Z' ^ 'Z'), wrapped[0]);   // bytes 0 and 32 both 'Z'
        Assert.Equal((byte)(1 ^ 0 ^ 0), wrapped[1]);
    }

    [Fact]
    public void Account_Mode_IsDpapi_WithTypeByte1()
    {
        var stored = Convert.FromBase64String(Codec(PasswordEncryptionMode.CurrentUser).Encode("x"));
        Assert.Equal(new byte[] { 0, 1 }, stored[..2]);
        Assert.True(stored.Length > 50); // DPAPI blob
    }

    [Fact]
    public void Master_WrongPassword_IsDetected()
    {
        var stored = new ProxifierPasswordCodec(PasswordEncryptionMode.MasterPassword, "right").Encode("proxy-pw");
        var wrong = new ProxifierPasswordCodec(PasswordEncryptionMode.MasterPassword, "wrong");
        var ex = Assert.Throws<CryptographicFailure>(() => wrong.Decode(stored));
        Assert.Contains("Wrong password", ex.Message);
    }

    [Fact]
    public void Hash_IsEncryptedA_AndVerifiesMasterPassword()
    {
        var right = new ProxifierPasswordCodec(PasswordEncryptionMode.MasterPassword, "right");
        Assert.Equal("A", right.Decode(right.Hash!));
        Assert.True(right.VerifyHash(right.Hash!));
        Assert.False(new ProxifierPasswordCodec(PasswordEncryptionMode.MasterPassword, "wrong").VerifyHash(right.Hash!));
        Assert.Null(Codec(PasswordEncryptionMode.Basic).Hash);
        Assert.Null(Codec(PasswordEncryptionMode.Disabled).Hash);
        Assert.NotNull(Codec(PasswordEncryptionMode.CurrentUser).Hash);
    }

    [Fact]
    public void Decode_FollowsTypeByte_NotProfileMode()
    {
        // A basic-encrypted value inside a master-mode profile still decodes (Proxifier reads the type byte).
        var basic = Codec(PasswordEncryptionMode.Basic).Encode("mixed");
        Assert.Equal("mixed", Codec(PasswordEncryptionMode.MasterPassword).Decode(basic));
    }

    [Fact]
    public void Base64_ToleratesNonZeroTrailingBits()
    {
        // Proxifier's encoder can leave garbage in the unused bits of the last digit: "QR==" vs canonical "QQ==".
        Assert.Equal(new byte[] { 0x41 }, ProxifierPasswordCodec.Base64Lenient("QR=="));
        Assert.Equal(new byte[] { 0x41 }, ProxifierPasswordCodec.Base64Lenient("QQ=="));
        Assert.Throws<CryptographicFailure>(() => ProxifierPasswordCodec.Base64Lenient("Q!=="));
    }

    [Fact]
    public void Corrupt_Values_FailCleanly()
    {
        var c = Codec(PasswordEncryptionMode.Basic);
        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String([1, 0, 2])));         // bad leading byte
        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String([0, 9, 2])));         // unknown type
        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String([0, 0, 2, 1, 2])));   // wrong length
        Assert.Throws<CryptographicFailure>(() => c.Encode(new string('x', 257)));                     // too long
    }

    // ---------------- profile files ----------------

    private static Profile ProfileWith(PasswordEncryptionMode mode, string? master = null)
    {
        var p = Profile.CreateDefault();
        p.Options.PasswordEncryption = mode;
        p.MasterPassword = master;
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "10.0.0.1", Port = 1080, Username = "alice", Password = "s3cret" });
        return p;
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Ppx_RoundTrip_AllModes(PasswordEncryptionMode mode)
    {
        var master = mode == PasswordEncryptionMode.MasterPassword ? "mp" : null;
        var xml = PpxSerializer.ToXml(ProfileWith(mode, master)).ToString();

        var enc = XDocument.Parse(xml).Root!.Element("Options")!.Element("Encryption")!;
        Assert.Equal(mode switch
        {
            PasswordEncryptionMode.Disabled => "disabled",
            PasswordEncryptionMode.CurrentUser => "account",
            PasswordEncryptionMode.MasterPassword => "master",
            _ => "basic",
        }, enc.Attribute("mode")!.Value);
        Assert.Equal(mode is PasswordEncryptionMode.CurrentUser or PasswordEncryptionMode.MasterPassword, enc.Element("Hash") is not null);
        Assert.Equal(mode == PasswordEncryptionMode.Disabled, xml.Contains("<Password>s3cret</Password>"));

        var back = PpxSerializer.Parse(xml, (_, _) => master);
        Assert.Equal("s3cret", back.Tunnels[0].Password);
        Assert.Equal("alice", back.Tunnels[0].Username);
        Assert.Equal(mode, back.Options.PasswordEncryption);
    }

    [Fact]
    public void Ppx_Master_RetriesThenFails_AndCancelPropagates()
    {
        var xml = PpxSerializer.ToXml(ProfileWith(PasswordEncryptionMode.MasterPassword, "mp")).ToString();

        var asked = 0;
        var ok = PpxSerializer.Parse(xml, (_, _) => ++asked < 3 ? "nope" : "mp");
        Assert.Equal(3, asked);
        Assert.Equal("s3cret", ok.Tunnels[0].Password);
        Assert.Equal("mp", ok.MasterPassword);

        Assert.Throws<CryptographicFailure>(() => PpxSerializer.Parse(xml, (_, _) => "nope"));
        Assert.Throws<OperationCanceledException>(() => PpxSerializer.Parse(xml, (_, _) => null));
        Assert.Throws<CryptographicFailure>(() => PpxSerializer.Parse(xml));       // no provider at all
    }

    [Fact]
    public void Ppx_SavingMasterModeWithoutPassword_Throws() =>
        Assert.Throws<ArgumentException>(() => PpxSerializer.ToXml(ProfileWith(PasswordEncryptionMode.MasterPassword)));
}
