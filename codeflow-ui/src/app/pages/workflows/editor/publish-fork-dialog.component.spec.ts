import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AgentsApi } from '../../../core/agents.api';
import {
  PublishForkDialogComponent,
  type PublishForkResult,
} from './publish-fork-dialog.component';

describe('PublishForkDialogComponent', () => {
  let fixture: ComponentFixture<PublishForkDialogComponent>;
  let component: PublishForkDialogComponent;
  let agentsApi: {
    getPublishStatus: ReturnType<typeof vi.fn>;
    publish: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    agentsApi = {
      getPublishStatus: vi.fn(),
      publish: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [PublishForkDialogComponent],
      providers: [
        { provide: AgentsApi, useValue: agentsApi },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(PublishForkDialogComponent);
    component = fixture.componentInstance;
  });

  it('loads publish status when a target is opened and resets editable state', () => {
    agentsApi.getPublishStatus.mockReturnValue(of({
      forkedFromKey: 'reviewer',
      forkedFromVersion: 2,
      originalLatestVersion: 4,
      isDrift: true,
    }));
    component.acknowledgeDrift = true;
    component.newKey = 'old-key';

    fixture.componentRef.setInput('target', { nodeId: 'node-1', forkKey: 'reviewer__fork' });
    fixture.detectChanges();

    expect(agentsApi.getPublishStatus).toHaveBeenCalledWith('reviewer__fork');
    expect(component.loading()).toBe(false);
    expect(component.status()).toEqual({
      forkedFromKey: 'reviewer',
      forkedFromVersion: 2,
      originalLatestVersion: 4,
      isDrift: true,
    });
    expect(component.acknowledgeDrift).toBe(false);
    expect(component.newKey).toBe('');
    expect(fixture.nativeElement.textContent).toContain('Drift detected');
  });

  it('publishes to the original with drift acknowledgement and emits the relink result', () => {
    const published: PublishForkResult[] = [];
    agentsApi.getPublishStatus.mockReturnValue(of({
      forkedFromKey: 'reviewer',
      forkedFromVersion: 2,
      originalLatestVersion: 4,
      isDrift: true,
    }));
    agentsApi.publish.mockReturnValue(of({
      publishedKey: 'reviewer',
      publishedVersion: 5,
    }));
    component.published.subscribe(result => published.push(result));
    fixture.componentRef.setInput('target', { nodeId: 'node-1', forkKey: 'reviewer__fork' });
    fixture.detectChanges();

    component.acknowledgeDrift = true;
    component.publishToOriginal();

    expect(agentsApi.publish).toHaveBeenCalledWith('reviewer__fork', {
      mode: 'original',
      acknowledgeDrift: true,
    });
    expect(component.saving()).toBeNull();
    expect(published).toEqual([
      { nodeId: 'node-1', publishedKey: 'reviewer', publishedVersion: 5 },
    ]);
  });

  it('trims the new agent key before publishing as a new agent', () => {
    agentsApi.getPublishStatus.mockReturnValue(of({
      forkedFromKey: 'reviewer',
      forkedFromVersion: 2,
      originalLatestVersion: 2,
      isDrift: false,
    }));
    agentsApi.publish.mockReturnValue(of({
      publishedKey: 'reviewer-next',
      publishedVersion: 1,
    }));
    fixture.componentRef.setInput('target', { nodeId: 'node-1', forkKey: 'reviewer__fork' });
    fixture.detectChanges();

    component.newKey = '  reviewer-next  ';
    component.publishAsNew();

    expect(agentsApi.publish).toHaveBeenCalledWith('reviewer__fork', {
      mode: 'new-agent',
      newKey: 'reviewer-next',
    });
  });

  it('surfaces load and publish errors without emitting success', () => {
    const published: PublishForkResult[] = [];
    agentsApi.getPublishStatus.mockReturnValue(throwError(() => ({ error: { error: 'Status failed' } })));
    component.published.subscribe(result => published.push(result));

    fixture.componentRef.setInput('target', { nodeId: 'node-1', forkKey: 'bad-fork' });
    fixture.detectChanges();

    expect(component.loading()).toBe(false);
    expect(component.loadError()).toBe('Status failed');

    agentsApi.getPublishStatus.mockReturnValue(of({
      forkedFromKey: 'reviewer',
      forkedFromVersion: 2,
      originalLatestVersion: 2,
      isDrift: false,
    }));
    agentsApi.publish.mockReturnValue(throwError(() => ({ error: 'Publish failed' })));
    fixture.componentRef.setInput('target', { nodeId: 'node-2', forkKey: 'reviewer__fork' });
    fixture.detectChanges();

    component.publishToOriginal();

    expect(component.saving()).toBeNull();
    expect(component.saveError()).toBe('Publish failed');
    expect(published).toEqual([]);
  });
});
