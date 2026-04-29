import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  VersionUpdateDialogComponent,
  type VersionUpdateResult,
  type VersionUpdateTarget,
} from './version-update-dialog.component';

describe('VersionUpdateDialogComponent', () => {
  let fixture: ComponentFixture<VersionUpdateDialogComponent>;
  let component: VersionUpdateDialogComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [VersionUpdateDialogComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(VersionUpdateDialogComponent);
    component = fixture.componentInstance;
  });

  it('computes added, removed, and broken-edge port changes for the target', () => {
    component.target = target();
    fixture.detectChanges();

    expect(component.dialogTitle()).toBe("Update agent 'reviewer' to v3");
    expect(component.added()).toEqual(['Escalated']);
    expect(component.removed()).toEqual(['Rejected']);
    expect(component.brokenEdges()).toEqual([
      { sourcePort: 'Rejected', targetLabel: 'escalation logic' },
    ]);
    expect(fixture.nativeElement.textContent).toContain('Edges that will be removed');
  });

  it('emits confirmation payload with latest ports and removed edge ports', () => {
    const confirmed: VersionUpdateResult[] = [];
    component.confirmed.subscribe(result => confirmed.push(result));
    component.target = target();
    fixture.detectChanges();

    component.onConfirm();

    expect(confirmed).toEqual([
      {
        nodeId: 'node-1',
        toVersion: 3,
        newPorts: ['Approved', 'Escalated'],
        edgePortsToRemove: ['Rejected'],
      },
    ]);
  });

  it('emits cancellation when the dialog is closed or cancelled', () => {
    const cancelled: void[] = [];
    component.cancelled.subscribe(() => cancelled.push(undefined));

    component.onCancel();

    expect(cancelled).toHaveLength(1);
  });
});

function target(): VersionUpdateTarget {
  return {
    nodeId: 'node-1',
    kind: 'agent',
    refKey: 'reviewer',
    fromVersion: 2,
    toVersion: 3,
    currentPorts: ['Approved', 'Rejected'],
    latestPorts: ['Approved', 'Escalated'],
    outgoing: [
      { sourcePort: 'Approved', targetLabel: 'finish' },
      { sourcePort: 'Rejected', targetLabel: 'escalation logic' },
    ],
  };
}
