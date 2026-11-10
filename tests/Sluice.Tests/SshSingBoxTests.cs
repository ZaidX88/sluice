using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Sluice.Core.Profiles;
using Sluice.Core.Tunnels;

namespace Sluice.Tests;

public class SshSingBoxTests
{
    private static byte[] Blob(string type, int bodyLength = 32)
    {
        var name = Encoding.ASCII.GetBytes(type);
        var b = new byte[4 + name.Length + 4 + bodyLength];
        BinaryPrimitives.WriteInt32BigEndian(b, name.Length);
        name.CopyTo(b, 4);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4 + name.Length), bodyLength);
        for (var i = 0; i < bodyLength; i++) b[8 + name.Length + i] = (byte)i;
        return b;
    }

    [Fact]
    public void HostKey_FromBlob_UsesTheKeyTypeInsideTheBlob_AndOpenSshFingerprint()
    {
        var blob = Blob("ssh-rsa");
        var k = SshHostKey.FromBlob("rsa-sha2-512", blob);
        Assert.Equal("ssh-rsa " + Convert.ToBase64String(blob), k.Line);         // authorized_keys form
        Assert.Equal("rsa-sha2-512", k.Algorithm);                                // what to negotiate
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(blob)).TrimEnd('=');
        Assert.Equal($"rsa-sha2-512 SHA256:{expected}", k.Fingerprint);           // same text SSH.NET pins
        Assert.True(SshHostKey.TryParseLine(k.Stored, out var back));
        Assert.Equal(k, back);
        Assert.False(SshHostKey.TryParseLine("ssh-ed25519 SHA256:abc", out _));   // a fingerprint is not a key
        Assert.False(SshHostKey.TryParseLine(null, out _));
    }

    [Fact]
    public void SshConfig_HasPinnedKey_Credentials_AndServer()
    {
        var t = new Tunnel { Type = TunnelType.Ssh, Address = "ssh.example", Port = 2222, Username = "u", Password = "p" };
        t.Settings["privatekey"] = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----";
        t.Settings["passphrase"] = "pp";
        var key = SshHostKey.FromBlob("ssh-ed25519", Blob("ssh-ed25519"));
        var c = JsonNode.Parse(SingBoxConfig.BuildSsh(t, 1234, "127.0.0.1", 40000, key.Line, key.Algorithm))!;
        var o = c["outbounds"]![0]!;
        Assert.Equal(("ssh", "127.0.0.1", 40000, "u", "p"), ((string)o["type"]!, (string)o["server"]!, (int)o["server_port"]!, (string)o["user"]!, (string)o["password"]!));
        Assert.Equal(key.Line, (string)o["host_key"]![0]!);
        Assert.Equal("ssh-ed25519", (string)o["host_key_algorithms"]![0]!);
        Assert.EndsWith("-----\n", (string)o["private_key"]!);
        Assert.Equal("pp", (string)o["private_key_passphrase"]!);
        Assert.Equal(1234, (int)c["inbounds"]![0]!["listen_port"]!);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("singbox", "singbox")]
    [InlineData("sshnet", "sshnet")]
    [InlineData("weird", "auto")]
    public void SshClientChoice(string? setting, string expected)
    {
        var t = new Tunnel { Type = TunnelType.Ssh };
        if (setting is not null) t.Settings["sshclient"] = setting;
        Assert.Equal(expected, TunnelBackends.SshClientOf(t));
    }

    [Fact]
    public void LearnedHostKeys_DoNotRestartTheSession()
    {
        var t = new Tunnel { Type = TunnelType.Ssh, Address = "a", Port = 22, Username = "u", Password = "p" };
        var before = TunnelBackends.Fingerprint(t);
        t.Settings["hostkey"] = "ssh-ed25519 SHA256:x";
        t.Settings["hostpubkey"] = "ssh-ed25519 ssh-ed25519 AAAA";
        Assert.Equal(before, TunnelBackends.Fingerprint(t));
        t.Settings["sshclient"] = "sshnet";
        Assert.NotEqual(before, TunnelBackends.Fingerprint(t));   // switching clients does
    }

    /// <summary>sing-box itself accepts the generated config (runs when sing-box.exe is next to the tests).</summary>
    [Fact]
    public void SingBox_AcceptsTheSshConfig()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
        if (!File.Exists(exe)) return;
        var t = new Tunnel { Type = TunnelType.Ssh, Address = "ssh.example", Port = 22, Username = "u", Password = "p" };
        var key = SshHostKey.FromBlob("ssh-ed25519", Blob("ssh-ed25519"));
        var path = Path.Combine(Path.GetTempPath(), $"sluice-ssh-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, SingBoxConfig.BuildSsh(t, 1080, "127.0.0.1", 22, key.Line, key.Algorithm));
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("check");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, err);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
