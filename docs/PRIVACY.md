# Aspire Deploy privacy policy

Last updated: October 24, 2026

Aspire Deploy is a Windows desktop app published by BlazorData.net (ADefWebserver). This policy covers the app from the Microsoft Store and the source in this repository.

## What we collect

Nothing. The app has no analytics, telemetry, advertising or crash reporting, and it never sends data to the publisher.

## What stays on your computer

- **Deploy profiles and settings** (`appsettings.Local.json`): solution paths, Azure subscription, region and resource group names, and the parameter values you enter. Secret parameter values and AI API keys are stored in this file in plain text, so protect it like any other credential file.
- **Run logs and working folders** under `%LOCALAPPDATA%`. Secrets are redacted from the logs.

You can delete these files at any time. Uninstalling the app removes the Store package's own copy of them.

## What the app sends, and to whom

The app only connects to services you choose, using your own accounts:

| Service | When | What is sent |
|---|---|---|
| Microsoft Azure (through your `az login` and `aspire deploy`) | When you scan, look up or deploy to a subscription | Your solution's build output, container images and the resource settings you choose |
| GitHub | When a profile uses a GitHub repository | The repository name and commit; a token if you provide one |
| The AI provider you configure (OpenAI, Azure OpenAI, Azure AI Foundry, Anthropic or Google AI), only if you turn AI Help on | When you ask the AI Help a question | Your question, the page you are on, and the files the assistant reads to answer it, with secrets removed |
| NuGet and the .NET and Aspire download sites | When tools or packages are installed or restored | Standard package requests |

Those services handle the data under their own privacy policies.

## Children

The app is a developer tool and is not directed at children.

## Contact

Open an issue at <https://github.com/ADefWebserver/AspireDeployService/issues>.
