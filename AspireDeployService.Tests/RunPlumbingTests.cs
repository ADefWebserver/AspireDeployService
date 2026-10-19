using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;
using AspireDeployService.Core.Processes;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Sources;
using AspireDeployService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AspireDeployService.Tests;

public class WorkKeysTests
{
    [Fact]
    public void Profile_key_is_the_profile_folder() =>
        Assert.Equal(Path.Combine("W", "shop"), WorkKeys.For("W", "shop", null));

    [Fact]
    public void Doctor_without_a_profile_has_its_own_key() =>
        Assert.Equal(Path.Combine("W", "doctor"), WorkKeys.For("W", null, null));

    [Fact]
    public void GitHub_ad_hoc_key_ignores_case_and_ref()
    {
        var a = WorkKeys.For("W", null, new SourceSpec(SourceKind.GitHub, "Owner/Repo", "main", null));
        var b = WorkKeys.For("W", null, new SourceSpec(SourceKind.GitHub, "owner/repo", "v2", null));
        Assert.Equal(a, b);
        Assert.Matches(@"adhoc-[0-9a-f]{12}$", a);
    }

    [Fact]
    public void Local_ad_hoc_key_ignores_case_and_trailing_separator()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var a = WorkKeys.For("W", null, new SourceSpec(SourceKind.LocalFolder, @"C:\X\", null, null));
        var b = WorkKeys.For("W", null, new SourceSpec(SourceKind.LocalFolder, @"c:\x", null, null));
        Assert.Equal(a, b);
        Assert.NotEqual(a, WorkKeys.For("W", null, new SourceSpec(SourceKind.GitHub, "c/x", null, null)));
    }
}

public class WorkKeyLockTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Second_lock_fails_at_once_with_code_2()
    {
        await using var first = WorkKeyLock.Acquire(_dir.Path, "shop");
        var ex = Assert.Throws<DeployerException>(() => WorkKeyLock.Acquire(_dir.Path, "shop"));
        Assert.Equal(OutcomeCode.InvalidInput, ex.Code);
        Assert.Contains("shop", ex.Message);
    }

    [Fact]
    public async Task Lock_is_released_after_an_exception()
    {
        try
        {
            await using var held = WorkKeyLock.Acquire(_dir.Path, "shop");
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
        }

        await using (WorkKeyLock.Acquire(_dir.Path, "shop"))
        {
        }
        Assert.False(File.Exists(WorkKeyLock.PathFor(_dir.Path)));
    }
}

public class DeploySecretRedactorTests
{
    [Fact]
    public void Masks_short_secrets_that_the_inner_redactor_ignores()
    {
        var redactor = new DeploySecretRedactor();
        redactor.AddSecret("abc12");
        Assert.Equal("key is [REDACTED] here", redactor.Redact("key is abc12 here"));
    }

    [Fact]
    public void Still_masks_long_secrets_and_known_patterns()
    {
        var redactor = new DeploySecretRedactor();
        redactor.AddSecret("a-long-github-token-value");
        Assert.DoesNotContain("a-long-github-token-value", redactor.Redact("token a-long-github-token-value"));
        Assert.Contains("[REDACTED-JWT]", redactor.Redact("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.abcdef"));
    }

    [Fact]
    public void Each_run_has_its_own_secrets()
    {
        var runA = new DeploySecretRedactor();
        var runB = new DeploySecretRedactor();
        runA.AddSecret("run-a-secret-value");
        Assert.Equal("run-a-secret-value", runB.Redact("run-a-secret-value"));
    }
}

public class RunLogSinkTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Lines_are_redacted_and_reach_the_observer_and_the_file()
    {
        var redactor = new DeploySecretRedactor();
        redactor.AddSecret("pw123");
        var observer = new ListRunObserver();
        var file = _dir.Combine("logs", "run.log");

        await using (var sink = new RunLogSink(Guid.NewGuid(), file, redactor, observer))
            await sink.WriteAsync(LogStreams.StdOut, "password is pw123");

        Assert.Equal("password is [REDACTED]", Assert.Single(observer.Lines).Text);
        var text = File.ReadAllText(file);
        Assert.Contains("password is [REDACTED]", text);
        Assert.DoesNotContain("pw123", text);
    }

    [Fact]
    public async Task Ansi_color_codes_are_removed()
    {
        var observer = new ListRunObserver();
        await using (var sink = new RunLogSink(Guid.NewGuid(), _dir.Combine("a.log"), new DeploySecretRedactor(), observer))
            await sink.WriteAsync(LogStreams.StdOut, "\u001b[1;38;5;2m6.\u001b[0m \u001b[38;5;14mbuild-web\u001b[0m \u001b]8;;https://x\u0007link\u001b]8;;\u0007");
        Assert.Equal("6. build-web link", Assert.Single(observer.Lines).Text);
    }

    [Fact]
    public async Task A_broken_file_keeps_the_observer_working_and_warns_once()
    {
        // A directory where the file should be makes every open fail.
        var file = _dir.Combine("logs", "run.log");
        Directory.CreateDirectory(file);
        var observer = new ListRunObserver();

        await using (var sink = new RunLogSink(Guid.NewGuid(), file, new DeploySecretRedactor(), observer))
        {
            await sink.WriteAsync(LogStreams.StdOut, "one");
            await sink.WriteAsync(LogStreams.StdOut, "two");
        }

        Assert.Equal(["one", "two"], observer.Lines.Where(l => l.Stream == LogStreams.StdOut).Select(l => l.Text));
        Assert.Single(observer.Lines, l => l.Stream == LogStreams.System && l.Text.Contains("cannot be written"));
    }
}

public class RunContextFactoryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Runner_uses_the_per_run_redactor()
    {
        var observer = new ListRunObserver();
        var factory = new RunContextFactory(NullLoggerFactory.Instance, TimeProvider.System);
        await using var run = factory.Create(_dir.Path, observer);
        run.Redactor.AddSecret("s3c");

        var spec = OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd", ["/c", "echo value s3c end"], _dir.Path)
            : new ProcessSpec("sh", ["-c", "echo value s3c end"], _dir.Path);
        await run.Runner.RunAsync(spec, run.Log, CancellationToken.None);

        Assert.Contains(observer.Lines, l => l.Text.Contains("value [REDACTED] end"));
        Assert.DoesNotContain(observer.Lines, l => l.Text.Contains("s3c"));
        Assert.Equal(run.LogFile, observer.LogFile);
        Assert.StartsWith(Path.Combine(_dir.Path, "logs"), run.LogFile);
    }
}
