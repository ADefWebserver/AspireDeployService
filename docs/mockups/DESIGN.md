---
name: Aspire Precision
colors:
  surface: '#131315'
  surface-dim: '#131315'
  surface-bright: '#39393b'
  surface-container-lowest: '#0e0e10'
  surface-container-low: '#1c1b1d'
  surface-container: '#201f22'
  surface-container-high: '#2a2a2c'
  surface-container-highest: '#353437'
  on-surface: '#e5e1e4'
  on-surface-variant: '#c9c4d8'
  inverse-surface: '#e5e1e4'
  inverse-on-surface: '#313032'
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
  background: '#131315'
  on-background: '#e5e1e4'
  surface-variant: '#353437'
typography:
  headline-xl:
    fontFamily: Inter
    fontSize: 24px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Inter
    fontSize: 18px
    fontWeight: '600'
    lineHeight: 26px
    letterSpacing: -0.015em
  headline-sm:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '600'
    lineHeight: 20px
    letterSpacing: -0.01em
  body-lg:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
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
    fontWeight: '400'
    lineHeight: 18px
  code-md:
    fontFamily: JetBrains Mono
    fontSize: 12px
    fontWeight: '400'
    lineHeight: 16px
  code-sm:
    fontFamily: JetBrains Mono
    fontSize: 11px
    fontWeight: '500'
    lineHeight: 14px
    letterSpacing: 0.02em
  label-md:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '500'
    lineHeight: 16px
  label-sm:
    fontFamily: JetBrains Mono
    fontSize: 10px
    fontWeight: '600'
    lineHeight: 12px
    letterSpacing: 0.05em
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  gutter: 1rem
  margin: 1rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 0.75rem
  space-lg: 1rem
  space-xl: 1.5rem
---

## Brand & Style

This design system establishes a focused, high-density desktop environment tailored for cloud engineers, infrastructure architects, and full-stack .NET developers. Rooted in the engineering precision of Linear, Vercel, and GitHub Desktop, the aesthetic pairs industrial utility with modern finish.

### Core Tenets
- **Calm Authority**: The interface steps back to let deployment graphs, terminal logs, and resource states lead. Visual noise is aggressively suppressed; decorative chrome is eliminated.
- **Engineered Density**: Space is calibrated for high information throughput without feeling cramped. Metrics, micro-statuses, and execution trees maintain razor-sharp alignment.
- **Definitive Feedback**: System states (provisioning, healthy, degraded, failed) are unambiguous, communicating runtime health instantaneously through crisp semantic tokens and dedicated monospace accents.

### Design Style
The system employs a **Modern Technical Minimalist** style: flat, layered matte surfaces, hairline 1px structural dividers, monochromatic base neutrals, and measured hits of deep Aspire purple and Azure blue. Depth is achieved via tonal surface stepping and low-contrast borders rather than heavy skeuomorphism or dramatic drop shadows.

## Colors

The palette is tuned for continuous production monitoring across long work sessions. While the system natively defaults to dark mode, it maintains strict token parity in light mode.

### Palette Architecture
- **Primary Brand (`#512BD4`)**: Deep Aspire Purple represents foundational orchestration, selected navigation states, primary action anchors, and active pipeline runs.
- **Secondary Accent (`#0078D4`)**: Azure Blue provides clarity for cloud links, resource mapping indicators, focus rings, and external service bindings.
- **Canvas & Neutral Surfaces**: 
  - **Dark Mode**: Layered charcoal hierarchy running from `#09090B` (Window canvas) to `#121215` (Sidebar / Panels) and `#18181B` (Cards / Active work surfaces).
  - **Light Mode**: Crisp, clinical slate palette starting at `#F8FAFC` (App base), `#FFFFFF` (Surface elevated), and `#F1F5F9` (Muted wells).
- **Hairline Dividers**: Structural separation relies on `#27272A` in dark mode and `#E2E8F0` in light mode.

### Status Indicators
Semantic tokens follow uncompromising DevOps clarity:
- **Pass / Ready**: `#10B981` (Emerald)
- **Running / Syncing**: `#3B82F6` (Cobalt)
- **Warning / Degraded**: `#F59E0B` (Amber)
- **Error / Terminated**: `#EF4444` (Crimson)

## Typography

The type system prioritizes micro-legibility and optical alignment. The pairing combines the balanced geometric neutrality of **Inter** with the structural legibility of **JetBrains Mono**.

### Rules & Hierarchy
- **Primary Body & Interface (Inter)**: Handles desktop navigation, modal dialogue, action controls, and meta headers. Weights stay strictly between Regular (400), Medium (500), and Semi-Bold (600).
- **Monospace Elements (JetBrains Mono)**: Applied to CLI output, container IDs, endpoints, deployment revisions, GUIDs, and telemetry matrices. Its increased x-height and clear distinction between easily confused glyphs (0 vs O, 1 vs l) guarantees fast scanning in high-pressure troubleshooting scenarios.
- **Numbers & Metrics**: Tabular figures (`tnum`) are mandatory across tables, progress indicators, uptime counters, and port numbers.

## Layout & Spacing

The layout is built around a standard desktop IDE shell model: persistent compact rail navigation, collateral tree view / project inspector, fluid main orchestrator stage, and collapsible bottom drawer for logs and terminal execution.

### Grid & Composition
- **Structural Model**: Multi-pane docking interface with fluid horizontal distribution. Panes snap along an absolute 8px baseline grid (with a 4px sub-grid for dense UI controls).
- **Collapsible Panes**: Sidebars resize between a collapsed 48px icon strip, a default 240px tree view, and an upper limit of 400px.
- **Density Zones**:
  - *High Density*: Log viewers, terminal outputs, and environment variable key-value grids utilize `space-xs` (4px) to `space-sm` (8px).
  - *Comfort Density*: Settings dialogs, deploy confirmation prompts, and empty-state dashboards scale to `space-md` (12px) and `space-lg` (16px).
- **Desktop Breakpoints**: Optimized primarily for screens between 1280px and 2560px width. Below 1024px, the ancillary inspector pane auto-docks behind tabs.

## Elevation & Depth

This design system avoids high-blur drop shadows and simulated light sources. Depth is achieved via **Tonal Stepping** combined with **Low-Contrast Hairlines**.

### Elevation Stack (Dark Mode)
- **Base Canvas (Level 0 / `#09090B`)**: Underneath all split panels and window margins.
- **Structural Sidebar / Drawer (Level 1 / `#121215`)**: Delimited strictly by a 1px solid `#27272A` border on the inner dividing axis.
- **Card Containers / Tables (Level 2 / `#18181B`)**: Bounded by 1px solid `#27272A`. Rests seamlessly atop Level 0 and Level 1.
- **Popovers, Dropdowns & Command Palette (Level 3 / `#1F1F23`)**: Floats above content with a 1px `#3F3F46` border and a directional shadow: `0 8px 24px -4px rgba(0, 0, 0, 0.45)`.
- **Active Focus Rings**: 2px solid `#0078D4` with a 2px offset (`#09090B`), ensuring immediate accessibility compliance without visually disturbing surrounding layout nodes.

## Shapes

The design system standardizes on a **10px to 12px corner radius** hierarchy, delivering an approachable yet engineered profile that avoids both harsh sharp rectangles and playful pill treatments.

### Corner Radii Guidelines
- **Primary Shell Surfaces (Cards, Modals, Terminal Panels)**: `12px` (`rounded-lg`) creates containment for nested interactive elements.
- **Controls & Buttons**: `8px` (`rounded`) maintains comfortable touch targets and aligns with text baselines.
- **Micro-Chips, Badges & Small Inputs**: `6px` (`rounded-sm`) to preserve internal visual density.
- **Pill Exceptions**: Circle/pill radii are strictly reserved for avatar initials, live activity pulse indicators (e.g., streaming server heartbeat), and floating toast alerts.

## Components

### Buttons
- **Primary Action (Deploy / Publish)**: Background `#512BD4`, text `#FFFFFF`, border `1px solid rgba(255, 255, 255, 0.1)`. Hover shifts to `#5D33E3`. Pressed shifts to `#4322B3`.
- **Secondary (Inspect / Abort)**: Background transparent, border `1px solid #27272A`, text `#E4E4E7`. Hover: `#18181B`.
- **Destructive**: Background `#EF4444` with white text, or low-profile transparent surface with `#EF4444` text and red-tinted hover state.
- **Dimensions**: Small (28px height, 11px font), Default (34px height, 13px font).

### Status Badges & Chips
- Status badges feature monospace typography (`code-sm`), `uppercase` styling, and an inline SVG dot status glyph.
- **Pass**: Background `rgba(16, 185, 129, 0.12)`, text `#10B981`, border `1px solid rgba(16, 185, 129, 0.25)`.
- **Running**: Background `rgba(59, 130, 246, 0.12)`, text `#3B82F6`, border `1px solid rgba(59, 130, 246, 0.25)`.
- **Warning**: Background `rgba(245, 158, 11, 0.12)`, text `#F59E0B`, border `1px solid rgba(245, 158, 11, 0.25)`.
- **Error**: Background `rgba(239, 68, 68, 0.12)`, text `#EF4444`, border `1px solid rgba(239, 68, 68, 0.25)`.

### Cards & Resource Items
- Uniform 12px outer radius, background `#18181B`, border `1px solid #27272A`.
- Header houses service type badge, resource moniker, and instant status pill on the right boundary.
- Interactive cards apply a hover state transition: border color transitions smoothly to `#3F3F46`.

### Form Fields & Inputs
- Height 34px, background `#121215`, border `1px solid #27272A`, font `Inter` 13px.
- Value paths, variables, and arguments automatically switch font family to `JetBrains Mono`.
- Focused state: border `#0078D4`, box-shadow `0 0 0 1px #0078D4`.

### Resource Lists & Tree Views
- Alternating row zebra-striping is forbidden. Rows rely on clear row lines (`1px solid #1C1C20`) or a 2px hover background change (`#18181B`).
- Selected item shows a solid left-edge marker (2px width, color `#512BD4`) with a tinted background (`rgba(81, 43, 212, 0.08)`).

### Command Palette & Terminal Windows
- **Palette**: Centered modal overlay at 640px width with blurred backdrop (`backdrop-filter: blur(12px)`). High-speed keyboard navigable.
- **Log Stream**: Integrated console drawer using background `#0C0C0E`, border-top `1px solid #27272A`, and full JetBrains Mono stack with color-coded log levels (INF, WRN, ERR, VRB).