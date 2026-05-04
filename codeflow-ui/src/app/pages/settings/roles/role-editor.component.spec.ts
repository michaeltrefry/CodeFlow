import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RoleEditorComponent } from './role-editor.component';

@Component({ standalone: true, template: '' })
class BlankComponent {}

describe('RoleEditorComponent', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RoleEditorComponent],
      providers: [
        provideRouter([{ path: 'settings/roles/:id', component: BlankComponent }]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('creates roles with normalized tag labels', () => {
    const fixture = TestBed.createComponent(RoleEditorComponent);
    fixture.detectChanges();

    fixture.componentInstance.key.set('reviewer');
    fixture.componentInstance.displayName.set('Reviewer');
    fixture.componentInstance.description.set('Reviews changes');
    fixture.componentInstance.tagsText.set('review, ops, review,  ');

    fixture.componentInstance.submit(preventableSubmitEvent());

    const req = httpMock.expectOne('/api/agent-roles');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      key: 'reviewer',
      displayName: 'Reviewer',
      description: 'Reviews changes',
      tags: ['review', 'ops'],
    });
    req.flush({ id: 7 });
  });
});

function preventableSubmitEvent(): Event {
  return { preventDefault: vi.fn() } as unknown as Event;
}
