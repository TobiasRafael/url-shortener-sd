import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { UserResponse } from './api-models';
import { AuthStore } from './auth.store';

const KEY = 'usk_u1.secret';
const STORAGE_KEY = 'url-shortener.api-key';
const USER: UserResponse = {
  id: 'u1',
  name: 'Ada',
  email: 'ada@example.com',
  createdAt: '2026-11-01T00:00:00Z',
};

describe('AuthStore', () => {
  let store: AuthStore;
  let http: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    store = TestBed.inject(AuthStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function answerMe(status: number): void {
    const request = http.expectOne('/api/v1/users/me');
    expect(request.request.headers.get('X-Api-Key')).toBe(KEY);
    if (status === 200) {
      request.flush(USER);
    } else if (status === 0) {
      request.error(new ProgressEvent('error')); // network failure
    } else {
      request.flush({ title: 'Unauthorized' }, { status, statusText: 'Error' });
    }
  }

  it('signs in with a key the API accepts, for this tab only by default', async () => {
    const signedIn = store.signIn(` ${KEY} `, false);
    answerMe(200);

    expect(await signedIn).toBe(true);
    expect(store.isSignedIn()).toBe(true);
    expect(store.user()).toEqual(USER);
    expect(store.apiKey()).toBe(KEY);
    expect(sessionStorage.getItem(STORAGE_KEY)).toBe(KEY);
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('keeps the key in localStorage only when asked to remember', async () => {
    const signedIn = store.signIn(KEY, true);
    answerMe(200);
    await signedIn;

    expect(localStorage.getItem(STORAGE_KEY)).toBe(KEY);
    expect(sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('rejects a key the API refuses, without storing it', async () => {
    const signedIn = store.signIn(KEY, true);
    answerMe(401);

    expect(await signedIn).toBe(false);
    expect(store.isSignedIn()).toBe(false);
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('throws other failures so the page can report them', async () => {
    const signedIn = store.signIn(KEY, false);
    answerMe(503);

    await expect(signedIn).rejects.toMatchObject({ status: 503 });
    expect(store.isSignedIn()).toBe(false);
  });

  it('signs out and forgets the key everywhere', async () => {
    const signedIn = store.signIn(KEY, true);
    answerMe(200);
    await signedIn;

    store.signOut();

    expect(store.isSignedIn()).toBe(false);
    expect(store.apiKey()).toBeNull();
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('restores a stored key the API still accepts', async () => {
    localStorage.setItem(STORAGE_KEY, KEY);

    const restored = store.restore();
    answerMe(200);
    await restored;

    expect(store.user()).toEqual(USER);
  });

  it('drops a stored key the API no longer accepts', async () => {
    sessionStorage.setItem(STORAGE_KEY, KEY);

    const restored = store.restore();
    answerMe(401);
    await restored;

    expect(store.isSignedIn()).toBe(false);
    expect(sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('keeps a stored key when the API is unreachable, to retry on the next visit', async () => {
    localStorage.setItem(STORAGE_KEY, KEY);

    const restored = store.restore();
    answerMe(0);
    await restored;

    expect(store.isSignedIn()).toBe(false);
    expect(localStorage.getItem(STORAGE_KEY)).toBe(KEY);
  });

  it('does not call the API when nothing is stored', async () => {
    await store.restore();

    http.expectNone('/api/v1/users/me');
    expect(store.isSignedIn()).toBe(false);
  });
});
