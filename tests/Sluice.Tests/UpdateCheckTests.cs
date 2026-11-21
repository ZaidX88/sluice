using Sluice.Core.Updates;
using Kind = Sluice.Core.Updates.UpdateCheck.PackageKind;

namespace Sluice.Tests;

/// <summary>The update check's choices (#197), from a saved sample of GitHub's releases JSON: no network.</summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("1.0.0-dev", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]     // numeric parts compare as numbers
    [InlineData("1.0.0-rc.9", "1.0.0")]           // a release ranks above its candidates
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.9", "1.1.0")]
    [InlineData("1.9.0", "2.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]  // more identifiers rank higher
    [InlineData("1.0.0-1", "1.0.0-alpha")]        // numeric identifiers rank below alphanumeric ones
    public void Versions_Order(string lower, string higher)
    {
        var (a, b) = (SemVersion.TryParse(lower)!, SemVersion.TryParse(higher)!);
        Assert.True(a.CompareTo(b) < 0, $"{lower} < {higher}");
        Assert.True(b.CompareTo(a) > 0, $"{higher} > {lower}");
    }

    [Fact]
    public void Build_Metadata_IsIgnored()
    {
        Assert.Equal(0, SemVersion.TryParse("1.0.0+abc1234")!.CompareTo(SemVersion.TryParse("1.0.0+def5678")));
        Assert.Equal("1.0.0-rc.1", SemVersion.TryParse("1.0.0-rc.1+abc1234")!.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("v1.0.0")]        // the caller strips the tag's "v"
    [InlineData("1.0.x")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-rc..1")]
    [InlineData("1.0.0-rc_1")]
    [InlineData("-1.0.0")]
    public void Malformed_Versions_AreRejected(string? text) => Assert.Null(SemVersion.TryParse(text));

    // The shape of GET /repos/{owner}/{repo}/releases, newest first, trimmed to the fields the check reads.
    private const string Releases = """
        [
          { "tag_name": "v1.2.0", "draft": true,  "prerelease": false, "html_url": "https://github.com/ZaidX88/sluice/releases/tag/v1.2.0", "assets": [] },
          { "tag_name": "v1.1.0-rc.1", "draft": false, "prerelease": true, "html_url": "https://github.com/ZaidX88/sluice/releases/tag/v1.1.0-rc.1",
            "assets": [ { "name": "Sluice-Setup-1.1.0-rc.1-x64.exe", "browser_download_url": "https://github.com/ZaidX88/sluice/releases/download/v1.1.0-rc.1/Sluice-Setup-1.1.0-rc.1-x64.exe" } ] },
          { "tag_name": "v1.0.1", "draft": false, "prerelease": false, "html_url": "https://github.com/ZaidX88/sluice/releases/tag/v1.0.1",
            "assets": [
              { "name": "Sluice-Setup-1.0.1-x64.exe", "browser_download_url": "https://github.com/ZaidX88/sluice/releases/download/v1.0.1/Sluice-Setup-1.0.1-x64.exe" },
              { "name": "Sluice-Setup-1.0.1-x64.exe.sha256", "browser_download_url": "https://github.com/ZaidX88/sluice/releases/download/v1.0.1/Sluice-Setup-1.0.1-x64.exe.sha256" },
              { "name": "sluice-1.0.1-linux-x64.tar.gz", "browser_download_url": "https://github.com/ZaidX88/sluice/releases/download/v1.0.1/sluice-1.0.1-linux-x64.tar.gz" },
              { "name": "sluice_1.0.1_amd64.deb", "browser_download_url": "https://github.com/ZaidX88/sluice/releases/download/v1.0.1/sluice_1.0.1_amd64.deb" }
            ] },
          { "tag_name": "not-a-version", "draft": false, "prerelease": false, "html_url": "https://github.com/ZaidX88/sluice/releases/tag/x", "assets": [] },
          { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "html_url": "https://github.com/ZaidX88/sluice/releases/tag/v1.0.0", "assets": [] }
        ]
        """;

    [Fact]
    public void A_Release_User_IsOffered_TheNewestRelease_NotDraftsOrCandidates()
    {
        var u = UpdateCheck.PickNewer(Releases, "1.0.0+abc1234", includePreReleases: false, Kind.WindowsInstaller)!;
        Assert.Equal("1.0.1", u.Version);
        Assert.Equal("https://github.com/ZaidX88/sluice/releases/tag/v1.0.1", u.ReleasePage.ToString());
        Assert.EndsWith("/v1.0.1/Sluice-Setup-1.0.1-x64.exe", u.Download!.ToString());
    }

    [Fact]
    public void A_Candidate_User_IsOffered_TheNewestCandidate()
    {
        var u = UpdateCheck.PickNewer(Releases, "1.0.1-rc.1", includePreReleases: true, Kind.WindowsInstaller)!;
        Assert.Equal("1.1.0-rc.1", u.Version);
    }

    [Theory]
    [InlineData("1.0.1")]
    [InlineData("1.0.2-dev+abc1234")]
    [InlineData("2.0.0")]
    public void UpToDate_OrNewer_GetsNothing(string current) =>
        Assert.Null(UpdateCheck.PickNewer(Releases, current, includePreReleases: false, Kind.WindowsInstaller));

    [Theory]
    [InlineData(Kind.WindowsInstaller, "Sluice-Setup-1.0.1-x64.exe")]
    [InlineData(Kind.UbuntuDeb, "sluice_1.0.1_amd64.deb")]
    [InlineData(Kind.LinuxTarGz, "sluice-1.0.1-linux-x64.tar.gz")]
    public void The_Package_Matches_The_System(Kind kind, string file) =>
        Assert.EndsWith("/" + file, UpdateCheck.PickNewer(Releases, "1.0.0", false, kind)!.Download!.ToString());

    [Fact]
    public void Ubuntu_FallsBack_ToTheTarGz_WithoutADeb()
    {
        var assets = new Dictionary<string, Uri> { ["sluice-1.0.1-linux-x64.tar.gz"] = new("https://example.com/t.tar.gz") };
        Assert.Equal("https://example.com/t.tar.gz", UpdateCheck.PickAsset(assets, "1.0.1", Kind.UbuntuDeb)!.ToString());
        Assert.Null(UpdateCheck.PickAsset(assets, "1.0.1", Kind.WindowsInstaller));   // the release page is the fallback
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    [InlineData("""[{"tag_name": 5}]""")]
    public void Bad_Answers_GiveNothing(string json) =>
        Assert.Null(UpdateCheck.PickNewer(json, "1.0.0", false, Kind.WindowsInstaller));

    [Theory]
    [InlineData(true, "ID=ubuntu", "VERSION_ID=\"24.04\"")]
    [InlineData(true, "ID=linuxmint", "ID_LIKE=\"ubuntu debian\"")]
    [InlineData(false, "ID=debian")]
    [InlineData(false, "ID=fedora", "ID_LIKE=\"rhel centos\"")]
    [InlineData(false, "ID=arch")]
    public void Ubuntu_IsRecognised(bool ubuntu, params string[] osRelease) =>
        Assert.Equal(ubuntu, UpdateCheck.IsUbuntu(osRelease));
}
