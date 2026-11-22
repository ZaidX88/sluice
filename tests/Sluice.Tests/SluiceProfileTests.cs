using System.Text.Json;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

public class SluiceProfileTests
{
    private static Profile Sample(PasswordEncryptionMode mode = PasswordEncryptionMode.Basic, string? master = null)
    {
        var p = Profile.CreateDefault();
        p.Options.PasswordEncryption = mode;
        p.MasterPassword = master;
        p.Options.Resolve.ViaProxy = false;                    // not the default, so the round trip shows it is written
        p.Options.Udp = UdpMode.BlockQuic;
        p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "10.0.0.1", Port = 1080, Username = "alice", Password = "s3cret", Label = "Office" });
        var vless = new Tunnel { Id = 101, Type = TunnelType.VLess, Address = "edge.example", Port = 443, Label = "Edge" };
        vless.Settings["uuid"] = "6c0a5c1e-9a3b-4b6f-8c2d-123456789abc";
        vless.Settings["security"] = "reality";
        vless.Settings["sni"] = "www.microsoft.com";
        p.Tunnels.Add(vless);
        var chain = new TunnelChain { Id = 200, Name = "Both", Type = ChainType.Redundancy };
        chain.Hops.AddRange([new ChainHop { TunnelId = 101 }, new ChainHop { TunnelId = 100, Enabled = false }]);
        p.Chains.Add(chain);
        p.Rules.Insert(0, new Rule { Name = "Browsers", Applications = "chrome.exe", Action = RuleAction.ViaTunnel(101) });
        p.Rules.Insert(1, new Rule { Name = "Work", Targets = "*.corp", Ports = "443", Action = RuleAction.ViaChain(200) });
        return p;
    }

    [Theory]
    [InlineData(PasswordEncryptionMode.Disabled)]
    [InlineData(PasswordEncryptionMode.Basic)]
    [InlineData(PasswordEncryptionMode.MasterPassword)]
    public void RoundTrips_EverythingIncludingEncryptedTunnelSettings(PasswordEncryptionMode mode)
    {
        var master = mode == PasswordEncryptionMode.MasterPassword ? "mp" : null;
        var json = SluiceProfileSerializer.Serialize(Sample(mode, master));
        var back = SluiceProfileSerializer.Parse(json, (_, _) => master);

        Assert.False(back.Options.Resolve.ViaProxy);
        Assert.Equal(UdpMode.BlockQuic, back.Options.Udp);
        Assert.Equal(mode, back.Options.PasswordEncryption);

        var socks = back.FindTunnel(100)!;
        Assert.Equal((TunnelType.Socks5, "10.0.0.1", 1080, "alice", "s3cret", "Office"),
            (socks.Type, socks.Address, socks.Port, socks.Username, socks.Password, socks.Label));

        var vless = back.FindTunnel(101)!;
        Assert.Equal(TunnelType.VLess, vless.Type);
        Assert.Equal("6c0a5c1e-9a3b-4b6f-8c2d-123456789abc", vless.Settings["UUID"]);   // keys are case-insensitive
        Assert.Equal("reality", vless.Settings["security"]);

        var chain = back.FindChain(200)!;
        Assert.Equal(ChainType.Redundancy, chain.Type);
        Assert.Equal([(101, true), (100, false)], chain.Hops.Select(h => (h.TunnelId, h.Enabled)));

        Assert.Equal(["Browsers", "Work", "Localhost", "Default"], back.Rules.Select(r => r.Name));
        Assert.Equal((RuleActionType.Tunnel, 101), (back.Rules[0].Action.Type, back.Rules[0].Action.TargetId));
        Assert.Equal((RuleActionType.Chain, 200), (back.Rules[1].Action.Type, back.Rules[1].Action.TargetId));
    }

    [Fact]
    public void Secrets_AreNotStoredInPlainText_UnlessDisabled()
    {
        var json = SluiceProfileSerializer.Serialize(Sample(PasswordEncryptionMode.Basic));
        Assert.DoesNotContain("s3cret", json);
        Assert.DoesNotContain("6c0a5c1e", json);         // uuid is a credential
        Assert.Contains("www.microsoft.com", json);       // ordinary settings stay readable

        var plain = SluiceProfileSerializer.Serialize(Sample(PasswordEncryptionMode.Disabled));
        Assert.Contains("s3cret", plain);
    }

    [Fact]
    public void Json_IsReadable_WithNamedEnums()
    {
        using var doc = JsonDocument.Parse(SluiceProfileSerializer.Serialize(Sample()));
        var root = doc.RootElement;
        Assert.Equal("sluice-profile", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("vless", root.GetProperty("tunnels")[1].GetProperty("type").GetString());
        Assert.Equal("tunnel", root.GetProperty("rules")[0].GetProperty("action").GetProperty("type").GetString());
        Assert.False(root.GetProperty("rules")[0].TryGetProperty("isDefault", out _));
    }

    [Fact]
    public void NewProfiles_ResolveThroughTheTunnel_AndASavedChoiceIsKept()
    {
        // Issue #167: new profiles look names up through the tunnel, so the default policy fakes them.
        Assert.True(new ResolveOptions().ViaProxy);
        var fresh = Profile.CreateDefault();
        Assert.True(fresh.Options.Resolve.ViaProxy);
        Assert.True(new Sluice.Core.Dns.DnsPolicyCache(() => fresh, "pc").Current.ShouldFake("example.com"));

        // A profile saved with "On this PC" says so in the file and still loads that way.
        var local = Profile.CreateDefault();
        local.Options.Resolve.ViaProxy = false;
        var json = SluiceProfileSerializer.Serialize(local);
        using (var doc = JsonDocument.Parse(json))
            Assert.False(doc.RootElement.GetProperty("options").GetProperty("resolve").GetProperty("viaProxy").GetBoolean());
        Assert.False(SluiceProfileSerializer.Parse(json).Options.Resolve.ViaProxy);
    }

    [Fact]
    public void WrongMasterPassword_AndForeignFiles_AreRejected()
    {
        var json = SluiceProfileSerializer.Serialize(Sample(PasswordEncryptionMode.MasterPassword, "mp"));
        Assert.Throws<CryptographicFailure>(() => SluiceProfileSerializer.Parse(json, (_, _) => "nope"));
        Assert.Throws<FormatException>(() => SluiceProfileSerializer.Parse("{\"format\":\"something-else\"}"));
        Assert.Throws<FormatException>(() => SluiceProfileSerializer.Parse("not json"));
        Assert.Throws<FormatException>(() => SluiceProfileSerializer.Parse("{\"format\":\"sluice-profile\",\"version\":99}"));
    }

    [Fact]
    public void ProfileFile_LoadsSluiceOnly_AndSaveIsAtomic()
    {
        var dir = Directory.CreateTempSubdirectory("sluice-profile-").FullName;
        try
        {
            var sluice = Path.Combine(dir, "a.sluice");
            ProfileFile.Save(Sample(), sluice);
            Assert.False(File.Exists(sluice + ".tmp"));
            Assert.Equal(2, ProfileFile.Load(sluice).Tunnels.Count);

            // Anything that isn't a Sluice profile is refused with a clear message, whatever its name.
            var xml = Path.Combine(dir, "other.ppx");
            File.WriteAllText(xml, "<ProxifierProfile version=\"102\"><RuleList/></ProxifierProfile>");
            Assert.Equal(ProfileFile.NotASluiceProfile, Assert.Throws<FormatException>(() => ProfileFile.Load(xml)).Message);
            var disguised = Path.Combine(dir, "really-xml.sluice");
            File.Copy(xml, disguised);
            Assert.Equal(ProfileFile.NotASluiceProfile, Assert.Throws<FormatException>(() => ProfileFile.Load(disguised)).Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
