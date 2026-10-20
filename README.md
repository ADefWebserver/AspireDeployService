# Aspire Deploy

<img width="1591" height="1304" alt="image" src="https://github.com/user-attachments/assets/31de889f-bb99-4c78-bcf3-2071b5ed98b3" />


A Windows desktop app (.NET MAUI Blazor Hybrid) that deploys **any Aspire 13.x solution**, from a local folder or a GitHub repository, to an Azure subscription with `aspire deploy`, using your own `az login`.

It **discovers** what the AppHost needs first (AppHost kind, Aspire version, parameters, resources, deployment targets) and keeps per-developer **deploy profiles** in a git-ignored `appsettings.Local.json`.

Design: [docs/AspireDeployService-Implementation-Plan.md](docs/AspireDeployService-Implementation-Plan.md), which builds on [docs/Aspire-Project-Deployer-Plan.md](docs/Aspire-Project-Deployer-Plan.md).

## Quick start

1. Install the prerequisites below, then run `az login`.
2. Start the app: open `AspireDeployService.slnx` in Visual Studio and run the **Windows Machine** profile, or run `aspire run` and start the `aspiredeployservice-windows` resource from the dashboard.
3. **Doctor**: run the general checks.
4. **Profiles → New profile**: name it, pick the solution folder (or `<owner>/<repo>`), click **Discover**, fill in the region and the parameters, and save.
5. **Deploy**: tick **Dry run** first to list the steps, then deploy for real. Every deploy shows the tenant, subscription and resource group and asks before it starts.

To remove a deployment, delete its resource group in Azure. Deleting a profile never touches Azure.

## Prerequisites

| Tool | Why |
|---|---|
| Windows 10 1809 or later | The app is Windows-only. |
| .NET 10 SDK (`global.json` pins 10.0.402, roll forward to later feature bands) with the MAUI workload | Builds this app; also builds the AppHosts you deploy. |
| Azure CLI, signed in (`az login`) | Deploys use your sign-in (`Azure__CredentialSource=AzureCli`). |
| Docker Desktop or Podman, running | Project resources are built into container images. |
| Node.js 20.19+, 22.13+ or 24+ | Only for TypeScript AppHosts. |
| GitHub CLI (`gh`), optional | Supplies a token for private repositories when none is configured. |

The Aspire CLI does **not** need to be installed: if the `aspire` on your PATH has a different version from the AppHost's SDK, the app installs the matching one under `%LOCALAPPDATA%\ads\tools\aspire\<version>`.

## Settings file

Profiles live in `appsettings.Local.json`, found in this order (shown on the **Settings** page):

1. the `ADS_SETTINGS` environment variable;
2. `AspireDeployService/appsettings.Local.json` next to the project file, when running from the source tree (Visual Studio or `aspire run`);
3. `%LOCALAPPDATA%\AspireDeployService\appsettings.Local.json` for a published copy.

`.gitignore` excludes it everywhere. [appsettings.Local.example.json](AspireDeployService/appsettings.Local.example.json) shows the shape with placeholders. Values, including secrets, are **plain text**, like Aspire's own `~/.aspire/deployments` cache. If you edit the file by hand and break the JSON, the app turns read-only and never overwrites it. Comments are read, but they are not kept when the app saves.

An empty parameter value (`""`) means "the AppHost's own configuration supplies it", so nothing is passed. A missing parameter stops a deploy.

## GitHub sources

- Downloads are pinned to a commit SHA and extracted into `%LOCALAPPDATA%\ads\w\<profile>\src`. A repository is never cloned or changed.
- **Private repositories** need a fine-grained token with **Contents: read** on the selected repositories only. The token comes from the profile, then the **Settings** page (`AspireDeploy:GitHub:Token`), then `gh auth token`. It is sent only to `api.github.com`.
- Before code from a commit is built for the first time, the app asks you to trust it. Your answers are recorded in `%LOCALAPPDATA%\ads\w\trust.jsonl`.
- Repositories that use **submodules or Git LFS** cannot be deployed from GitHub (archives leave that content out). Clone them and use a local folder.

## What the tooling writes into a local folder

The app itself never writes into your source, but the Aspire tooling it runs does: `bin/` and `obj/` from the build, and for TypeScript AppHosts `.aspire/modules/` and `node_modules/`, and possibly `aspire.config.json`. Deploy output goes to the work folder, not your source.

## Shared code

These files are copied, not referenced, from the BlazorDataDeploymentWebsite repository (plan D2). Each one starts with a comment naming its origin. Review them when the originals change:

`ProcessRunner`, `SecretRedactor`, `DeploymentLogSink`, `SafeZip`, `FileLock`, `AspireLauncher.FindAspire`, `ErrorClassifier`, `ErrorCategory`, plus their tests under `AspireDeployService.Tests/Copied`.

## Development

```bash
dotnet build AspireDeployService.slnx
```

```bash
dotnet test AspireDeployService.Tests/AspireDeployService.Tests.csproj
```

The debug-only page `/debug/runs` (not in the menu) runs the infrastructure checks: a 5-minute `ping`, a 20,000-line flood, and two dialogs at once.

## Manual checklist

Run this before a release. Rows marked **(Azure)** create real resources; delete the resource groups afterwards.

| # | Check | Expected |
|---|---|---|
| 1 | Doctor, general checks | Each tool shows Pass, Warn or Fail with a fix. |
| 2 | Discover this repository's own folder | AppHost 13.5.4, and a "no Azure compute environment" warning. |
| 3 | New profile from this repository | Refused with result code 4 and the line to add. |
| 4 | Discover an `aspire-starter` app that has `AddAzureContainerAppEnvironment` | Its parameters and resources are listed. |
| 5 | New profile for that app, then **Dry run** | The confirmation names the tenant, subscription and resource group; the steps are listed; no secret appears in the log. |
| 6 | Start a dry run, open Settings, come back | The log re-attaches. |
| 7 | Start `/debug/runs` → ping, close the window | Asks first; after "Stop and close", no `ping` process is left. |
| 8 | Start `/debug/runs` → ping, kill the app in Task Manager | No `ping` process is left. |
| 9 | **(Azure)** Deploy the starter app from a local folder | Success; endpoints in the log; Profiles shows the deployed Aspire version. |
| 10 | **(Azure)** Deploy it again with no changes | Success; no new resource group. |
| 11 | **(Azure)** Deploy from a private GitHub repository with a fine-grained token | The trust prompt shows the SHA; deploy succeeds. |
| 12 | **(Azure)** Deploy a TypeScript starter (`aspire-ts-starter`) | Success. |
| 13 | Search the log file, `history.jsonl` and `trust.jsonl` for each secret value | Not found. |
