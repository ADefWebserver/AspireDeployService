Redesign the UI of **Aspire Deploy**, a Windows desktop app (about 1280×800, must still work at 1000px wide) that developers use to deploy .NET Aspire applications to Microsoft Azure Container Apps with their own `az login`. Make it look like a premium, polished professional developer tool, in the spirit of Linear, Vercel, Raycast and GitHub Desktop: calm, confident, precise, generous whitespace, and nothing decorative that does not help the user. Design **light and dark themes** for every screen.

Two goals for this pass:
1. **Much richer iconography.** Every navigation item, card header, status, resource type, empty state and primary action gets a clear icon. Add small spot illustrations for empty states and success moments.
2. **Wizard-style step flows wherever the work is sequential**: creating a profile, the Setup checks, a deployment in progress, and first-time AI setup. Elsewhere (lists, settings, editing an existing profile) keep plain, scannable pages.

## Layout
- **No left sidebar.** A full-width **horizontal top app bar**: logo mark + "Aspire Deploy" wordmark; tabs **Home · Setup · Profiles · Settings · Guide**, each with a 16px icon (home, stethoscope, layers, sliders, book-open); on the right, an **Azure account chip** (avatar initial, "webmaster@contoso.com", tenant name; amber "Not signed in" variant with a "Sign in" action), a "2 running" activity chip with a spinner when background tasks run, and a sun/moon theme toggle.
- A tab shows a small pulsing dot while one of its tasks runs (for example "Profiles ●" during a deploy).
- Each page: an eyebrow line (small caps, with an icon), title, one-line lead, primary action on the right. Content sits in a centered column, max ~1100px.
- Under 1000px, the tabs become a horizontally scrolling strip, and two-column grids stack.

## Visual language
- Primary: Aspire violet (#512BD4); accent: Azure blue (#0078D4), used sparingly for Azure-specific things (regions, subscriptions, "azure-container-apps" chips).
- Neutral surfaces with soft 1px borders, 12px card radius, 8px control radius, very subtle elevation only on cards that float (dialogs, popovers, side panel).
- Type: Inter (or Segoe UI Variable) for UI; JetBrains Mono for paths, IDs, SHAs, commands and logs.
- Status: green = passed/succeeded/new, amber = warning/updates existing, red = failed/conflict, blue = running, gray = not checked. Always a **pill with an icon**, never colored text alone.
- **Icons:** one consistent line set (Lucide or Fluent, 1.75px stroke), 16px inline and 20px in card headers, placed inside soft tinted squares ("icon tiles") in card headers and list rows. Use resource-type icons consistently: project (terminal), container (box), Dockerfile (layers), database (cylinder), Azure resource (cloud), parameter (key), secret (lock), GitHub (github mark), local folder (folder).
- Motion: 150–200ms fades and slides; steps that finish get a check-mark draw animation; nothing bounces.
- Accessible: 4.5:1 text contrast in both themes, visible focus rings, every icon-only button has a tooltip.

## Shared components (design once, reuse everywhere)
1. **Stepper**: horizontal, numbered circles joined by lines; states *done* (check), *current* (filled violet, bold label), *upcoming* (outlined), *blocked* (lock icon with a tooltip saying why). Each step shows a one-line subtitle under its label once done (for example "Scan · 5d3aed3").
2. **Run banner**: blue with a spinner, "Deployment of orders-dev is running", Cancel button ("Stopping…" after click); then a green success or red failure state with a multi-line message, a muted "Result code 3 (MissingPrerequisite), category Permissions" line, and an "AI Help" sparkle button on failures.
3. **Terminal console**: dark panel, monospace, timestamps in a muted gutter, stderr tinted amber/red, toolbar with Copy, Wrap, Follow and "Ask AI about this log"; collapsible header "Terminal output / deployment"; footer "Log file: C:\…\run.log". Empty: "No output yet."
4. **Callouts**: info (blue), warning (amber), error (red), each with an icon, optional title and an inline action ("AI Help", "Read the guide").
5. **Searchable select**: dropdown with a filter box, grouped items (for example regions grouped by geography), secondary detail text, disabled items with a reason chip ("No Container Apps", "Disabled").
6. **Secret field**: "•••••• (set)" with Show, Replace and Clear.
7. **Segmented control**: for exclusive choices ("Local folder | GitHub repository", "New deployment | Update an existing deployment", "Let Aspire create | Use existing | Create new").
8. **Confirmation dialog**: centered modal over a dimmed, blurred backdrop, with an icon tile, title, a definition list of what will happen, Cancel and a primary (or danger) confirm.
9. **AI Help side panel**: slides in from the right (~420px) over the page; header "AI Help" with sparkle icon, context title and "Provider · model"; a collapsible "Context sent" box; suggestion chips; chat bubbles with streamed markdown and code blocks with Copy; input at the bottom ("Enter sends, Shift+Enter for a new line"); footer note "Read-only: the AI never changes files."

## Screens

### 1. Home
- Hero: eyebrow "Aspire 13.x · Azure"; title "Aspire Deploy"; lead "Scan an Aspire AppHost for what it needs, keep per-developer deploy profiles, and deploy it to Azure with `aspire deploy` and your own `az login`." Actions: "Run checks" (secondary), "New profile" (primary).
- A **3-step journey** drawn as a connected horizontal path of large cards with icon tiles and a dotted connector: **1 Check prerequisites → 2 Create a profile → 3 Deploy**. Each card has a state pill ("Passed", "Running", "3 saved") and a button. Highlight the next recommended step.
- "Recent profiles" list: icon tile (GitHub or folder), name, source, resource group with cloud icon, a "Deploy" button that turns into "Deploying…" with a spinner.
- "Settings file" card with the monospace path, Copy, and a "Loaded / Not created yet / Cannot be read" pill; the error variant uses a red callout.

### 2. Setup (guided checklist)
- Toolbar: "Profile scope" select ("General checks only" or a profile) + "Run checks".
- Show the checks as a **vertical progress checklist**: .NET SDK → Azure CLI signed in → Container runtime → Node.js → GitHub CLI → Aspire CLI for this AppHost. Each row: tool icon tile, name, status pill, detail, and on Warn/Fail an expandable "Fix" box with a copyable command (`az login`) and, for sign-in, an inline **"Sign in to Azure"** button.
- Summary ring at the top: "5 passed · 1 warning · 0 failed"; empty state illustration "No checks run yet".

### 3. Profiles
- Grid or refined list of **profile cards**: icon tile, name, source chip (GitHub with branch, or Local folder), "Azure Free Trial · rg-orders-dev · eastus2", "Last deployed with Aspire 13.6.1" or "Never deployed", actions Deploy (primary), Edit, and an overflow menu with red Delete.
- Empty state: illustration of a stack of layers with a plus, "No profiles yet", "Create one by scanning an AppHost.", "New profile" button.
- Delete dialog: "Delete profile orders-dev?", "This edits only the local settings file. Nothing in Azure is touched; the resource group stays.", checkbox "Also delete its work folder (source copy, logs, history)".

### 4. New profile (the main wizard)
A full-page wizard with a **4-step stepper: Source → Scan → Destination → Parameters**, and a sticky footer bar holding Back / the next primary action. Show each step as its own frame:

- **Step 1 · Source**: "Profile name" (placeholder `orders-dev`, hint "needed to build, not to scan", rule "2 to 40 lower-case letters, digits or dashes"); segmented "Local folder | GitHub repository"; Local: "Solution folder" with Browse…; GitHub: "Repository (owner/repo)" + "Branch, tag or SHA"; optional "AppHost path" ("found automatically"); checkbox "Overwrite an existing profile with this name". Primary: **Scan** with a magnifier icon, and a helper line "Scanning reads files only. Nothing is built or run."
- **Step 2 · Scan result**: an **AppHost summary card** (layers icon; pill "Static scan"): Path, Kind (C# project / File-based C# / TypeScript), Aspire SDK 13.6.1, global.json SDK, Source "ADefWebserver/Blazor-Azure-Inventory@main (5d3aed3)", Declared targets as chips. Warnings as amber callouts. A **trust panel** for GitHub: shield icon, "Building runs the AppHost from ADefWebserver/Blazor-Azure-Inventory at commit 5d3aed3 on this machine.", primary **"Trust and build 5d3aed3"** (disabled with "Enter a profile name to build." until a name is typed). For a local folder the button is just **Build**. Refused variant: red callout "The AppHost declares no Azure compute environment. Add builder.AddAzureContainerAppEnvironment("env") …" and no build button. Building state: summary stays visible, run banner with Cancel, console below.
- After the build: a collapsible **"AppHost details"** card ("3 parameters · 6 resources") with a Parameters table (Name, Secret lock chip, Default, Generated, Configuration key) and a Resources table (resource-type icon + name, type badge, image or project, Azure, Needs container runtime), and a "Copy JSON" button.
- **Step 3 · Destination**: a large segmented choice at the top, **New deployment | Update an existing deployment**, each with an icon and a one-line description.
  - *New deployment*: Subscription (searchable select, "· az default" marker, "Type an ID instead" link, Refresh), Tenant (read-only, from the subscription), Region (grouped by geography), Resource group with "Use existing | Create new" and a suggested name `rg-orders-dev`; then three **resource cards**, Container registry, Container Apps environment and ACR pull identity, each with the 3-mode segmented control (Let Aspire create / Use existing / Create new), a picker or name field, and a "locked" variant with a lock icon and reason ("The AppHost already chooses its own environment."). Below: Credential source, Environment (Production/Staging/Development), Container runtime.
  - *Not signed in variant*: under Subscription, an amber line "The Azure CLI is not signed in." with **Retry** and a **Sign in to Azure** button (log-in icon); "Options" reveals "Tenant (optional)" with the hint "Microsoft Entra ID → Overview → Tenant ID" and a "Use a device code" checkbox. Signing-in state: spinner, "Waiting for the sign-in to finish in the browser…", Cancel, a compact console; device-code variant shows the code line prominently in a mono box with Copy. Success: "Signed in as webmaster@contoso.com." Canceled: "Sign-in was canceled."
  - *Update an existing deployment*: a **finder card**: "Find the existing deployment", "Looks for container apps named azure-mcp, azinventory-web in the subscription below.", **Find** button, then a results table: Resource group, Region, Environment, Apps found ("2 of 2", missing names in a tooltip), Origin chip (aspire deploy / azd / unknown), Warnings (amber text), **Use this**. Show an applied state where the row is highlighted with a check and the form below is filled in (resource group `rg-orders-dev`, creation switch off: "Create the resource group again if it no longer exists"). Empty state: "No container apps named … were found in this subscription. Choose another subscription, or pick the resource group by hand below."
  - At the bottom of the step, the **Container apps preview** table: Resource, Container app, Action pill (**New** green, **Updates existing** amber, **Conflict** red with the reason), "Check again".
- **Step 4 · Parameters**: one row per parameter: monospace name, caption with config key and "secret", masked input for secrets, checkbox "Leave empty (the AppHost's own configuration supplies it)". In update mode, a separate group **"Values from the existing deployment"** with an amber warning ("Leave empty and Aspire generates a new value, which can break a database that already uses the old one.") and optional secret fields. A muted line lists parameters not asked. Primary: **Save profile**; success state: a small celebratory check illustration, "Profile orders-dev saved.", buttons "Deploy it" and "Review it".

### 5. Profile (edit)
- Header "Profile orders-dev", actions "Deploy…" (primary), "Save", "Back". Locked banner when a deploy runs.
- A sticky **in-page section nav** (Source · Destination · Azure resources · Parameters) on the left of the content column (not a global sidebar), with cards on the right: Source (kind, location, ref, AppHost path, GitHub token secret field), Destination (as in the wizard, including the sign-in button state), Azure resources (the three resource cards with a help link "Read the guide: 2. Choose Azure resources"), Parameters (editable table: name, value or secret field, secret toggle, config key override, remove; "Add parameter" row). Footer meta "Last deployed with Aspire 13.6.1".

### 6. Deploy (phased run)
- "Flight check" card styled like a boarding pass: source with branch and commit, subscription, resource group + region, environment, container apps list, last deployed version.
- Options: "Ref for this run" (GitHub), switches "Dry run (list the steps only)" and "Clear Aspire's deploy cache", big primary **Deploy** (or "List steps").
- Confirmation dialog: "Deploy to Azure?", definition list Tenant / Subscription / Resource group "rg-orders-dev in eastus2" / Container apps "azure-mcp (update), azinventory-web (new)" / Environment; Cancel + Deploy. Also show the trust dialog: shield icon, "Run code from GitHub?", "This builds and runs code from owner/repo at 36c89cf… on this machine.", "Trust and continue".
- While running: a **phase stepper** across the top, **Prepare → Build images → Push to registry → Provision Azure → Deploy apps → Done**, with per-phase elapsed time, the current phase animated, then the run banner and a tall console.
- Success: green banner plus an **endpoints card** (globe icon, clickable URLs, "No public endpoints" for internal apps, link to the Aspire dashboard and the Azure portal resource group), and a note "To remove everything, delete resource group rg-orders-dev in Azure." Failure: red banner with AI Help and the failing phase marked red in the stepper.

### 7. Settings
- Cards with icon tiles: **Locations** (settings file, work root, tools root as mono paths with "Open folder"), **GitHub token** (explanation, "(set)/(not set)", Replace/Clear), **AI assistant**.
- **AI assistant** is a small **3-step setup flow** the first time: 1 Choose provider (logo tiles: OpenAI, Azure OpenAI, Azure AI Foundry, Anthropic, Google AI) → 2 Connect (API key secret field, endpoint, API version, deployment path or protocol as the provider needs) with "Test connection" → 3 Pick a model (searchable list) and "Make active". Once set up, show a compact summary row per provider with an "Active" badge and Edit.

### 8. Guide
- A documentation page with a sticky table of contents on the left of the content column: Before you start · How it works · 1. Create a profile · Update an existing deployment · 2. Choose Azure resources · AppHost kinds · 3. Deploy and watch · 4. Deploy again · 5. Remove a deployment · Troubleshooting.
- Clean diagrams with icons: "How it works" (your machine → az / aspire deploy → resource group with registry, environment, container apps), and a "Resource modes" diagram. A troubleshooting table with message and fix.

### 9. States to include
- Not found page with a small illustration and "Go home".
- In-page error card: "This page hit an unexpected error (result code 1): … Running work is not affected." with "Try again".
- Skeleton loaders for lists and pickers while az is being read.

Use realistic sample data throughout: profiles `orders-dev`, `storefront-prod`, `azinv-test`; repository `ADefWebserver/Blazor-Azure-Inventory`, commit `5d3aed3`; region eastus2; Aspire 13.6.1; container apps `azure-mcp` and `azinventory-web`; environment `acaenvzd3n7pq724i34`; subscription "Azure Free Trial".
