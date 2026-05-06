# Assistant Artifacts

## Problem

The homepage assistant can spend significant time and tokens producing
a workflow-package draft (or other tool-emitted file). Today the only
surface that lets the user act on those bytes is the ephemeral
"Save / Resolve" chip attached to the `save_workflow_package` tool
result. If the user dismisses the chip, navigates away, or the
preview returns conflicts the user can't unblock, **the bytes have no
addressable surface**: they live in `WorkflowPackageDraftStore` on
disk but the chat UI shows nothing the user can click to retrieve
them. Reloading the conversation also strips every tool-call card
(only final assistant text persists), so even the save chip is lost.

The user reported losing a substantial drafting investment to this
gap. We need an always-available, reload-survivable surface for
artifacts the assistant produces.

## Requirements

### Functional

1. Whenever a tool produces a downloadable artifact, the chat thread
   must show an inline indicator naming the artifact and offering
   download.
2. The indicator must persist in conversation history — i.e., reload
   re-renders the same indicator without depending on transient
   tool-call cards.
3. The user must be able to download the artifact's current bytes at
   any time, not only at the moment the tool ran.
4. Updating an artifact (e.g., `patch_workflow_package_draft`) must
   produce a new indicator on the new turn; the old one keeps
   pointing at its frozen snapshot bytes (if any) or is marked
   superseded if it referenced live draft state.
5. Deletion (`clear_workflow_package_draft`) must mark prior
   indicators superseded so the user isn't offered a phantom
   download.
6. Auth: only the conversation owner can list or download.
   Cross-conversation reads must 404 — same shape as existing
   `GET /api/workflows/package-draft`.

### Non-functional

- No DB-blob storage. Bytes stay on disk in the per-conversation
  workspace; the new persistence is metadata only.
- No tool-call replay. We are not persisting the full tool transcript
  — only artifact-creation events.
- The download endpoint must stream the file (no full-buffer reads
  for large packages).
- Quota-friendly: rendering the rail and pills must not refetch bytes
  unless the user explicitly previews/downloads.

### Out of scope

- Persisting the full tool-call transcript (separate concern, larger
  schema impact). Artifacts are a narrow slice — only events that
  produce a file the user might want.
- Cross-conversation artifact sharing.
- Mobile-tailored UI.

## Architecture

### Persistence

New table `assistant_artifact_events`:

| column            | type      | notes                                                |
| ----------------- | --------- | ---------------------------------------------------- |
| id                | guid      | PK                                                   |
| conversation_id   | guid      | FK → assistants.conversation_id                      |
| message_id        | guid?     | FK → assistant_messages.id; null until next turn end |
| sequence          | int       | per-conversation sequence; orders pills inline       |
| kind              | enum      | `WorkflowPackageDraft`, `WorkflowPackageSnapshot`, … |
| name              | text      | display name (e.g., `draft.cf-workflow-package.json`)|
| relative_path     | text      | path inside the workspace                            |
| snapshot_id       | guid?     | non-null for immutable snapshots                     |
| summary_json      | jsonb     | tool-supplied; e.g., entry-point, item counts        |
| superseded_by_id  | guid?     | self-FK; set when a later event replaces this one    |
| created_at_utc    | timestamp |                                                      |

Events are written **inside** the tool-dispatcher path, just after
the tool successfully produces the artifact. They are bound to a
conversation immediately and re-bound to the assistant message id
when the turn finishes (mirrors how `assistant_messages` itself
gets its sequence assigned post-turn).

### API surface

- `GET /api/assistant/conversations/{id}/artifacts` —
  list events for the conversation, owner-scoped, ordered by
  sequence.
- `GET /api/assistant/conversations/{id}/artifacts/{eventId}` —
  stream the bytes. Resolves `relative_path` (or `snapshot_id`) under
  the conversation's workspace dir; 404 if the file no longer
  exists.
- `POST /api/assistant/conversations/{id}/artifacts/{eventId}/save` —
  re-run `save_workflow_package` semantics on the artifact bytes
  (Phase 2 only, for `WorkflowPackageDraft` and
  `WorkflowPackageSnapshot` kinds). Returns the same shape the
  existing `/package/apply` endpoint does so the rail's mini-chip
  can mirror the chat chip's outcome handling.

### Stream events

New SSE event kind `artifact-event` carries `{ id, sequence, kind,
name, summary, supersedes? }` so the chat-panel can render the pill
live without refetching the list. On reload, the conversation-load
endpoint includes the persisted events alongside `messages` and the
chat panel hydrates from there.

### Chat-panel rendering

- **Inline pill** between message bubbles, anchored to the assistant
  message that produced it. Pill shows kind icon + name +
  short summary + Download / View actions.
- **Pinned rail** above the composer (Phase 2) lists all
  non-superseded artifacts with kind, name, age, and quick
  actions (Download, View, Save to library for package kinds).
- **Read-only Monaco preview** opens in a side sheet on View.

### The recorder contract (locked in AA-8)

`IArtifactRecorder` (in `CodeFlow.Api/Assistant/Artifacts/`) is the
canonical write path for every artifact event. Producer tools call
this; nothing else writes directly to the repository.

```csharp
public interface IArtifactRecorder
{
    Task<AssistantArtifactEvent> RecordAsync(
        Guid conversationId,
        ArtifactEventKind kind,
        string name,
        string relativePath,
        Guid? snapshotId,
        string? summaryJson,
        bool supersedesPriorByName,
        CancellationToken cancellationToken = default);

    Task<int> ClearByNameAsync(
        Guid conversationId,
        string name,
        CancellationToken cancellationToken = default);

    Task<int> MarkSnapshotExpiredAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);
}
```

Repository access is split:

- `IAssistantArtifactReadRepository` — list + get; injected by API
  endpoints that surface artifacts to the chat panel.
- `IAssistantArtifactRepository` (extends the read interface) —
  full read+write; injected only by the recorder. Producer code that
  injects this interface to bypass the recorder is a code-review
  red flag.

### `ArtifactEventKind` values + `relativePath` conventions

Numeric values are stable (the column stores the int code). New kinds
are additive; reordering / removing values would corrupt rows.

| Kind                       | Value | Live? | `name` shape                                | `relativePath`                                       |
| -------------------------- | ----- | ----- | ------------------------------------------- | ---------------------------------------------------- |
| `WorkflowPackageDraft`     | 1     | yes   | `draft.cf-workflow-package.json`            | same as `name`                                       |
| `WorkflowPackageSnapshot`  | 2     | no    | `snapshot-{guid:N}.cf-workflow-package.json`| same as `name`                                       |
| `TraceDiagnostic` (AA-9)   | 3     | yes   | `diagnose-{traceId:N}-{utcTs}.json`         | same as `name`                                       |
| `EvidenceBundle` (AA-9)    | 4     | yes   | `evidence-{traceId:N}-{utcTs}.zip`          | same as `name`                                       |

Bytes always live under the conversation's workspace root. `name` is
what the chat panel displays in the pill; `relativePath` is what the
download endpoint resolves on disk. They're equal for every kind
shipped today; future kinds with subdirectories (e.g. attachments
under `attachments/{guid}/...`) can diverge.

### Implementing a new artifact producer

1. Pick (or add) an `ArtifactEventKind` value. Reserve the int via
   numeric assignment in the enum and add a row to the table above.
2. Inject `IArtifactRecorder` into the producer (tool, endpoint,
   service — anything with the conversation id in scope).
3. After successfully writing the bytes to disk in the conversation
   workspace, call `RecordAsync` with the kind, `name`,
   `relativePath`, optional `snapshotId` (only for snapshot kinds),
   optional `summaryJson` (any free-form JSON the UI will use to
   render the pill summary line), and `supersedesPriorByName: true`
   when the new event replaces an active prior event with the same
   name.
4. If the producer's bytes are immutable and consumed downstream
   (apply-from-draft semantics), call `MarkSnapshotExpiredAsync`
   before deleting the file so the metadata row outlives the bytes
   in a discoverable state.
5. Add unit + integration tests mirroring AA-1's
   `WorkflowPackageDraftToolsTests` and `AssistantArtifactRepositoryTests`.

The chat panel renders pills + rail rows automatically — no UI
changes needed for a new kind, unless the kind needs custom actions
beyond Download / View.

## Implementation plan (10 slices)

### Phase 1 — inline artifact pills, persistence-grade

- **AA-1** Persistence + recorder hooks. New `assistant_artifact_events`
  table (EF migration), `IAssistantArtifactRepository`,
  `IArtifactRecorder` (Phase 1 scope: invoked from
  `WorkflowPackageDraftTools` + `WorkflowPackageTools` snapshot
  emit). Unit-tested.
- **AA-2** Live SSE + inline pill render. New `artifact-event`
  stream item; chat-panel ingests and renders a pill anchored to
  the in-flight assistant message. No persistence read yet — pills
  appear during the turn.
- **AA-3** Conversation-load hydration. Extend
  `ConversationResponse` with `artifactEvents`; chat-panel seeds
  pills on load. Pills survive reload + thread switch.
- **AA-4** Download + read-only preview. New endpoint streams
  bytes. Chat-panel pill's Download triggers a blob save; View
  opens a Monaco-readonly side sheet.

### Phase 2 — pinned rail + recovery affordance

- **AA-5** Artifact rail. Pinned strip above composer shows all
  non-superseded artifacts, ordered newest-first. Same Download /
  View actions as the inline pill.
- **AA-6** Save-to-library from rail. New
  `POST .../artifacts/{id}/save` endpoint mirrors
  `save_workflow_package` semantics for package-kind artifacts;
  rail mini-chip mirrors the chat chip's apply / resolve outcomes.
  Closes the dismissed-chip recovery loop (the original motivating
  scenario).
- **AA-7** Diff viewer. From any package-kind pill or rail entry,
  open a Monaco side-by-side diff against (a) the prior snapshot
  for that name, or (b) the current library version. Read-only.

### Phase 3 — generalize the artifact contract

- **AA-8** `ArtifactEventKind` taxonomy + `IArtifactRecorder`
  abstraction shipped as the canonical write path; migrate all
  existing package-draft producers off direct repo writes.
- **AA-9** Trace-diagnostic + evidence-bundle producers emit
  artifact events. `diagnose_trace` writes a JSON summary as an
  artifact; `export_evidence_bundle` writes the zipped bundle as
  an artifact. (Existing trace evidence-bundle endpoint stays —
  this is the conversation-scoped surface.)
- **AA-10** Documentation + skill prompt update. Add producer
  guidance to `Assistant/Skills/workflow-authoring.md` and any
  new producer skill so future tools naturally write artifact
  events.

## Risks and follow-ups

- **Workspace lifecycle.** If a workspace is ever GC'd or rotated,
  artifact events go stale. The download endpoint already 404s when
  the underlying file is gone; we surface that as
  `state: 'expired'` on the pill so the user sees why Download
  doesn't work. Long-term cleanup of orphaned events is a
  follow-up.
- **Multi-tenant.** When the `codeflow-team` repo lands tenant
  scoping, this table needs a `tenant_id` FK and the lookup must
  scope by tenant, mirroring whatever pattern lands for
  `assistant_messages`. Out of scope for this epic.
- **Conversation export.** A natural follow-up is "download all
  artifacts in this conversation as a zip." Not in scope.
- **Snapshot retention.** `save_workflow_package` snapshots are
  cleaned up after apply today. We need to keep them around for
  the artifact lifetime — AA-1 includes a guard against
  `DeleteSnapshot` orphaning a referenced event.

## Migration

- AA-1 EF migration adds a new table + indexes; no existing data
  changes. Safe to roll forward without backfill — pre-existing
  conversations simply have no artifact events.
- No API breaking changes. New endpoints; existing endpoints
  unchanged.
- UI: chat-panel handles missing `artifactEvents` field
  defensively (back-compat with pre-AA-3 server bodies).
