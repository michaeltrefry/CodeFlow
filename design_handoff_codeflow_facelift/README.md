# Handoff: CodeFlow UI facelift

## Overview

A visual facelift for **CodeFlow**, the internal web app for operating a multi-agent LLM orchestration platform. The goal is to elevate the aesthetic from "functional dashboard" to "polished modern dev-tool" — while preserving the existing information density, route structure, and muscle memory of the current Angular app.

**Target codebase:** `github.com/michaeltrefry/CodeFlow` — Angular 20, standalone components, rete 2.x for the workflow canvas, MassTransit + RabbitMQ on the backend.

## About the design files

The files in this bundle are **design references built in HTML + React/JSX** — high-fidelity prototypes showing the intended look, layout, spacing, and interactions. They are **not production code to lift directly**.

The task is to recreate these designs in the existing Angular codebase, using the project's current patterns (standalone components, `ApiService`, `HttpParamsBuilder`, router-outlet routing, existing SCSS file-per-component). The CSS token system (`:root` variables, `data-theme` / `data-accent` / `data-font` attributes on `<html>`) should port wholesale into `src/app/styles/` as a new `tokens.scss` that replaces the current slate/sky palette.

## Fidelity

**High-fidelity.** Colors, typography, spacing, radii, shadows, and all motion are final. Recreate pixel-perfectly against the token values in `app.css`.

## What's included

- `CodeFlow.html` — entry point; wires React + Babel + all page scripts.
- `app.css` — the entire design system (themes, tokens, components). This is the source of truth for visual values.
- `shell.jsx` — left nav + topbar.
- `primitives.jsx` — `Button`, `Chip`, `StateChip`, `Card`, `Segmented`, `Tabs`, `Field`, `Empty`, `PageHeader`, icon set.
- `pages-traces.jsx` — traces list (landing page) + trace detail.
- `pages-workflows.jsx` — workflows list + the node canvas.
- `pages-agents.jsx` — agents grid + tabbed agent editor.
- `pages-hitl-dlq.jsx` — HITL queue + DLQ ops.
- `pages-settings.jsx` — MCP servers, Roles, Skills, Git host.
- `pages-inventory.jsx` — component inventory / style reference.
- `data.jsx` — realistic mock data matching the real domain models.
- `app.jsx` — root composition + Tweaks wiring.

## Screens / Views

### 1. Shell (every page)

- **Left nav** — 232px wide (56px collapsed). Brand mark (accent→purple gradient square with inset diamond) + wordmark. Section groups, nav links with icon + label + optional count badge. 3px accent rail on the active item (`::before`). Footer holds a Collapse toggle and the user block (avatar, name, role chips). `[data-nav="collapsed"]` on the shell root hides all labels.
- **Topbar** — sticky, 10px 24px padding, translucent blur (`color-mix bg + blur(8px)`), hairline bottom border. Breadcrumb left, 320px `⌘K` search right of that, bell + tweaks icon buttons.

### 2. Traces list (landing at `/traces`)

- PageHeader (h1 + subtitle + actions).
- **Bulk-cleanup panel** — full-width card, left: label + muted description, right: state select + "older than" select + destructive button.
- List toolbar — Segmented (All / Running / Terminal) + "hide subflow children" checkbox; right-aligned row counter.
- Dense table, sticky headers, row hover reveals trash/terminate action. State shown via `<StateChip>` (dot + label).

### 3. Trace detail (`/traces/:id`)

- Back button, h1 "Trace", mono trace ID, meta chip row.
- Failure banner (red tint) when `currentState === 'Failed'`.
- 2-column grid (1fr 360px):
  - Left: **Execution timeline** — stepper with colored dots per state (`ok/err/warn/run/hitl`). Pulsing dot for `run`. Vertical connector line between steps. Each step shows agent key (mono), version chip, port chip, decision label, and an optional monospace payload block.
  - Right stack: Decision output card, Context inputs card, Pinned agent versions card.

### 4. Workflows list + canvas

**List:** key / name / version / nodes / edges / runs / last-edit. Click row → canvas.

**Canvas (flagship):**
- `.wf-surface` — 1fr 320px grid (canvas + inspector), height `calc(100vh - 165px)`.
- `.wf-canvas` — dot-grid background via `radial-gradient`, 22px cell size. `.wf-canvas-inner` is absolutely positioned and scaled via `transform: scale(zoom)`.
- **Toolbar** top — floating translucent button groups (Select / Pan / Connect, Add node, Script / Auto-layout).
- **Zoom chip** bottom-left. **Minimap** bottom-right (180×110, showing nodes as colored rects + a viewport box).
- **Nodes** (`.wf-node`) — 220px wide, 4px colored left border by kind (start=green, agent=blue, logic=amber, hitl=purple, escalation=red, subflow/reviewloop=cyan). Header has uppercase kind chip + title + `{ }` script badge when `hasScript`. Two-column port grid in body. Footer shows last-run state dot + duration. Selected state adds 2px accent ring. "Active" state (currently executing) adds blue ring.
- **Ports** — 12px circles, stroke in kind color, filled when connected. Labels mono 11px.
- **Edges** — SVG bezier. Default stroke `--border-2`. Active edges animate a dashed stroke via `stroke-dashoffset`. Port name label centered on edge.
- **Inspector** (right) — kind-specific fields (Agent key/version/pin; Node label + "Edit script" button; Subflow key + max rounds + loop decision). Output-port editor below. "Last run" KV block at the bottom.
- **Code drawer** — slides up from bottom, covers canvas except the inspector column. Monaco-style (`.code-editor`): 36px line-number gutter, tokenized code. Open/close via the `{ } Script` toolbar toggle.

### 5. Agents grid + editor

**Grid:** `auto-fill minmax(300px, 1fr)` cards. Key (mono, bold), display name (muted), kind icon top-right, tags row (version accent chip, provider+model or `hitl` chip), footer with relative stamp + author handle.

**Editor:** Tabs — Identity / Prompt & output / Model / Skills / Output ports / Versions. Form sections use 2-col grid (`field.span-2` for full-width). Code fields (`.code-field`) have a mono header bar and a raw `<textarea>` body on `--bg`. Versions tab is a table with diff buttons.

### 6. HITL queue

- 1fr 460px grid.
- Left: list of `.hitl-card` (32px purple icon · body · action column). Body has title row (#id, agent, version, round chips), meta row (trace slug, queued ago), fade-masked preview block. Selected card gets accent border + 3px accent-weak halo.
- Right: detail card with decision segmented control (Approve / Request changes / Reject) + comment textarea + submit.

### 7. DLQ ops

- 4-up stat cards at top — queue name + message count. Selected queue card has accent border.
- 1fr 1.2fr grid: left is a faulted-message table (message ID slice, fault exception chip, age); right is a Fault card (summary + metadata KV rows) + Payload preview + action row (Discard / Retry).

### 8. Settings

- **MCP servers** — table: key / name / transport / endpoint / auth / health chip / last-verified / Verify button. Error banner below for unhealthy endpoints.
- **Roles** — table: key / display / grant count / skill count / description / Edit.
- **Skills** — 320px list on left with accent-bar selected item, editor on right (markdown code-field).
- **Git host** — two cards: provider connection (2-col form grid with repo chip cloud) + webhook (url, event list, rotate secret).

### 9. Component inventory

Reference page used to verify visual consistency. Sections: Buttons / Chips & status / Form inputs / Segmented + Tabs / Toasts / Empty state / Color semantic / Color surfaces / Type scale / Keyboard affordances / Accessibility notes.

## Interactions & behavior

- **Navigation** — single-page; left nav `setActive` clears all nested state (selected trace, workflow, agent). Match to Angular router: `/traces`, `/workflows`, `/workflows/:key/:version`, `/agents`, `/agents/:key/edit`, `/hitl`, `/ops/dlq`, `/settings/mcp`, etc.
- **Trace row click** → detail view. Row hover reveals a Terminate (Running) or Trash (terminal) button.
- **Workflow canvas** — node click selects (blue accent ring); ports hover-scale; active edges animate (`stroke-dashoffset` 1.1s linear infinite); `{ } Script` toolbar toggle opens/closes the code drawer.
- **Tweaks panel** — toggled from the topbar gear or via the toolbar activation message. Theme / accent / font / nav-collapsed. Persists via the editmode protocol; in the Angular port, persist to `localStorage`.
- **HITL approve/reject** — optimistic; the card should animate out and decrement the queue counter in the nav.
- **DLQ retry** — optimistic; remove row, decrement queue count; show a toast on failure.
- **Motion** — `--transition: 140ms cubic-bezier(.3,.6,.3,1)` for all state changes. Running dots pulse at 1.6s ease-in-out infinite. Active edges flow at 1.1s linear.

## State management

Mirror existing patterns (`BehaviorSubject`-backed services, signal-based state where already used).

- `ThemeService` — theme/accent/font/nav-collapsed, backed by `localStorage`, applied by setting `data-*` attrs on `document.documentElement`.
- Traces and HITL queue should live-poll (existing app already does this via signals / SSE where relevant); the Running state-chip pulse depends on `currentState` updates arriving.
- Workflow canvas — continue with rete 2.x; replace the current node component rendering with the new `.wf-node` markup + kind colors and footer.

## Design tokens

All tokens are in `app.css` under `:root` and `[data-theme="…"]`. Port them to `src/app/styles/tokens.scss`.

### Themes (swapped via `[data-theme]` on `<html>`)

**Dark**
```
--bg:        #0B0C0E
--surface:   #131519
--surface-2: #1A1D22
--surface-3: #22262C
--border:    #23272E
--border-2:  #2E333B
--hairline:  #1D2026
--text:      #E7E9EE
--text-2:    #B8BDC8
--muted:     #8B92A1
--faint:     #5A6070
```

**Light**
```
--bg:        #FAFAF7
--surface:   #FFFFFF
--surface-2: #F4F4EE
--surface-3: #EDECE5
--border:    #E5E4DC
--border-2:  #D6D4C8
--hairline:  #EDECE5
--text:      #15171C
--text-2:    #3B3F49
--muted:     #6B6F7A
--faint:     #9A9EA8
```

### Semantic (theme-invariant — load-bearing for node kinds + state chips)

```
--sem-green:  #3FB950  (ok, Start node)
--sem-amber:  #D29922  (warn, Logic node, script badge)
--sem-red:    #F85149  (error, Escalation node, destructive)
--sem-blue:   #58A6FF  (running, Agent node)
--sem-purple: #BC8CFF  (HITL)
--sem-cyan:   #2EA3F2  (Subflow, ReviewLoop)
```

### Accent (swapped via `[data-accent]`)

```
indigo: oklch(0.66 0.16 265)
cyan:   oklch(0.72 0.14 210)
green:  oklch(0.72 0.15 150)
amber:  oklch(0.76 0.15 75)
```
Each exposes `--accent`, `--accent-weak` (.14), `--accent-dim` (.28), `--accent-ink` (0.82 / 0.11 at hue).

### Type

```
[data-font="geist"]  Geist + Geist Mono
[data-font="inter"]  Inter + JetBrains Mono
[data-font="plex"]   IBM Plex Sans + IBM Plex Mono   (user-selected default)
```

Scale: `--fs-xs 11, --fs-sm 12, --fs-md 13, --fs-lg 14, --fs-xl 16, --fs-h3 18, --fs-h2 22, --fs-h1 28`.

### Radius

`--radius-sm 4, --radius 6, --radius-md 8, --radius-lg 10, --radius-xl 14`.

### Motion

`--transition: 140ms cubic-bezier(.3,.6,.3,1)`.

### Focus

`--focus-ring: 0 0 0 2px var(--bg), 0 0 0 4px var(--accent)` — applied via `:focus-visible`. Carry this into Angular's global stylesheet.

## Accessibility

- Body text hits AA against both themes; destructive button color is deliberately muted red on red-tinted background.
- **Status is never color-only** — every chip pairs a dot with a label; destructive row actions carry both an icon and a label.
- **Node kinds are encoded twice** — left-border color AND an uppercase text kind chip.
- **Focus** — visible 2-ring halo on all tab-reachable elements. When porting, make sure Angular CDK overlays carry the ring through.
- **Keyboard** — `⌘K` opens search; single-letter shortcuts (J/K for row nav, E to edit, `⇧R` to retry) are documented in the inventory page.

## Recreating in the existing codebase

1. **Tokens first** — copy `:root` + themes from `app.css` into `src/app/styles/tokens.scss`. Keep the existing `rgb()`-style wrappers if any of the component SCSS depends on them; otherwise switch to the tokens directly. Apply `data-theme` / `data-accent` / `data-font` on `<html>` via an `AppComponent` effect.
2. **Primitives** — port the `Button`, `Chip`, `StateChip`, `Card`, `Segmented`, `Tabs`, `PageHeader`, `Field` atoms into standalone components under `src/app/ui/`. The CSS lifts 1:1 — class names (`.btn`, `.chip`, `.card`, `.tabs`, etc.) are deliberately framework-agnostic.
3. **Shell** — replace the existing `AppShellComponent` nav SCSS with `.nav` / `.nav-link` / `.nav-footer`. Keep its current badge-count signals bound to the new `.nav-link-badge`.
4. **Pages** — port screen-by-screen. `pages-*.jsx` each correspond to one or two existing Angular route components; use them as layout + copy references.
5. **Workflow canvas** — the existing rete setup stays; swap the node renderer and edge styling to match `.wf-node` / `.wf-edge` / `.wf-port` rules. The animated "active edge" dashes are pure CSS (`animation: flow 1.1s linear infinite`).
6. **Mock data** — `data.jsx` matches the real shape of `TraceSummary`, `AgentSummary`, `WorkflowNode`, `HitlTask`, `McpServer` etc. Use it for Storybook fixtures if you run Storybook.

## Notes

- The agent cards, HITL cards, DLQ rows all use the same row-hover affordance — action buttons fade in on `tr:hover` via `opacity 0 → 1`. Preserve this; it keeps the tables breathing.
- There's a deliberate **single accent rail** (the 3px bar on the active nav item) that doubles as the brand's visual punctuation — don't add other large blocks of accent color to pages.
- Running/active motion is scoped to state — don't animate static UI.
