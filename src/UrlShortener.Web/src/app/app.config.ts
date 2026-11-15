import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { TitleStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { apiKeyInterceptor } from './core/api-key.interceptor';
import { AuthStore } from './core/auth.store';
import { AppTitleStrategy } from './core/title-strategy';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withFetch(), withInterceptors([apiKeyInterceptor])),
    { provide: TitleStrategy, useClass: AppTitleStrategy },
    // Restore a stored API key before the first navigation, so guarded pages don't bounce to sign-in.
    provideAppInitializer(() => inject(AuthStore).restore()),
  ],
};
