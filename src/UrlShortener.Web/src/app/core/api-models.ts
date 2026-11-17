// Mirrors UrlShortener.Api/Contracts/Contracts.cs. Dates arrive as ISO 8601 strings.

export interface CreateLinkRequest {
  longUrl: string;
  customAlias?: string | null;
}

export interface LinkResponse {
  code: string;
  shortUrl: string;
  longUrl: string;
  createdAt: string;
  isCustom: boolean;
}

export interface DailyClicks {
  /** A UTC day, `yyyy-MM-dd`. */
  date: string;
  clicks: number;
}

export interface LinkStatsResponse {
  code: string;
  shortUrl: string;
  longUrl: string;
  totalClicks: number;
  lastClickedAt: string | null;
  /** One entry per UTC day, oldest first, zero-filled. */
  daily: DailyClicks[];
}

export interface TopLinkResponse {
  code: string;
  shortUrl: string;
  longUrl: string | null;
  totalClicks: number;
  lastClickedAt: string | null;
}

export interface RegisterUserRequest {
  name: string;
  email: string;
}

export interface UserResponse {
  id: string;
  name: string;
  email: string;
  createdAt: string;
}

export interface RegisterUserResponse extends UserResponse {
  /** Shown only once; it can't be recovered. */
  apiKey: string;
}

/** A created link (201) or the caller's existing link for the same URL (200). */
export interface CreateLinkResult {
  link: LinkResponse;
  created: boolean;
}

// Limits enforced by the API; the UI validates the same rules before sending.
export const LONG_URL_MAX_LENGTH = 2048;
export const CUSTOM_ALIAS_PATTERN = /^[A-Za-z0-9_-]{3,16}$/;
export const MAX_MY_LINKS = 100;
export const MAX_TOP_LINKS = 50;
export const MAX_STATS_DAYS = 365;
