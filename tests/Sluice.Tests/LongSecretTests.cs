using System.Security.Cryptography;
using Sluice.Core.Engine;
using Sluice.Core.Interception;
using Sluice.Core.Platform;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

/// <summary>
/// Secrets longer than Proxifier's 255-block limit, such as a pasted SSH private key (#213), in every password mode.
/// The "account" mode runs with a stand-in <see cref="ISecretProtector"/> so it is tested on both OSes; that swaps the
/// platform in use, hence the non-parallel collection.
/// </summary>
[Collection(nameof(PlatformServicesTests))]
public class LongSecretTests
{
    public static TheoryData<PasswordEncryptionMode> AllModes =>
        [PasswordEncryptionMode.Disabled, PasswordEncryptionMode.Basic, PasswordEncryptionMode.CurrentUser, PasswordEncryptionMode.MasterPassword];

    /// <summary>A 4096-bit RSA key in PEM, the same size as a pasted one (about 3.2 KB).</summary>
    private static readonly Lazy<string> RsaKey = new(() =>
    {
        using var rsa = RSA.Create(4096);
        return rsa.ExportRSAPrivateKeyPem();
    });

    /// <summary>Reversible stand-in for DPAPI: enough to run the account-mode code path on any OS.</summary>
    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data, string? description = null) => [0x5A, .. data.Select(b => (byte)(b ^ 0x5A))];

        public byte[] Unprotect(byte[] data) =>
            data.Length > 0 && data[0] == 0x5A ? data[1..].Select(b => (byte)(b ^ 0x5A)).ToArray() : throw new CryptographicException("not ours");
    }

    private sealed class ProtectingPlatform : IPlatformServices
    {
        public string Name => "protecting";
        public void OnAppStart() { }
        public void Notify(string title, string message, bool isError) { }
        public string? CaptureUnavailableReason => null;
        public bool HasCaptureRights => true;
        public string CaptureRightsHint => "";
        public bool CanRestartElevated => false;
        public ITrafficCapture CreateCapture(RoutingEngine engine) => throw new NotSupportedException();
        public ISecretProtector SecretProtector { get; } = new XorProtector();
    }

    private static void WithProtector(Action test)
    {
        var previous = PlatformServices.Current;
        try
        {
            PlatformServices.Use(new ProtectingPlatform());
            test();
        }
        finally { PlatformServices.Use(previous); }
    }

    private static string? Master(PasswordEncryptionMode m) => m == PasswordEncryptionMode.MasterPassword ? "correct horse" : null;

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Codec_RoundTrips_LongValues(PasswordEncryptionMode mode) => WithProtector(() =>
    {
        var codec = new ProxifierPasswordCodec(mode, Master(mode));
        foreach (var s in new[] { new string('y', 257), new string('z', 2037), new string('z', 2038), RsaKey.Value, new string('k', 64 * 1024) })
            Assert.Equal(s, codec.Decode(codec.Encode(s)));
    });

    [Theory]
    [InlineData(PasswordEncryptionMode.Basic, 0)]
    [InlineData(PasswordEncryptionMode.MasterPassword, 2)]
    public void Aes_Values_KeepProxifiersLayout_UpTo255Blocks_ThenUseTheLongForm(PasswordEncryptionMode mode, byte type)
    {
        var codec = new ProxifierPasswordCodec(mode, Master(mode));

        // 2037 chars: 4 + 2*2038 (text and NUL) = 4080 bytes = 255 blocks, the most one byte can count → Proxifier's layout.
        var fits = Convert.FromBase64String(codec.Encode(new string('a', 2037)));
        Assert.Equal(new byte[] { 0, type, 255 }, fits[..3]);
        Assert.Equal(3 + 255 * 16, fits.Length);

        // 2038 chars → 256 blocks: block byte 0, then the count as 4 bytes little-endian.
        var longer = Convert.FromBase64String(codec.Encode(new string('a', 2038)));
        Assert.Equal(new byte[] { 0, type, 0, 0, 1, 0, 0 }, longer[..7]);
        Assert.Equal(7 + 256 * 16, longer.Length);

        // The long form's ciphertext is the same as Proxifier's would be: same key, IV and plaintext layout.
        using var aes = Aes.Create();
        aes.Key = ProxifierPasswordCodec.Key(type, Master(mode));
        var plain = aes.DecryptCbc(longer.AsSpan(7), new byte[16], PaddingMode.None);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, plain[..4]);
        Assert.Equal(new string('a', 2038) + "\0", System.Text.Encoding.Unicode.GetString(plain, 4, 2 * 2039));
    }

    [Fact]
    public void A_Value_Written_Before_The_Long_Form_Still_Decodes()
    {
        // Basic-mode "s3cret" as every earlier version (and Proxifier) wrote it, computed independently of the codec.
        const string stored = "AAACadbtItO07wPiIPJkRIw2s7P9ZGhQy8PA31JWB2MSdRM=";
        Assert.Equal("s3cret", new ProxifierPasswordCodec(PasswordEncryptionMode.Basic).Decode(stored));
    }

    [Fact]
    public void Corrupt_Long_Values_FailCleanly()
    {
        var c = new ProxifierPasswordCodec(PasswordEncryptionMode.Basic);
        var good = Convert.FromBase64String(c.Encode(new string('a', 3000)));

        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String(good[..^1])));                  // truncated
        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String(good[..5])));                   // no full count
        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String([0, 0, 0, 2, 0, 0, 0, .. new byte[32]])));   // count that fits a byte
        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String([0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F])));        // absurd count
        Assert.Throws<CryptographicFailure>(() => c.Decode(Convert.ToBase64String([0, 0, 0, 0, 0, 0, 0x80])));                 // negative count
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Profile_With_A_4096Bit_Rsa_Key_SavesAndLoads(PasswordEncryptionMode mode) => WithProtector(() =>
    {
        var p = Profile.CreateDefault();
        p.Options.PasswordEncryption = mode;
        p.MasterPassword = Master(mode);
        var tunnel = new Tunnel { Id = 100, Type = TunnelType.Ssh, Address = "ssh.example", Port = 22, Username = "alice" };
        tunnel.Settings["privatekey"] = RsaKey.Value;
        p.Tunnels.Add(tunnel);
        Assert.True(RsaKey.Value.Length > 3000);

        var json = SluiceProfileSerializer.Serialize(p);
        Assert.Equal(mode == PasswordEncryptionMode.Disabled, json.Contains("PRIVATE KEY"));

        var back = SluiceProfileSerializer.Parse(json, (_, _) => Master(mode));
        Assert.Equal(RsaKey.Value, back.FindTunnel(100)!.Settings["privatekey"]);
    });
}
