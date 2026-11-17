import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api.service';
import { UserResponse } from './api-models';

const STORAGE_KEY = 'url-shortener.api-key';

/**
 * The signed-in user, identified by their API key.
 *
 * The key lives in sessionStorage (gone when the tab closes) unless the user asks to be
 * remembered, then in localStorage. It is a bearer secret: anything that runs script on this
 * origin can read it, which is why "remember me" is opt-in.
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly api = inject(ApiService);

  private readonly key = signal<string | null>(null);
  private readonly currentUser = signal<UserResponse | null>(null);

  readonly apiKey = this.key.asReadonly();
  readonly user = this.currentUser.asReadonly();
  readonly isSignedIn = computed(() => this.currentUser() !== null);

  /**
   * Checks the key with the API, then keeps it. Resolves to false for a key the API rejects;
   * other failures (network, 5xx) are thrown so the caller can say what went wrong.
   */
  async signIn(apiKey: string, remember: boolean): Promise<boolean> {
    const key = apiKey.trim();
    try {
      const user = await firstValueFrom(this.api.getMe(key));
      this.key.set(key);
      this.currentUser.set(user);
      persist(key, remember);
      return true;
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        return false;
      }
      throw error;
    }
  }

  signOut(): void {
    this.key.set(null);
    this.currentUser.set(null);
    persist(null, false);
  }

  /** Restores a stored key at startup. A key the API no longer accepts is dropped. */
  async restore(): Promise<void> {
    const stored = read(sessionStorage) ?? read(localStorage);
    if (!stored) {
      return;
    }
    try {
      const user = await firstValueFrom(this.api.getMe(stored));
      this.key.set(stored);
      this.currentUser.set(user);
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        persist(null, false);
      }
      // Anything else (API down): start signed out but keep the key for the next visit.
    }
  }
}

function persist(key: string | null, remember: boolean): void {
  write(sessionStorage, key && !remember ? key : null);
  write(localStorage, key && remember ? key : null);
}

// Storage can be missing or throw (private windows, blocked site data); signing in still works
// for the life of the page.
function read(storage: Storage): string | null {
  try {
    return storage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function write(storage: Storage, value: string | null): void {
  try {
    if (value === null) {
      storage.removeItem(STORAGE_KEY);
    } else {
      storage.setItem(STORAGE_KEY, value);
    }
  } catch {
    // See read().
  }
}
