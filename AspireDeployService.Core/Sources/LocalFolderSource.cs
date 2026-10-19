using AspireDeployService.Core.Logging;
using AspireDeployService.Core.Outcomes;

namespace AspireDeployService.Core.Sources;

/// <summary>A local folder is used in place. Aspire's own tooling writes bin/obj (and more) into it.</summary>
public sealed class LocalFolderSource : ISourceProvider
{
    public SourceKind Kind => SourceKind.LocalFolder;

    public async Task<SourceIdentity> MaterializeAsync(SourceSpec spec, string workKeyDir, RunServices run, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(spec.Location))
            throw new DeployerException(OutcomeCode.InvalidInput, $"'{spec.Location}' is not an absolute folder path.");
        var folder = Path.GetFullPath(spec.Location);
        if (!Directory.Exists(folder))
            throw new DeployerException(OutcomeCode.InvalidInput, $"The folder does not exist: {folder}");
        await run.Log.InfoAsync($"Using the local folder {folder}.", ct);
        return new SourceIdentity(folder, folder, null);
    }
}
