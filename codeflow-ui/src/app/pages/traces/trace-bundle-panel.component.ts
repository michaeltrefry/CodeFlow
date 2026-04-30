import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { TracesApi } from '../../core/traces.api';
import {
  TraceBundleArtifactRef,
  TraceBundleAuthoritySnapshot,
  TraceBundleManifest,
} from '../../core/models';
import { CardComponent } from '../../ui/card.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';

interface RefusalStageBucket {
  stage: string;
  count: number;
}

/**
 * sc-271 PR2: trace evidence bundle composition panel + Export Bundle action.
 *
 * Reads the bundle manifest (the same JSON that lives at `manifest.json` inside the
 * exported zip) so the inspector can show how many decisions, refusals, authority
 * snapshots, artifacts, and token usage records the bundle would contain — without
 * needing a client-side zip parser. The Export button hits the zip endpoint to
 * trigger the actual download.
 *
 * Self-contained: takes only the `traceId` and fetches its own data. Renders nothing
 * for traces that don't yet have a saga (404 path).
 */
@Component({
  selector: 'cf-trace-bundle-panel',
  standalone: true,
  imports: [CommonModule, DatePipe, CardComponent, ButtonComponent, ChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (manifest(); as m) {
      <cf-card title="Evidence bundle">
        <ng-template #cardRight>
          <button type="button" cf-button variant="ghost"
                  (click)="exportBundle()" [disabled]="exporting()">
            {{ exporting() ? 'Exporting…' : 'Export Bundle' }}
          </button>
        </ng-template>

        <p class="muted small bundle-intro">
          Portable, hash-pinned snapshot of this trace's saga subtree, decisions,
          refusals, authority snapshots, token usage, and every referenced artifact.
          Schema: <code class="mono">{{ m.schemaVersion }}</code>.
        </p>

        <div class="bundle-stats">
          <cf-chip mono>{{ m.trace.decisions.length }} decisions</cf-chip>
          <cf-chip mono>{{ m.trace.subflowSagas.length }} subflow sagas</cf-chip>
          <cf-chip mono>{{ m.trace.refusals.length }} refusals</cf-chip>
          <cf-chip mono>{{ m.trace.authoritySnapshots.length }} authority snapshots</cf-chip>
          <cf-chip mono>{{ m.artifacts.length }} artifacts</cf-chip>
          @if (missingArtifactCount() > 0) {
            <cf-chip variant="err" dot mono>{{ missingArtifactCount() }} missing</cf-chip>
          }
          <cf-chip mono>{{ m.trace.tokenUsage.recordCount }} token records</cf-chip>
        </div>

        @if (refusalStages().length > 0) {
          <details class="bundle-section">
            <summary>
              <strong>Refusals</strong>
              <span class="muted xsmall">
                @for (bucket of refusalStages(); track bucket.stage) {
                  <span class="bucket-tag">{{ bucket.stage }} × {{ bucket.count }}</span>
                }
              </span>
            </summary>
            <ul class="bundle-list">
              @for (refusal of m.trace.refusals; track refusal.id) {
                <li>
                  <div class="row-spread">
                    <span>
                      <cf-chip variant="err" dot mono>{{ refusal.stage }}</cf-chip>
                      <code class="mono small">{{ refusal.code }}</code>
                      @if (refusal.axis) {
                        <span class="muted xsmall">axis: {{ refusal.axis }}</span>
                      }
                    </span>
                    <span class="muted xsmall mono">{{ refusal.occurredAtUtc | date:'medium' }}</span>
                  </div>
                  <div class="muted small reason">{{ refusal.reason }}</div>
                </li>
              }
            </ul>
          </details>
        }

        @if (m.trace.authoritySnapshots.length > 0) {
          <details class="bundle-section">
            <summary>
              <strong>Authority snapshots</strong>
              <span class="muted xsmall">{{ m.trace.authoritySnapshots.length }} resolved envelopes</span>
            </summary>
            <ul class="bundle-list">
              @for (snapshot of m.trace.authoritySnapshots; track snapshot.id) {
                <li>
                  <div class="row-spread">
                    <span>
                      <code class="mono small">{{ snapshot.agentKey }}</code>
                      @if (snapshot.agentVersion != null) {
                        <span class="muted xsmall">v{{ snapshot.agentVersion }}</span>
                      }
                      @if (blockedAxesCountFor(snapshot); as count) {
                        @if (count > 0) {
                          <cf-chip variant="warn" dot mono>{{ count }} blocked axes</cf-chip>
                        }
                      }
                    </span>
                    <span class="muted xsmall mono">{{ snapshot.resolvedAtUtc | date:'medium' }}</span>
                  </div>
                </li>
              }
            </ul>
          </details>
        }

        <p class="muted xsmall generated-at">
          Manifest generated {{ m.generatedAtUtc | date:'medium' }}
        </p>
      </cf-card>
    } @else if (loadError()) {
      <cf-card title="Evidence bundle">
        <p class="muted small">{{ loadError() }}</p>
      </cf-card>
    }
  `,
  styles: [`
    .bundle-intro { margin: 0 0 12px 0; }
    .bundle-stats {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-bottom: 12px;
    }
    .bundle-section { margin-top: 10px; }
    .bundle-section summary {
      cursor: pointer;
      display: flex;
      gap: 12px;
      align-items: baseline;
      padding: 6px 0;
    }
    .bundle-section summary strong { font-weight: 600; }
    .bucket-tag + .bucket-tag::before { content: ' · '; }
    .bundle-list {
      list-style: none;
      margin: 4px 0 0 0;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .bundle-list li { padding: 6px 0; border-top: 1px solid var(--hairline); }
    .bundle-list li:first-child { border-top: none; }
    .row-spread {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    .row-spread > span:first-child { display: inline-flex; gap: 6px; align-items: center; }
    .reason { margin-top: 4px; word-break: break-word; }
    .generated-at { margin: 12px 0 0 0; }
  `],
})
export class TraceBundlePanelComponent implements OnDestroy {
  private readonly api = inject(TracesApi);

  readonly traceId = input.required<string>();

  readonly manifest = signal<TraceBundleManifest | null>(null);
  readonly exporting = signal(false);
  readonly loadError = signal<string | null>(null);

  /** Bundle manifest records "missing" artifacts as pointers with empty `sha256` and a
   *  `missing-…` bundle path so retention sweeps don't silently drop evidence. Surface
   *  the count as a warn chip when present. */
  readonly missingArtifactCount = computed(() =>
    (this.manifest()?.artifacts ?? []).filter(this.isMissingArtifact).length,
  );

  readonly refusalStages = computed<RefusalStageBucket[]>(() => {
    const list = this.manifest()?.trace.refusals ?? [];
    if (list.length === 0) return [];
    const counts = new Map<string, number>();
    for (const refusal of list) {
      counts.set(refusal.stage, (counts.get(refusal.stage) ?? 0) + 1);
    }
    return [...counts.entries()]
      .map(([stage, count]) => ({ stage, count }))
      .sort((a, b) => b.count - a.count);
  });

  private loadSub?: Subscription;
  private exportSub?: Subscription;

  constructor() {
    effect(() => {
      const id = this.traceId();
      this.fetch(id);
    });
  }

  ngOnDestroy(): void {
    this.loadSub?.unsubscribe();
    this.exportSub?.unsubscribe();
  }

  blockedAxesCountFor(snapshot: TraceBundleAuthoritySnapshot): number {
    return this.parseBlockedAxesCount(snapshot.blockedAxesJson);
  }

  exportBundle(): void {
    if (this.exporting()) return;
    this.exporting.set(true);
    this.exportSub?.unsubscribe();
    const id = this.traceId();
    this.exportSub = this.api.downloadBundle(id).subscribe({
      next: response => this.triggerDownload(response, id),
      error: () => this.exporting.set(false),
      complete: () => this.exporting.set(false),
    });
  }

  private fetch(id: string): void {
    this.loadSub?.unsubscribe();
    this.manifest.set(null);
    this.loadError.set(null);
    this.loadSub = this.api.getBundleManifest(id).subscribe({
      next: m => this.manifest.set(m),
      error: (err: unknown) => {
        if (err instanceof HttpErrorResponse && err.status === 404) {
          this.loadError.set('No bundle available yet — saga has not produced any state.');
        } else {
          this.loadError.set('Could not load bundle manifest.');
        }
      },
    });
  }

  private triggerDownload(response: HttpResponse<Blob>, traceId: string): void {
    const blob = response.body;
    if (!blob) return;
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = this.fileNameFromResponse(response.headers.get('content-disposition'))
      ?? `trace-${traceId.replace(/-/g, '')}.zip`;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  private fileNameFromResponse(contentDisposition: string | null): string | null {
    if (!contentDisposition) return null;
    const match = /filename="?([^";]+)"?/i.exec(contentDisposition);
    return match?.[1] ?? null;
  }

  private isMissingArtifact(ref: TraceBundleArtifactRef): boolean {
    return ref.sha256.length === 0 || ref.bundlePath.includes('/missing-');
  }

  /** `BlockedAxesJson` is serialized as a JSON array of axis strings on the backend.
   *  Older snapshots may carry an empty string or `null`; both should resolve to 0. */
  private parseBlockedAxesCount(raw: string | null): number {
    if (!raw) return 0;
    try {
      const parsed = JSON.parse(raw);
      return Array.isArray(parsed) ? parsed.length : 0;
    } catch {
      return 0;
    }
  }

}
