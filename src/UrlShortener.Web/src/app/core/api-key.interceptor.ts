import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { API_BASE, API_KEY_HEADER } from './api.service';
import { AuthStore } from './auth.store';

/** Sends the signed-in user's API key with calls to our API, and only to our API. */
export const apiKeyInterceptor: HttpInterceptorFn = (request, next) => {
  const key = inject(AuthStore).apiKey();
  const ours = request.url.startsWith(`${API_BASE}/`);
  if (!key || !ours || request.headers.has(API_KEY_HEADER)) {
    return next(request);
  }
  return next(request.clone({ setHeaders: { [API_KEY_HEADER]: key } }));
};
