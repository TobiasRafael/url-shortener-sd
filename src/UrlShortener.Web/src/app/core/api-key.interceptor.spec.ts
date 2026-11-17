import { HttpClient, HttpHeaders, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { apiKeyInterceptor } from './api-key.interceptor';
import { AuthStore } from './auth.store';

describe('apiKeyInterceptor', () => {
  const apiKey = signal<string | null>(null);
  let client: HttpClient;
  let http: HttpTestingController;

  beforeEach(() => {
    apiKey.set(null);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiKeyInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthStore, useValue: { apiKey } },
      ],
    });
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function headerSentTo(url: string, headers?: HttpHeaders): string | null {
    client.get(url, { headers }).subscribe();
    const request = http.expectOne(url);
    request.flush({});
    return request.request.headers.get('X-Api-Key');
  }

  it('sends the signed-in key to the API', () => {
    apiKey.set('usk_u1.secret');

    expect(headerSentTo('/api/v1/users/me/links')).toBe('usk_u1.secret');
  });

  it('sends nothing when signed out', () => {
    expect(headerSentTo('/api/v1/links/abc')).toBeNull();
  });

  it('never sends the key to other origins or paths', () => {
    apiKey.set('usk_u1.secret');

    expect(headerSentTo('https://example.com/api/v1/links')).toBeNull();
    expect(headerSentTo('/assets/config.json')).toBeNull();
    expect(headerSentTo('/api/v1x/links')).toBeNull();
  });

  it('keeps a key that the caller set explicitly', () => {
    apiKey.set('usk_u1.old');

    expect(headerSentTo('/api/v1/users/me', new HttpHeaders({ 'X-Api-Key': 'usk_u2.new' }))).toBe(
      'usk_u2.new',
    );
  });
});
