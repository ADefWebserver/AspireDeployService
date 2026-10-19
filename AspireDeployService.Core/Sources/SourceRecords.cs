using System.Text.Json.Serialization;
using AspireDeployService.Core.Logging;

namespace AspireDeployService.Core.Sources;

[JsonConverter(typeof(JsonStringEnumConverter<SourceKind>))]
public enum SourceKind { LocalFolder, GitHub }

/// <param name="Location">An absolute folder path, or "&lt;owner&gt;/&lt;repo&gt;".</param>
public sealed record SourceSpec(SourceKind Kind, string Location, string? Ref, string? AppHostPath)
{
    public string Display => Kind == SourceKind.GitHub
        ? $"{Location}@{Ref ?? "default branch"}"
        : Location;
}

/// <param name="RootFolder">The folder the AppHost path is relative to.</param>
/// <param name="CommitSha">The resolved commit for a GitHub source; null for a local folder.</param>
public sealed record SourceIdentity(string RootFolder, string Display, string? CommitSha);

public interface ISourceProvider
{
    SourceKind Kind { get; }

    Task<SourceIdentity> MaterializeAsync(SourceSpec spec, string workKeyDir, RunServices run, CancellationToken ct);
}

/// <summary>The per-run services a source provider may use: the log and the redactor for tokens.</summary>
public sealed record RunServices(IDeploymentLogSink Log, ISecretRedactor Redactor);
