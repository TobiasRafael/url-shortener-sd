import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { UserResponse } from './core/api-models';
import { AuthStore } from './core/auth.store';

describe('App', () => {
  const user = signal<UserResponse | null>(null);
  const isSignedIn = signal(false);

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        { provide: AuthStore, useValue: { user, isSignedIn, signOut: () => undefined } },
      ],
    });
  });

  async function render(): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  const links = (root: HTMLElement) =>
    Array.from(root.querySelectorAll('mat-toolbar a')).map((a) => a.textContent?.trim());

  it('offers sign in and registration when signed out', async () => {
    user.set(null);
    isSignedIn.set(false);

    const root = await render();

    expect(links(root)).toEqual([
      'linkURL Shortener',
      'Shorten',
      'Top links',
      'Sign in',
      'Get an API key',
    ]);
  });

  it("shows the user's links and name when signed in", async () => {
    user.set({
      id: 'u1',
      name: 'Ada',
      email: 'ada@example.com',
      createdAt: '2026-11-01T00:00:00Z',
    });
    isSignedIn.set(true);

    const root = await render();

    expect(links(root)).toContain('My links');
    expect(root.querySelector('.user')?.textContent).toBe('Ada');
    expect(root.querySelector('mat-toolbar button')?.textContent?.trim()).toBe('Sign out');
  });
});
