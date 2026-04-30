import { TestBed } from '@angular/core/testing';
import { Subject, throwError } from 'rxjs';
import { useAsyncList } from './async-state';

describe('useAsyncList', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('loads items and toggles loading state', () => {
    const source = new Subject<string[]>();
    const state = TestBed.runInInjectionContext(() => useAsyncList(() => source.asObservable()));

    state.reload();

    expect(state.loading()).toBe(true);
    expect(state.items()).toEqual([]);

    source.next(['alpha', 'beta']);
    source.complete();

    expect(state.items()).toEqual(['alpha', 'beta']);
    expect(state.loading()).toBe(false);
    expect(state.error()).toBeNull();
  });

  it('captures fallback errors', () => {
    const state = TestBed.runInInjectionContext(() => useAsyncList(
      () => throwError(() => ({})),
      { errorMessage: 'Failed to load records' },
    ));

    state.reload();

    expect(state.loading()).toBe(false);
    expect(state.error()).toBe('Failed to load records');
  });
});
