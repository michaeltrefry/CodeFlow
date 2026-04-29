import { Injectable, computed, inject, signal } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { LlmProviderKey } from './models';

/**
 * HAA-16 — per-user, browser-local persistence for the chat composer's provider/model selection.
 * Keyed by the authenticated subject id when available, falling back to a single shared anonymous
 * bucket so demo-mode users still get persistence across page reloads. If browser storage proves
 * insufficient (cross-device sync requirement) we'll graduate to a DB-backed user-preferences
 * table — see the HAA-16 story for the trigger.
 */
export interface AssistantSelection {
  provider: LlmProviderKey | null;
  model: string | null;
}

const STORAGE_PREFIX = 'cf.assistant.selection';
const ANON_KEY = `${STORAGE_PREFIX}.anon`;

@Injectable({ providedIn: 'root' })
export class AssistantPreferencesService {
  private readonly auth = inject(AuthService);

  /** Current user's stored selection (or empty when nothing saved yet). */
  private readonly selection = signal<AssistantSelection>(this.read());

  readonly current = computed(() => this.selection());

  /**
   * Persist a new selection. Pass `{ provider: null, model: null }` to clear back to defaults.
   * Mirrors AssistantSettingsResponse so callers can flow values straight through without
   * normalization.
   */
  save(next: AssistantSelection): void {
    this.selection.set(next);
    this.write(next);
  }

  /** Reload from storage — useful if the auth subject changes mid-session. */
  reload(): void {
    this.selection.set(this.read());
  }

  private storageKey(): string {
    const userId = this.tryReadUserId();
    return userId ? `${STORAGE_PREFIX}.${userId}` : ANON_KEY;
  }

  private read(): AssistantSelection {
    if (typeof localStorage === 'undefined') {
      return { provider: null, model: null };
    }
    try {
      const raw = localStorage.getItem(this.storageKey());
      if (!raw) return { provider: null, model: null };
      const parsed = JSON.parse(raw) as Partial<AssistantSelection>;
      return {
        provider: (parsed?.provider ?? null) as LlmProviderKey | null,
        model: parsed?.model ?? null,
      };
    } catch {
      return { provider: null, model: null };
    }
  }

  private write(value: AssistantSelection): void {
    if (typeof localStorage === 'undefined') return;
    try {
      const cleaned: AssistantSelection = {
        provider: value.provider || null,
        model: value.model || null,
      };
      if (cleaned.provider === null && cleaned.model === null) {
        localStorage.removeItem(this.storageKey());
      } else {
        localStorage.setItem(this.storageKey(), JSON.stringify(cleaned));
      }
    } catch {
      // Quota / private-mode failures shouldn't break chat — just skip persistence.
    }
  }

  /**
   * Resolve a stable per-user key for the storage bucket. Reads the auth service's currentUser
   * signal; null/unauthenticated callers share the ANON_KEY bucket.
   */
  private tryReadUserId(): string | null {
    try {
      return this.auth.currentUser()?.id ?? null;
    } catch {
      return null;
    }
  }
}
