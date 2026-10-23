---
name: Aspire Deploy
colors:
  surface: '#111319'
  surface-dim: '#111319'
  surface-bright: '#373940'
  surface-container-lowest: '#0c0e14'
  surface-container-low: '#191b22'
  surface-container: '#1e1f26'
  surface-container-high: '#282a30'
  surface-container-highest: '#33343b'
  on-surface: '#e2e2eb'
  on-surface-variant: '#c9c4d8'
  inverse-surface: '#e2e2eb'
  inverse-on-surface: '#2e3037'
  outline: '#938ea1'
  outline-variant: '#484555'
  surface-tint: '#c9beff'
  primary: '#c9beff'
  on-primary: '#30009b'
  primary-container: '#512bd4'
  on-primary-container: '#c7bcff'
  inverse-primary: '#5f3de2'
  secondary: '#a3c9ff'
  on-secondary: '#00315c'
  secondary-container: '#3b93f1'
  on-secondary-container: '#002a51'
  tertiary: '#4edea3'
  on-tertiary: '#003824'
  tertiary-container: '#005d3f'
  on-tertiary-container: '#4bdca0'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#e6deff'
  primary-fixed-dim: '#c9beff'
  on-primary-fixed: '#1b0063'
  on-primary-fixed-variant: '#4618ca'
  secondary-fixed: '#d3e3ff'
  secondary-fixed-dim: '#a3c9ff'
  on-secondary-fixed: '#001c39'
  on-secondary-fixed-variant: '#004883'
  tertiary-fixed: '#6ffbbe'
  tertiary-fixed-dim: '#4edea3'
  on-tertiary-fixed: '#002113'
  on-tertiary-fixed-variant: '#005236'
  background: '#111319'
  on-background: '#e2e2eb'
  surface-variant: '#33343b'
typography:
  headline-lg:
    fontFamily: Inter
    fontSize: 32px
    fontWeight: '600'
    lineHeight: 40px
    letterSpacing: -0.02em
  headline-lg-mobile:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.015em
  headline-md:
    fontFamily: Inter
    fontSize: 20px
    fontWeight: '600'
    lineHeight: 28px
    letterSpacing: -0.01em
  headline-sm:
    fontFamily: Inter
    fontSize: 16px
    fontWeight: '600'
    lineHeight: 24px
    letterSpacing: -0.005em
  body-lg:
    fontFamily: Inter
    fontSize: 15px
    fontWeight: '400'
    lineHeight: 22px
  body-md:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 18px
  body-sm:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 16px
  code-lg:
    fontFamily: JetBrains Mono
    fontSize: 13px
    fontWeight: '500'
    lineHeight: 20px
  code-md:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 16px
  code-sm:
    fontFamily: JetBrains Mono
    fontSize: 11px
    fontWeight: '400'
    lineHeight: 14px
  label-caps:
    fontFamily: Inter
    fontSize: 11px
    fontWeight: '600'
    lineHeight: 14px
    letterSpacing: 0.04em
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  gutter: 1rem
  gutter-dense: 0.5rem
  margin: 1.5rem
  margin-mobile: 1rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 0.75rem
  space-lg: 1.25rem
  space-xl: 2rem
---

## Brand & Style

This design system targets modern cloud engineers, DevOps practitioners, and .NET ecosystem developers who demand extreme density, sub-second visual parsing, and the refined precision seen in tools like Linear, Raycast, and Vercel. 

The aesthetic is technical minimalism layered with engineering precision. It emphasizes:
- Crisp 1px geometric boundaries over blurry atmospheric effects.
- Deep, purposeful brand accents (.NET Aspire violet paired with Azure infrastructure blue) applied sparingly against neutral chassis backdrops.
- Purpose-built density: visual separation is achieved through explicit containment lines, muted background tints, and tight typographic hierarchies.
- Utility-first clarity: critical states (deployment progress, resource orchestrations, distributed trace spans) communicate status instantly without decorative friction.

## Colors

The palette is engineered around high functional contrast, maintaining strict compliance with WCAG AAA standards for actionable text and AA (4.5:1+) across subtle data points.

### Role Tokens & States
- **Primary Brand Accent (`#512BD4`)**: Signifies orchestrator root actions, Aspire service host anchors, and high-level project contexts.
- **Secondary Accent (`#0078D4`)**: Reserved for cloud compute targets, Azure resource bindings, and operational links.
- **Surface Hierarchy (Dark Theme)**:
  - Base Canvas: `#0B0D13`
  - Elevated Container / Sidebar: `#11141D`
  - Card & Panel Fill: `#171B26`
  - Subtle Hover Surface: `#1F2433`
  - Structural Border Default: `#262B3D`
  - Structural Border Focus / Active: `#512BD4`
- **Surface Hierarchy (Light Theme)**:
  - Base Canvas: `#F8FAFC`
  - Elevated Container: `#FFFFFF`
  - Subtle Hover Surface: `#F1F5F9`
  - Structural Border Default: `#E2E8F0`
- **Status & Telemetry Tokens**:
  - `Running / Active`: `#0078D4` (Background tint: `rgba(0, 120, 212, 0.12)`)
  - `Pass / Deployed / New`: `#10B981` (Background tint: `rgba(16, 185, 129, 0.12)`)
  - `Warning / Rebuilding / Drift`: `#F59E0B` (Background tint: `rgba(245, 158, 11, 0.12)`)
  - `Failed / Terminated / Conflict`: `#EF4444` (Background tint: `rgba(239, 68, 68, 0.12)`)

## Typography

Typography balances rapid UI navigation with data-dense telemetry scanning. 
- **Inter** handles all structural navigational framing, page headlines, descriptions, button labels, and status badges. Tabular numerals (`tnum`) must be turned on by default for numeric layouts.
- **JetBrains Mono** is enforced strictly for commit hashes, resource endpoints, CLI log outputs, environment variables, trace identifiers, and time stamps.
- Maintain consistent vertical alignment between code pills and standard UI labels using inline-flex centering with matched optical line-heights.

## Layout & Spacing

This design system uses a fluid, split-pane layout model anchored by a structural command bar and dynamic telemetry drawer:
- **Left Navigation Rail**: Fixed width at 240px (collapsible to 56px icon mode on narrow displays).
- **Primary Grid**: 12-column fluid grid system across views containing deployments, dashboard metrics, and node graphs.
- **Panels & Drawers**: Collapsible right-hand inspector panel (360px default) for log stream inspectors, raw resource manifests, and environment overrides.
- **Breakpoints**:
  - `Desktop Wide` (≥1440px): 3-pane view enabled (Nav, Main Graph/List, Terminal Inspector).
  - `Desktop Standard` (1024px–1439px): Nav + Main workspace, Inspector transitions to an overlay pane.
  - `Tablet` (768px–1023px): Nav collapses to icon rail, inspector collapses to bottom drawer.
  - `Mobile` (<768px): Single pane layout with bottom navigation bar; tabular metrics switch to card-based stacks.

## Elevation & Depth

Visual hierarchy is communicated via clean structural boundaries and layered surface tones rather than heavy atmospheric drops.

- **Crisp Borders**: Surfaces are partitioned by sharp `1px` continuous borders (`#262B3D` in dark mode, `#E2E8F0` in light mode).
- **Tonal Tiers**:
  - Level 0 (Canvas): Background workspace.
  - Level 1 (Panels & Cards): Surface `#171B26` with 1px border.
  - Level 2 (Flyouts, Popovers, Command Palettes): Surface `#1A1F2C` bordered with `#363C52` and accompanied by a low-spread focus shadow (`0 8px 24px rgba(0, 0, 0, 0.45)`).
- **Interactive Depth**: Avoid physical extruded effects or skeuomorphic buttons. On hover, interactive controls shift background luminance by +4% and border opacity to 100%.

## Shapes

The shape system strictly balances compact developer controls with structural cards:
- **Cards & Data Panels**: Fixed `12px` corner radius. This softens technical layouts without wasting spatial footprint.
- **Controls & Form Elements**: Fixed `8px` corner radius across all buttons, text fields, search selectors, dropdowns, and segmented toggles.
- **Icon Tiles**: Square tiles with an inner `6px` radius inside `8px` containers.
- **Status Pills & Inline Tags**: `4px` or fully rounded pill styles strictly when representing transient status indicators or runtime states.

## Components

### Buttons
- **Primary**: Solid background `#512BD4`, text `#FFFFFF`, border `1px solid rgba(255, 255, 255, 0.12)`. On hover: `#5D33EC`. Height: 32px (standard density), 28px (compact). Radius: 8px.
- **Secondary**: Surface `#171B26`, text `#E2E8F0`, border `1px solid #262B3D`. On hover: surface `#1F2433`.
- **Ghost / Tool Button**: Background transparent, border none. Hover state: surface `#1F2433`.

### Cards & Resource Blocks
- Contained with a background of `#171B26`, border of `1px solid #262B3D`, and a corner radius of `12px`.
- Card headers feature an integrated top-bar with border bottom `1px solid #262B3D`, housing the service icon tile, health indicator, and external Azure endpoint links.

### Tinted Icon Tiles
- Standardized dimensions: 28x28px or 32x32px with 6px–8px radius.
- Background uses status or brand colors at 12% alpha transparency.
- The icon foreground matches the 100% token color (e.g., Aspire host icons sit on `#512BD4` at 15% opacity with `#A78BFA` icon lines).

### Form Inputs & Search Command Bars
- Background `#11141D`, border `1px solid #262B3D`, text `13px Inter`.
- Focus state: `1px solid #512BD4`, accompanied by an outer halo of `0 0 0 1px #512BD4`.
- Integrated keyboard shortcut hint rendered via `JetBrains Mono` 11px inside a `4px` rounded badge (`#1F2433`).

### Telemetry Logs & Status Badges
- **Status Badges**: Constructed as inline-flex items with 4px border radius, 11px uppercase Inter or JetBrains Mono, padded `2px 6px`. A pulsing 6px circle denotes active runtime services.
- **Log Stream Viewer**: Full-bleed background `#0B0D13` with `JetBrains Mono` 12px text, line numbers styled in `#4B5563`, and error/warning blocks rendered with a 2px left border strip matching the status token.

### Checkboxes & Segmented Controls
- Checkboxes: 16x16px boxes, 4px corner radius, `#262B3D` border. Checked fill: `#512BD4`.
- Segmented Group: Outer wrapper `#11141D` with 1px border, containing active tabs highlighted in `#1F2433` with an 8px radius.