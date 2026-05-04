import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { WorkflowsListComponent } from './workflows-list.component';
import {
  WorkflowPackageImportApplyResult,
  WorkflowPackageImportItem,
  WorkflowPackageImportPreview,
  WorkflowPackageImportResolution,
  WorkflowsApi,
} from '../../core/workflows.api';

/**
 * sc-396: per-conflict resolution flow on the imports page. Covers the resolutions signal →
 * debounced re-preview path, UseExisting confirmation modal, and the drift-409 retry path.
 * The component is instantiated standalone with a faked WorkflowsApi; we drive the preview
 * state directly rather than going through the file-upload pipeline so the tests stay
 * focused on resolution behavior, not File / Promise plumbing.
 */
describe('WorkflowsListComponent (sc-396 resolutions)', () => {
  // Faked WorkflowsApi — we only need the few methods the component touches. Exposed as a
  // simple object of vi.fn()s so individual tests can override return values per-call via
  // `mockReturnValueOnce` without re-wiring the whole stub.
  let api: {
    list: ReturnType<typeof vi.fn>;
    previewPackageImport: ReturnType<typeof vi.fn>;
    applyPackageImport: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    vi.useFakeTimers();
    api = {
      list: vi.fn(() => of([])),
      previewPackageImport: vi.fn(() => of(makeEmptyPreview())),
      applyPackageImport: vi.fn(() => of(makeApplyResult())),
    };

    await TestBed.configureTestingModule({
      imports: [WorkflowsListComponent],
      providers: [
        provideRouter([]),
        { provide: WorkflowsApi, useValue: api },
      ],
    }).compileComponents();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function seedPreview(component: WorkflowsListComponent, items: WorkflowPackageImportItem[]): void {
    // Pull the component into the "post-upload, preview loaded" state without driving the
    // file-upload pipeline. The Map signals stay empty, mirroring a fresh upload.
    component.importPreview.set({
      entryPoint: { key: 'demo', version: 1 },
      items,
      warnings: [],
      createCount: items.filter(i => i.action === 'Create').length,
      reuseCount: items.filter(i => i.action === 'Reuse').length,
      conflictCount: items.filter(i => i.action === 'Conflict').length,
      refusedCount: items.filter(i => i.action === 'Refused').length,
      warningCount: 0,
      canApply: items.every(i => i.action !== 'Conflict' && i.action !== 'Refused'),
    });
    // pendingImportPackage is private — set via cast so the runApply / runRePreview paths
    // see a non-null package and proceed.
    (component as unknown as { pendingImportPackage: unknown }).pendingImportPackage = {
      schemaVersion: 'codeflow.workflow-package.v1',
      entryPoint: { key: 'demo', version: 1 },
      workflows: [{ key: 'demo', version: 1 }],
      agents: [{ key: 'writer', version: 2 }],
    };
  }

  it('renders dropdown options for Conflict rows + carries existingMaxVersion through to the resolution', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    const conflictItem: WorkflowPackageImportItem = {
      kind: 'Agent',
      key: 'writer',
      version: 2,
      action: 'Conflict',
      message: 'Stale package version',
      sourceVersion: 2,
      existingMaxVersion: 3,
    };
    seedPreview(component, [conflictItem]);

    const options = component.resolutionOptions(conflictItem);
    expect(options.map(o => o.id)).toEqual(['UseExisting', 'Bump', 'Copy']);
    expect(options.find(o => o.id === 'UseExisting')!.label).toContain('v3');
    expect(options.find(o => o.id === 'Bump')!.label).toContain('v4');

    // Picking Bump (no UseExisting prompt) commits the resolution and schedules the re-preview.
    component.onResolutionChange(conflictItem, 'Bump');
    expect(component.resolutions().get('Agent:writer:2')).toEqual({
      mode: 'Bump',
      expectedExistingMaxVersion: 3,
    });

    api.previewPackageImport.mockReturnValueOnce(of({
      ...makeEmptyPreview(),
      items: [{ ...conflictItem, action: 'Create', version: 4 }],
      createCount: 1,
      conflictCount: 0,
      canApply: true,
    }));
    vi.advanceTimersByTime(300);

    expect(api.previewPackageImport).toHaveBeenCalledTimes(1);
    const [, resolutions] = api.previewPackageImport.mock.calls.at(-1)!;
    expect(resolutions).toEqual([expect.objectContaining<WorkflowPackageImportResolution>({
      kind: 'Agent',
      key: 'writer',
      sourceVersion: 2,
      mode: 'Bump',
      expectedExistingMaxVersion: 3,
    })]);
    expect(component.importPreview()!.canApply).toBe(true);
  });

  it('debounces re-preview so multiple dropdown changes coalesce into a single call', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    const itemA: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'writer', version: 2, action: 'Conflict',
      message: '', sourceVersion: 2, existingMaxVersion: 3,
    };
    const itemB: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'reviewer', version: 4, action: 'Conflict',
      message: '', sourceVersion: 4, existingMaxVersion: 5,
    };
    seedPreview(component, [itemA, itemB]);

    component.onResolutionChange(itemA, 'Bump');
    vi.advanceTimersByTime(150); // halfway through the debounce window
    component.onResolutionChange(itemB, 'Bump');
    vi.advanceTimersByTime(150); // total elapsed since first change is 300, but second change reset the timer

    expect(api.previewPackageImport).not.toHaveBeenCalled();

    vi.advanceTimersByTime(150); // 300ms since the second change

    expect(api.previewPackageImport).toHaveBeenCalledTimes(1);
  });

  it('first UseExisting pick prompts the modal; confirmation commits the resolution', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    const item: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'writer', version: 2, action: 'Conflict',
      message: '', sourceVersion: 2, existingMaxVersion: 3,
    };
    seedPreview(component, [item]);

    component.onResolutionChange(item, 'UseExisting');

    // Modal is showing, no resolution committed, no re-preview scheduled.
    expect(component.useExistingPrompt()).not.toBeNull();
    expect(component.resolutions().size).toBe(0);
    vi.advanceTimersByTime(500);
    expect(api.previewPackageImport).not.toHaveBeenCalled();

    component.confirmUseExistingPrompt();
    expect(component.useExistingPrompt()).toBeNull();
    expect(component.useExistingWarned()).toBe(true);
    expect(component.resolutions().get('Agent:writer:2')!.mode).toBe('UseExisting');
    vi.advanceTimersByTime(300);
    expect(api.previewPackageImport).toHaveBeenCalledTimes(1);
  });

  it('subsequent UseExisting picks skip the modal once the user has been warned', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    const itemA: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'writer', version: 2, action: 'Conflict',
      message: '', sourceVersion: 2, existingMaxVersion: 3,
    };
    const itemB: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'reviewer', version: 4, action: 'Conflict',
      message: '', sourceVersion: 4, existingMaxVersion: 5,
    };
    seedPreview(component, [itemA, itemB]);
    component.useExistingWarned.set(true);

    component.onResolutionChange(itemA, 'UseExisting');
    component.onResolutionChange(itemB, 'UseExisting');

    expect(component.useExistingPrompt()).toBeNull();
    expect(component.resolutions().size).toBe(2);
    vi.advanceTimersByTime(300);
    expect(api.previewPackageImport).toHaveBeenCalledTimes(1);
  });

  it('clearResolutions wipes the map and re-fetches the preview without resolutions', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    const item: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'writer', version: 2, action: 'Conflict',
      message: '', sourceVersion: 2, existingMaxVersion: 3,
    };
    seedPreview(component, [item]);
    component.onResolutionChange(item, 'Bump');
    vi.advanceTimersByTime(300);
    api.previewPackageImport.mockClear();

    component.clearResolutions();
    vi.advanceTimersByTime(300);

    expect(component.resolutions().size).toBe(0);
    expect(api.previewPackageImport).toHaveBeenCalledTimes(1);
    const [, resolutions] = api.previewPackageImport.mock.calls.at(-1)!;
    expect(resolutions).toBeUndefined();
  });

  it('apply renders drift banner on 409 and the retry sends acknowledgeDrift=true', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    const item: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'writer', version: 4, action: 'Create',
      message: 'Bump', sourceVersion: 2, existingMaxVersion: 3,
    };
    seedPreview(component, [item]);
    component.resolutions.set(new Map([
      ['Agent:writer:2', { mode: 'Bump', expectedExistingMaxVersion: 3 }],
    ]));

    vi.spyOn(window, 'confirm').mockReturnValue(true);

    // First apply: server returns 409 because the library moved to v5 between preview + apply.
    api.applyPackageImport.mockReturnValueOnce(throwError(() => ({
      status: 409,
      error: {
        error: 'Library has moved',
        movedEntities: [
          {
            kind: 'Agent',
            key: 'writer',
            sourceVersion: 2,
            expectedExistingMaxVersion: 3,
            currentExistingMaxVersion: 5,
          },
        ],
      },
    })));

    component.applyPackageImport();

    expect(component.driftConflict()).not.toBeNull();
    expect(component.driftConflict()!.movedEntities[0].currentExistingMaxVersion).toBe(5);
    expect(component.importError()).toBeNull();
    expect(api.applyPackageImport).toHaveBeenCalledTimes(1);
    const firstCallArgs = api.applyPackageImport.mock.calls.at(-1)!;
    expect(firstCallArgs[2]).toBeUndefined(); // acknowledgeDrift not set on first try

    // Retry: succeeds with acknowledgeDrift=true.
    api.applyPackageImport.mockReturnValueOnce(of(makeApplyResult()));
    component.applyAcknowledgingDrift();

    expect(api.applyPackageImport).toHaveBeenCalledTimes(2);
    const retryArgs = api.applyPackageImport.mock.calls.at(-1)!;
    expect(retryArgs[2]).toBe(true);
    expect(component.driftConflict()).toBeNull();
    expect(component.importSuccess()).toContain('Import applied');
  });

  it('apply ignores the click when canApply is false (Refused row blocks apply)', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    seedPreview(component, [{
      kind: 'Agent', key: 'writer', version: 3, action: 'Refused',
      message: 'port mismatch', sourceVersion: 2, existingMaxVersion: 3,
    }]);
    const confirmSpy = vi.spyOn(window, 'confirm');

    component.applyPackageImport();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(api.applyPackageImport).not.toHaveBeenCalled();
  });

  it('rowIsResolvable + resolutionOptions: Reuse rows have no dropdown; Refused disables UseExisting', () => {
    const fixture = TestBed.createComponent(WorkflowsListComponent);
    const component = fixture.componentInstance;
    const reuse: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'reused', version: 1, action: 'Reuse',
      message: '', sourceVersion: 1, existingMaxVersion: 1,
    };
    const refused: WorkflowPackageImportItem = {
      kind: 'Agent', key: 'writer', version: 3, action: 'Refused',
      message: '', sourceVersion: 2, existingMaxVersion: 3,
    };

    expect(component.rowIsResolvable(reuse)).toBe(false);
    expect(component.rowIsResolvable(refused)).toBe(true);
    const refusedOpts = component.resolutionOptions(refused);
    expect(refusedOpts.find(o => o.id === 'UseExisting')!.disabled).toBe(true);
    expect(refusedOpts.find(o => o.id === 'Bump')!.disabled).toBeFalsy();
  });
});

function makeEmptyPreview(): WorkflowPackageImportPreview {
  return {
    entryPoint: { key: 'demo', version: 1 },
    items: [],
    warnings: [],
    createCount: 0,
    reuseCount: 0,
    conflictCount: 0,
    refusedCount: 0,
    warningCount: 0,
    canApply: true,
  };
}

function makeApplyResult(): WorkflowPackageImportApplyResult {
  return {
    entryPoint: { key: 'demo', version: 1 },
    items: [],
    warnings: [],
    createCount: 0,
    reuseCount: 0,
    conflictCount: 0,
    warningCount: 0,
  };
}
