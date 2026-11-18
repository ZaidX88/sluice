using System.Text.Json;
using System.Xml.Linq;
using Sluice.Core.Profiles;

namespace Sluice.Tests;

public class SluiceProfileTests
{
    private static Profile Sample(PasswordEncryptionMode mode = PasswordEncryptionMode.Basic, string? master = null)
    {
        var p = Profile.CreateDefault();
        p.Options.PasswordEncryption = mode;
        p.MasterPassword = master;
        p.Options.Resolve.ViaProxy = true;
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

        Assert.True(back.Options.Resolve.ViaProxy);
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
    public void WrongMasterPassword_AndForeignFiles_AreRejected()
    {
        var json = SluiceProfileSerializer.Serialize(Sample(PasswordEncryptionMode.MasterPassword, "mp"));
        Assert.Throws<CryptographicFailure>(() => SluiceProfileSerializer.Parse(json, (_, _) => "nope"));
        Assert.Throws<FormatException>(() => SluiceProfileSerializer.Parse("{\"format\":\"something-else\"}"));
        Assert.Throws<FormatException>(() => SluiceProfileSerializer.Parse("not json"));
        Assert.Throws<FormatException>(() => SluiceProfileSerializer.Parse("{\"format\":\"sluice-profile\",\"version\":99}"));
    }

    [Fact]
    public void ProfileFile_DetectsFormatByContent_AndSaveIsAtomic()
    {
        var dir = Directory.CreateTempSubdirectory("sluice-profile-").FullName;
        try
        {
            var sluice = Path.Combine(dir, "a.sluice");
            ProfileFile.Save(Sample(), sluice);
            Assert.False(File.Exists(sluice + ".tmp"));
            Assert.Equal(2, ProfileFile.Load(sluice).Tunnels.Count);

            // A Proxifier file is recognised even with a misleading name.
            var ppxContent = PpxSerializer.ToXml(Sample()).ToString();
            var disguised = Path.Combine(dir, "really-ppx.sluice");
            File.WriteAllText(disguised, ppxContent);
            Assert.Single(ProfileFile.Load(disguised).Tunnels);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PpxExport_DropsEncryptedTunnels_WithWarnings_AndKeepsProxifierWording()
    {
        var warnings = new List<string>();
        var xml = PpxSerializer.ToXml(Sample(), warnings);

        var proxies = xml.Root!.Element("ProxyList")!.Elements("Proxy").ToList();
        Assert.Single(proxies);
        Assert.Equal("100", proxies[0].Attribute("id")!.Value);

        var rules = xml.Root.Element("RuleList")!.Elements("Rule").ToDictionary(r => r.Element("Name")!.Value, r => r.Element("Action")!);
        Assert.Equal("Direct", rules["Browsers"].Attribute("type")!.Value);         // its VLESS tunnel was dropped
        Assert.Equal("Chain", rules["Work"].Attribute("type")!.Value);
        var chainHops = xml.Root.Element("ChainList")!.Element("Chain")!.Elements("Proxy").Select(h => h.Value);
        Assert.Equal(["100"], chainHops);

        Assert.Contains(warnings, w => w.Contains("Edge") && w.Contains("VLESS"));
        Assert.Contains(warnings, w => w.Contains("Browsers"));
        Assert.Contains(warnings, w => w.Contains("Both"));

        // A rule using a plain tunnel is written with Proxifier's own word, "Proxy".
        var simple = Profile.CreateDefault();
        simple.Tunnels.Add(new Tunnel { Id = 1, Address = "a", Port = 1 });
        simple.Rules.Single(r => r.IsDefault).Action = RuleAction.ViaTunnel(1);
        var dflt = PpxSerializer.ToXml(simple).Root!.Element("RuleList")!.Elements("Rule").Single(r => r.Element("Name")!.Value == "Default");
        Assert.Equal("Proxy", dflt.Element("Action")!.Attribute("type")!.Value);
        Assert.Equal("1", dflt.Element("Action")!.Value);
    }

    [Fact]
    public void ImportedPpx_ThenSavedAsSluice_KeepsEverything()
    {
        var original = Sample();
        original.Tunnels.RemoveAll(t => !t.Type.IsSimple());
        original.Chains.Clear();
        original.Rules.RemoveAll(r => r.Name is "Browsers" or "Work");
        var fromPpx = PpxSerializer.Parse(PpxSerializer.ToXml(original).ToString());
        var back = SluiceProfileSerializer.Parse(SluiceProfileSerializer.Serialize(fromPpx));
        Assert.Equal("s3cret", back.FindTunnel(100)!.Password);
        Assert.Equal(["Localhost", "Default"], back.Rules.Select(r => r.Name));
    }
}
