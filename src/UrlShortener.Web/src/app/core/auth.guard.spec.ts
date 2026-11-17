import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { signedInGuard } from './auth.guard';
import { AuthStore } from './auth.store';

describe('signedInGuard', () => {
  const isSignedIn = signal(false);

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthStore, useValue: { isSignedIn } }],
    });
  });

  function run(url: string): boolean | UrlTree {
    return TestBed.runInInjectionContext(() =>
      signedInGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    ) as boolean | UrlTree;
  }

  it('lets signed-in users through', () => {
    isSignedIn.set(true);

    expect(run('/links')).toBe(true);
  });

  it('sends everyone else to sign in, remembering where they were going', () => {
    isSignedIn.set(false);

    const result = run('/links');

    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe(
      '/sign-in?returnUrl=%2Flinks',
    );
  });
});
