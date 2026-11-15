import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from './auth.store';

/** Pages that need an API key send signed-out visitors to sign in, then back. */
export const signedInGuard: CanActivateFn = (_route, state) => {
  if (inject(AuthStore).isSignedIn()) {
    return true;
  }
  return inject(Router).createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } });
};
