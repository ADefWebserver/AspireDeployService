using System.Net;
using AspireDeployService.Core.Deploy;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Tests.Support;

namespace AspireDeployService.Tests;

/// <summary>Fixes from the second review of PR #1 (the first review's fixes are in ReviewFixTests.cs).</summary>
public class ReservedParameterMappingTests
{
    private static DeployProfile Profile() => new()
    {
        Destination = new ProfileDestination { SubscriptionId = "00000000-0000-0000-0000-000000000001", Location = "eastus2", ResourceGroup = "rg-ok" },
        Parameters = { ["sub"] = "11111111-1111-1111-1111-111111111111" }
    };

    [Theory]
    [InlineData("Azure:SubscriptionId")]
    [InlineData("azure:resourcegroup")]
    [InlineData("ASPIRE_CONTAINER_RUNTIME")]
    [InlineData("DOTNET_ENVIRONMENT")]
    [InlineData("PATH")]
    public void Override_onto_a_deployment_setting_is_refused(string key)
    {
        var p = Profile();
        p.ParameterConfigKeys["sub"] = key;

        var ex = Assert.Throws<DeployerException>(() => DeployEnvironment.Build(p, [], null, _ => null));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("sub", ex.Message);
        Assert.Contains(ProfileRules.Validate(p), e => e.StartsWith("ParameterConfigKeys:sub", StringComparison.Ordinal));
    }

    [Fact]
    public void Ordinary_keys_and_the_confirmed_destination_are_kept()
    {
        var p = Profile();
        p.ParameterConfigKeys["sub"] = "MyApp:Subscription";

        var env = DeployEnvironment.Build(p, [], null, _ => null);

        Assert.Equal("11111111-1111-1111-1111-111111111111", env["MyApp__Subscription"]);
        Assert.Equal("00000000-0000-0000-0000-000000000001", env["Azure__SubscriptionId"]);
        Assert.DoesNotContain(ProfileRules.Validate(p), e => e.StartsWith("ParameterConfigKeys:", StringComparison.Ordinal));
    }
}

public class MultilineSecretTests
{
    [Fact]
    public void Each_line_of_a_multi_line_secret_is_masked_on_its_own()
    {
        var redactor = new DeploySecretRedactor();
        redactor.AddSecret("first-fragment-1234\r\nsecond-fragment-5678\n");

        Assert.Equal("[REDACTED]", redactor.Redact("first-fragment-1234"));
        Assert.Equal("value: [REDACTED]", redactor.Redact("value: second-fragment-5678"));
    }
}

public class DeleteUnderLockTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Lock_file_is_outside_the_work_folder_so_the_folder_can_be_deleted_while_locked()
    {
        var workKey = _dir.Combine("w", "shop");
        Directory.CreateDirectory(Path.Combine(workKey, "logs"));

        await using (WorkKeyLock.Acquire(workKey, "shop"))
        {
            Assert.False(File.Exists(Path.Combine(workKey, ".lock")));
            Assert.True(File.Exists(_dir.Combine("w", ".locks", "shop.lock")));
            Directory.Delete(workKey, recursive: true);
            Assert.Throws<DeployerException>(() => WorkKeyLock.Acquire(workKey, "shop"));
        }
        Assert.False(File.Exists(_dir.Combine("w", ".locks", "shop.lock")));
    }

    [Fact]
    public async Task Delete_removes_the_work_folder_before_the_lock_is_released()
    {
        var settings = _dir.Combine("appsettings.Local.json");
        File.WriteAllText(settings, $$"""{ "AspireDeploy": { "WorkRoot": {{System.Text.Json.JsonSerializer.Serialize(_dir.Combine("w"))}} } }""");
        var service = new Core.Services.ProfileService(new ProfileStore(new FixedLocator(settings)));
        await service.SaveAsync("shop", ProfileStoreTests.ValidProfile(_dir.Path), false, CancellationToken.None);
        Directory.CreateDirectory(_dir.Combine("w", "shop", "src"));

        await service.DeleteAsync("shop", deleteWorkFolder: true, CancellationToken.None);

        Assert.False(Directory.Exists(_dir.Combine("w", "shop")));
        await using (WorkKeyLock.Acquire(_dir.Combine("w", "shop"), "shop"))
        {
        }
    }
}

public class PartialDownloadCleanupTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task An_oversized_download_leaves_no_zip_behind()
    {
        _h.ServeRepository();
        var workKey = _h.Dir.Combine("wk");
        await using var run = _h.Runs.Create(workKey, _h.Observer);
        var source = new GitHubSource(new StubHttpClientFactory(_h.GitHub)) { MaxDownloadBytes = 100 };

        await Assert.ThrowsAsync<DeployerException>(() =>
            source.MaterializeAsync(new SourceSpec(SourceKind.GitHub, "owner/repo", "main", null), workKey, run.Services, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(workKey, "download", Harness.Sha + ".zip")));
    }

    [Fact]
    public async Task A_broken_connection_leaves_no_zip_behind()
    {
        _h.GitHub.On("https://api.github.com/repos/owner/repo/commits/", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{Harness.Sha}\"}}") });
        _h.GitHub.On("https://api.github.com/repos/owner/repo/zipball/", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) });
        var workKey = _h.Dir.Combine("wk");
        await using var run = _h.Runs.Create(workKey, _h.Observer);
        var source = new GitHubSource(new StubHttpClientFactory(_h.GitHub));

        await Assert.ThrowsAsync<IOException>(() =>
            source.MaterializeAsync(new SourceSpec(SourceKind.GitHub, "owner/repo", "main", null), workKey, run.Services, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(workKey, "download", Harness.Sha + ".zip")));
    }

    /// <summary>Returns some bytes, then fails like a dropped connection.</summary>
    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ > 0)
                throw new IOException("connection reset");
            buffer[offset] = 0x50;
            return 1;
        }
    }
}
