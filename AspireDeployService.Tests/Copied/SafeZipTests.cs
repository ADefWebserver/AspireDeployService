// Copied from the SafeZip tests in BlazorDataDeploymentWebsite.Tests/Worker/ReleaseDownloaderTests.cs
// (implementation plan D2).
using AspireDeployService.Core.Archives;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests.Copied;

public class SafeZipTests : IDisposable
{
    private readonly TempDir _work = new();

    public void Dispose() => _work.Dispose();

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("root/../../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("root\\..\\..\\evil.txt")]
    public void Zip_slip_entries_are_refused(string entry)
    {
        var zipPath = _work.Combine("slip.zip");
        File.WriteAllBytes(zipPath, Zipball.Create(("root/ok.txt", "ok"), (entry, "pwned")));
        var dest = _work.Combine("out", "inner");

        Assert.Throws<InvalidDataException>(() => SafeZip.ExtractToDirectory(zipPath, dest));
        Assert.False(File.Exists(_work.Combine("out", "evil.txt")));
        Assert.False(File.Exists(_work.Combine("evil.txt")));
    }

    [Fact]
    public void Archive_without_a_single_top_level_folder_is_refused()
    {
        var zipPath = _work.Combine("flat.zip");
        File.WriteAllBytes(zipPath, Zipball.Create(("azure.yaml", "x"), ("other/Program.cs", "y")));
        var dest = _work.Combine("flat");
        SafeZip.ExtractToDirectory(zipPath, dest);

        Assert.Throws<InvalidDataException>(() => SafeZip.GetSingleTopLevelFolder(dest));
    }

    [Fact]
    public async Task Sha256_matches_the_standard_hash()
    {
        var path = _work.Combine("x.bin");
        await File.WriteAllTextAsync(path, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await SafeZip.Sha256HexAsync(path, CancellationToken.None));
    }
}
