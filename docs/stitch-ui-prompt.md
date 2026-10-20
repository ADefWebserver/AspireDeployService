Design a modern, polished desktop app UI called **Aspire Deploy**. It is a Windows desktop tool (window around 1280×800, should also work down to ~1000px wide) that developers use to deploy .NET Aspire applications to Microsoft Azure. Tone: a professional developer tool, calm and confident, in the spirit of Linear, Vercel and GitHub Desktop. Light theme by default; please also show a dark theme version.

## Layout (important)
- **No left sidebar.** Navigation is a **horizontal top app bar** across the full width.
- Top bar, left to right: small logo mark + "Aspire Deploy" wordmark; horizontal nav tabs **Home · Setup · Discover · Profiles · Settings** (active tab marked with an underline or pill); on the right, a compact "signed in to Azure" status chip (account name + tenant) and a small settings/theme toggle icon.
- A nav tab shows a tiny animated spinner/dot next to its label while a background task for that page is running (e.g. "Profiles ●" while a deploy runs).
- Under the top bar, each page has a page header (title, one-line description, primary action on the right), then content in a centered column, max width ~1100px, generous whitespace.
- Below 1000px wide, the nav tabs collapse into a horizontal scrolling tab strip (still no side drawer).

## Visual style
- Primary color: a deep indigo/violet (Aspire purple, ~#512BD4), with an Azure blue (#0078D4) accent used sparingly.
- Neutral gray surfaces, soft 1px borders, 10–12px rounded corners, subtle shadows only on raised cards and dialogs.
- Font: Inter or Segoe UI Variable for UI text; JetBrains Mono for paths, IDs, commands and logs.
- Status colors: green = Pass/Succeeded, amber = Warn, red = Fail/Error, blue = Running. Use small rounded status pills with an icon, not plain colored text.
- Clean line icons (Fluent or Lucide style).

## Shared components (use consistently on every page)
1. **Run status banner**: while a task runs, a blue banner with spinner, "Deploy orders-dev is running", and a Cancel button (shows "stopping…" after click). When done, green success or red failure banner with a multi-line message and a small "Result code 3 (AzureAuth), category Authentication" line.
2. **Live log console**: dark terminal-style panel (monospace, ~360px tall, auto-scrolls), stderr lines tinted red/amber, toolbar with "Copy", "Wrap lines" and "Follow" toggles; under it a muted "Log file: C:\…\deploy.log" line. Empty state: "No output yet."
3. **Source picker**: segmented control "Local folder | GitHub repository". Local: "Solution folder" text field with "Browse…" button and inline error "This folder does not exist." GitHub: "Repository (owner/repo)" field + "Branch, tag or SHA" field. Both: optional "AppHost path" field with placeholder "found automatically".
4. **Secret field**: masked value showing "•••••• (set)" with Replace / Clear / Show-eye actions.
5. **Confirmation dialog**: centered modal over a dimmed, blurred backdrop, title, message (may list tenant, subscription, resource group), Cancel and a primary confirm button (e.g. "Deploy", "Trust this commit").

## Screens to design

### 1. Home
- Hero: "Aspire Deploy" with the lead text "Discover what an Aspire 13.x AppHost needs, keep per-developer deploy profiles, and deploy it to Azure with `aspire deploy` and your own `az login`."
- A 4-step "Getting started" stepper/cards row: **Check prerequisites → Discover an AppHost → Create a profile → Deploy**, each card with an icon, one line of text and a button.
- "Settings file" card: monospace path `C:\Users\dev\AppData\Local\AspireDeployService\appsettings.Local.json`, "Exists. Profiles: 3." Show an alternate error state: red callout "The settings file cannot be read, so the app is read-only until it is fixed."
- "Recent profiles" mini list (3 rows) with quick Deploy buttons.

### 2. Setup (prerequisite checks)
- Header: "Setup", description "Checks the tools a deployment needs. With a profile, it also fetches the source and checks what its AppHost needs, and installs the matching Aspire CLI."
- Toolbar: "Profile" dropdown (default "(general checks only)") + primary "Run checks" button.
- Results as a clean list/table of checks: .NET SDK, Azure CLI signed in, Docker running, Node.js, GitHub CLI, Aspire CLI version. Columns: Check, State pill (Pass/Warn/Fail), Detail, Fix (Fix text in a subtle box with a copy button for commands like `az login`).
- Summary chips above the table: "5 passed · 1 warning · 0 failed".
- Log console at the bottom (collapsible).

### 3. Discover
- Header "Discover"; description: "Stage 1 reads files only. Stage 2 runs `aspire do publish-manifest`, which builds and runs the AppHost on this machine. Nothing is created in Azure."
- Form card: Source picker, a "Static scan only (runs nothing)" switch, primary "Discover" button.
- Results:
  - "AppHost" summary card in a 2-column definition grid: Path, Kind (C# project / file-based / TypeScript), Aspire SDK 13.5.4, Source, Declared targets (chips like "azure-container-apps"), global.json SDK.
  - Amber warning callouts (e.g. "No Azure compute environment is declared").
  - "Parameters" table: Name, Secret, Default, Generated (as check/dash icons), Configuration key (monospace).
  - "Resources" table: Name, Type (badge), Image or project (monospace), Azure, Needs container runtime.
  - Action row: "Copy JSON" (secondary) and "Create profile from this" (primary).
- Log console.

### 4. Profiles (list)
- Header "Profiles" with primary "New profile" button.
- Profile cards or a refined table: name (link), source badge ("LocalFolder" / "GitHub") + source text, subscription ID (monospace, truncated with copy), resource group + region, "Last deployed with Aspire 13.5.4". Row actions: primary "Deploy" (shows "Deploying…" with spinner when running), "Edit", and a red "Delete" in an overflow menu.
- Empty state illustration: "No profiles yet. Create one from a discovered AppHost."
- Delete confirmation panel/dialog: "Delete profile orders-dev?", "This edits only the local settings file. Nothing in Azure is touched; the resource group stays.", checkbox "Also delete its work folder (source copy, logs, history)", red Delete + Cancel.

### 5. New profile (wizard)
- A 3-step horizontal stepper: **1 Source → 2 Destination → 3 Parameters**.
- Step 1: "Profile name" (placeholder "orders-dev", inline rule "2 to 40 lower-case letters, digits or dashes"), Source picker, "Overwrite an existing profile with this name" checkbox, "Discover" button. Run banner + log while discovering.
- Step 2 "Destination": Subscription dropdown listing "Contoso Dev (GUID) — default" with a "Type an ID instead" link, Tenant ID, Region (placeholder eastus2), Resource group, Environment.
- Step 3 "Parameters": one field per parameter with monospace name, small caption showing config key and "secret", masked input for secrets, and a checkbox "Leave empty (the AppHost's own configuration supplies it)". Muted note listing optional parameters not asked.
- Footer: "Save profile". Success state: green banner "Profile orders-dev saved. Deploy it or review it."

### 6. Profile detail / edit
- Header "Profile orders-dev" with actions "Deploy…" (primary), "Save", "Back".
- Info banner when locked: "A deployment of this profile is running; editing is disabled until it ends."
- Sections as cards:
  - **Source**: Kind, Location (monospace), Branch/tag/SHA, AppHost path, GitHub token (secret field).
  - **Destination**: Subscription ID, Tenant ID (optional), Region, Resource group, Credential source (placeholder AzureCli), Environment (placeholder Production), Container runtime select (default/docker/podman), switch "Allow creating the resource group".
  - **Parameters**: editable table — Name (monospace), Value (text or secret field), Secret toggle, Config key override (placeholder `Parameters:name`), Remove icon button; below it an inline "parameter name" input + "Add parameter". Helper text: "An empty value means the AppHost's own configuration supplies it."
- Footer meta: "Last deployed with Aspire: 13.5.4".

### 7. Deploy
- Header "Deploy orders-dev".
- Summary card with the deployment target, styled like a "flight check": Source, Subscription (monospace), Resource group "rg-orders-dev in eastus2", Environment, Last deployed with.
- Options row: "Ref for this run" field (GitHub only), switches "Dry run (list the steps only)" and "Clear Aspire's deploy cache", big primary button whose label changes between "Deploy" and "List steps".
- While running: progress area with a step timeline (Build images → Push to registry → Provision Azure resources → Deploy containers) with per-step status, plus the run banner and the live log console filling most of the page.
- Finished: success banner with extra line "Resource group: rg-orders-dev. To remove everything, delete that resource group in Azure." Show the failure variant too.
- Show the pre-deploy confirmation dialog listing tenant, subscription and resource group with a "Deploy" confirm button.

### 8. Settings
- Header "Settings".
- "Locations" card: Settings file, Work root, Tools root — each a monospace path with an "Open folder" link button and a caption like "Found by: next to the project file".
- "GitHub token" card: explanation text ("`AspireDeploy:GitHub:Token` is used for private repositories when a profile has no token of its own. Use a fine-grained token with **Contents: read**…"), status "(set)" / "(not set)", Replace and Clear buttons; replace mode shows a masked input (placeholder github_pat_...) with Save/Cancel. Success/error toast after saving.
- Red error state when the settings file cannot be parsed.

### 9. Not found
- Friendly minimal empty state "Not found — the page you are looking for does not exist." with a "Go home" button.

## Also include
- An in-page error card: "This page hit an unexpected error (result code 1): … Running work is not affected." with "Try again".
- Consistent form styling: labels above fields, muted helper text, inline validation in red, disabled states clearly visible.
- Use realistic sample data (profiles "orders-dev", "storefront-prod", "eshop-staging"; region eastus2; Aspire 13.5.4).
