using System.Xml.Linq;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

/// <summary>
/// The "account" password mode (Proxifier's CurrentUser) uses DPAPI, which exists only on Windows. The other modes are
/// tested in the shared project on both OSes.
/// </summary>
public class WindowsPasswordEncryptionTests
{
    private static ProxifierPasswordCodec Account() => new(PasswordEncryptionMode.CurrentUser, null);

    [Fact]
    public void RoundTrip()
    {
        var codec = Account();
        foreach (var pw in new[] { "", "a", "s3cret", "pässwörd ✓", new string('x', 7), new string('y', 256) })
            Assert.Equal(pw, codec.Decode(codec.Encode(pw)));
    }

    [Fact]
    public void Account_Mode_IsDpapi_WithTypeByte1()
    {
        var stored = Convert.FromBase64String(Account().Encode("x"));
        Assert.Equal(new byte[] { 0, 1 }, stored[..2]);
        Assert.True(stored.Length > 50); // DPAPI blob
    }

    [Fact]
    public void Account_Mode_HasHash() => Assert.NotNull(Account().Hash);

    private static Profile ProfileWithAccountMode()
    {
        var p = Profile.CreateDefault();
        p.Options.PasswordEncryption = PasswordEncryptionMode.CurrentUser;
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "10.0.0.1", Port = 1080, Username = "alice", Password = "s3cret" });
        return p;
    }

    [Fact]
    public void Ppx_RoundTrip_AccountMode()
    {
        var xml = PpxSerializer.ToXml(ProfileWithAccountMode()).ToString();
        var enc = XDocument.Parse(xml).Root!.Element("Options")!.Element("Encryption")!;
        Assert.Equal("account", enc.Attribute("mode")!.Value);
        Assert.NotNull(enc.Element("Hash"));
        Assert.DoesNotContain("<Password>s3cret</Password>", xml);

        var back = PpxSerializer.Parse(xml, (_, _) => null);
        Assert.Equal(("alice", "s3cret"), (back.Tunnels[0].Username, back.Tunnels[0].Password));
        Assert.Equal(PasswordEncryptionMode.CurrentUser, back.Options.PasswordEncryption);
    }

    [Fact]
    public void SluiceProfile_RoundTrip_AccountMode()
    {
        var json = SluiceProfileSerializer.Serialize(ProfileWithAccountMode());
        Assert.DoesNotContain("s3cret", json);
        var back = SluiceProfileSerializer.Parse(json, (_, _) => null);
        Assert.Equal(PasswordEncryptionMode.CurrentUser, back.Options.PasswordEncryption);
        Assert.Equal("s3cret", back.FindTunnel(100)!.Password);
    }
}
