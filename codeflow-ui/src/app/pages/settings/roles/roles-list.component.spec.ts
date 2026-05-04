import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AgentRole } from '../../../core/models';
import { RolesListComponent } from './roles-list.component';

describe('RolesListComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RolesListComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('filters visible roles by selected tags', () => {
    const fixture = TestBed.createComponent(RolesListComponent);
    fixture.detectChanges();

    httpMock.expectOne('/api/agent-roles?includeArchived=false').flush([
      role({ id: 1, key: 'reviewer', tags: ['review', 'ops'] }),
      role({ id: 2, key: 'reader', tags: ['docs'] }),
      role({ id: 3, key: 'operator', tags: ['ops'] }),
    ]);
    fixture.detectChanges();

    expect(fixture.componentInstance.availableTags()).toEqual(['docs', 'ops', 'review']);

    fixture.componentInstance.addTag('ops');

    expect(fixture.componentInstance.visibleRoles().map(r => r.key)).toEqual(['reviewer', 'operator']);
    expect(fixture.componentInstance.visibleTagOptions()).toEqual(['docs', 'review']);
  });

  it('selects only visible roles when toggling all under a tag filter', () => {
    const fixture = TestBed.createComponent(RolesListComponent);
    fixture.detectChanges();

    httpMock.expectOne('/api/agent-roles?includeArchived=false').flush([
      role({ id: 1, key: 'reviewer', tags: ['review', 'ops'] }),
      role({ id: 2, key: 'reader', tags: ['docs'] }),
      role({ id: 3, key: 'operator', tags: ['ops'] }),
    ]);
    fixture.detectChanges();

    fixture.componentInstance.addTag('ops');
    fixture.componentInstance.toggleAll({ target: { checked: true } } as unknown as Event);

    expect([...fixture.componentInstance.selectedIds()]).toEqual([1, 3]);
  });
});

function role(overrides: Partial<AgentRole>): AgentRole {
  return {
    id: 1,
    key: 'role',
    displayName: 'Role',
    description: null,
    createdAtUtc: '2026-05-03T00:00:00Z',
    createdBy: null,
    updatedAtUtc: '2026-05-03T00:00:00Z',
    updatedBy: null,
    isArchived: false,
    isRetired: false,
    tags: [],
    ...overrides,
  };
}
