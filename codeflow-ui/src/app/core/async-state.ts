import { DestroyRef, Signal, WritableSignal, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable } from 'rxjs';

interface AsyncStateOptions<T> {
  errorMessage?: string | ((err: unknown) => string);
  onLoaded?: (value: T) => void;
}

export interface AsyncState<T> {
  readonly value: WritableSignal<T>;
  readonly loading: Signal<boolean>;
  readonly error: WritableSignal<string | null>;
  reload(): void;
}

export interface AsyncListState<T> {
  readonly items: WritableSignal<T[]>;
  readonly loading: Signal<boolean>;
  readonly error: WritableSignal<string | null>;
  reload(): void;
}

export function useAsyncState<T>(
  initialValue: T,
  load: () => Observable<T>,
  options: AsyncStateOptions<T> = {},
): AsyncState<T> {
  const destroyRef = inject(DestroyRef);
  const value = signal<T>(initialValue);
  const loading = signal(true);
  const error = signal<string | null>(null);

  const reload = (): void => {
    loading.set(true);
    error.set(null);
    load().pipe(takeUntilDestroyed(destroyRef)).subscribe({
      next: result => {
        value.set(result);
        options.onLoaded?.(result);
        loading.set(false);
      },
      error: err => {
        error.set(resolveErrorMessage(err, options.errorMessage));
        loading.set(false);
      },
    });
  };

  return { value, loading, error, reload };
}

export function useAsyncList<T>(
  load: () => Observable<T[]>,
  options: AsyncStateOptions<T[]> = {},
): AsyncListState<T> {
  const state = useAsyncState<T[]>([], load, options);
  return {
    items: state.value,
    loading: state.loading,
    error: state.error,
    reload: state.reload,
  };
}

function resolveErrorMessage(err: unknown, formatter?: string | ((err: unknown) => string)): string {
  if (typeof formatter === 'function') {
    return formatter(err);
  }
  return (err as { message?: string } | null)?.message ?? formatter ?? 'Failed to load';
}
