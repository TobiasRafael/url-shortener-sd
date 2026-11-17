import { Routes } from '@angular/router';
import { signedInGuard } from './core/auth.guard';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Shorten a link',
    loadComponent: () => import('./features/shorten/shorten-page').then((m) => m.ShortenPage),
  },
  {
    path: 'links',
    title: 'My links',
    canActivate: [signedInGuard],
    loadComponent: () => import('./features/links/my-links-page').then((m) => m.MyLinksPage),
  },
  {
    // Stats are public, like the API endpoint: anyone with the short code can see them.
    path: 'links/:code',
    title: 'Link stats',
    loadComponent: () => import('./features/links/link-stats-page').then((m) => m.LinkStatsPage),
  },
  {
    path: 'top',
    title: 'Top links',
    loadComponent: () => import('./features/top/top-links-page').then((m) => m.TopLinksPage),
  },
  {
    path: 'sign-in',
    title: 'Sign in',
    loadComponent: () => import('./features/account/sign-in-page').then((m) => m.SignInPage),
  },
  {
    path: 'register',
    title: 'Get an API key',
    loadComponent: () => import('./features/account/register-page').then((m) => m.RegisterPage),
  },
  { path: '**', redirectTo: '' },
];
