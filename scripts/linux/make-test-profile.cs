// Writes the profile used by capture-livetest.sh: curl -> SOCKS5 127.0.0.1:1080, wget -> Block, hostnames through tunnels.
// Usage (Windows or Linux, .NET 10 SDK): dotnet run scripts/linux/make-test-profile.cs -- linux-test.sluice
#:project ../../src/Sluice.Core/Sluice.Core.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using Sluice.Core.Profiles;

var p = Profile.CreateDefault();
p.Tunnels.Add(new Tunnel { Id = 100, Type = TunnelType.Socks5, Address = "127.0.0.1", Port = 1080, Label = "test socks" });
p.Rules.Insert(0, new Rule { Name = "curl via socks", Applications = "curl", Action = RuleAction.ViaTunnel(100) });
p.Rules.Insert(1, new Rule { Name = "wget blocked", Applications = "wget", Action = RuleAction.Block });
p.Options.Resolve.ViaProxy = true;
SluiceProfileSerializer.Save(p, args[0]);
Console.WriteLine("saved " + args[0]);
