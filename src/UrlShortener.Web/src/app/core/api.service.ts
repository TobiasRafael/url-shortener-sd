import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import {
  CreateLinkRequest,
  CreateLinkResult,
  LinkResponse,
  LinkStatsResponse,
  RegisterUserRequest,
  RegisterUserResponse,
  TopLinkResponse,
  UserResponse,
} from './api-models';

/**
 * The REST API under `/api/v1`. Always same-origin: `ng serve` proxies `/api` to the API in
 * development (proxy.conf.js), and the published site's YARP proxy does the same.
 */
export const API_BASE = '/api/v1';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  createLink(request: CreateLinkRequest): Observable<CreateLinkResult> {
    return this.http
      .post<LinkResponse>(`${API_BASE}/links`, request, { observe: 'response' })
      .pipe(map((response) => ({ link: response.body!, created: response.status === 201 })));
  }

  getLink(code: string): Observable<LinkResponse> {
    return this.http.get<LinkResponse>(`${API_BASE}/links/${encodeURIComponent(code)}`);
  }

  getStats(code: string, days: number): Observable<LinkStatsResponse> {
    return this.http.get<LinkStatsResponse>(`${API_BASE}/links/${encodeURIComponent(code)}/stats`, {
      params: new HttpParams().set('days', days),
    });
  }

  register(request: RegisterUserRequest): Observable<RegisterUserResponse> {
    return this.http.post<RegisterUserResponse>(`${API_BASE}/users`, request);
  }

  /**
   * The key's owner. `apiKey` checks a key that isn't stored yet (signing in); without it the
   * interceptor sends the current one.
   */
  getMe(apiKey?: string): Observable<UserResponse> {
    const headers = apiKey ? new HttpHeaders({ [API_KEY_HEADER]: apiKey }) : undefined;
    return this.http.get<UserResponse>(`${API_BASE}/users/me`, { headers });
  }

  getMyLinks(limit: number): Observable<LinkResponse[]> {
    return this.http.get<LinkResponse[]>(`${API_BASE}/users/me/links`, {
      params: new HttpParams().set('limit', limit),
    });
  }

  getTopLinks(limit: number): Observable<TopLinkResponse[]> {
    return this.http.get<TopLinkResponse[]>(`${API_BASE}/analytics/top`, {
      params: new HttpParams().set('limit', limit),
    });
  }
}

export const API_KEY_HEADER = 'X-Api-Key';
