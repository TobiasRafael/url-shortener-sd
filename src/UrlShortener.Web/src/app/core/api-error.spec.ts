import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { toApiError } from './api-error';

function httpError(
  status: number,
  body: unknown = null,
  headers: Record<string, string> = {},
): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body, headers: new HttpHeaders(headers) });
}

describe('toApiError', () => {
  it('uses the problem detail, then the title', () => {
    expect(
      toApiError(httpError(404, { title: 'Short link not found', detail: "No link 'abc'." }))
        .message,
    ).toBe("No link 'abc'.");
    expect(toApiError(httpError(409, { title: 'Alias already taken' })).message).toBe(
      'Alias already taken',
    );
  });

  it('keeps validation errors, with camelCase keys', () => {
    const error = toApiError(
      httpError(400, {
        title: 'One or more validation errors occurred.',
        errors: { LongUrl: ['Too long.'], email: ['Bad.'] },
      }),
    );

    expect(error.status).toBe(400);
    expect(error.fieldErrors).toEqual({ longUrl: ['Too long.'], email: ['Bad.'] });
  });

  it('says when to retry after a 429', () => {
    expect(
      toApiError(httpError(429, { title: 'Too many requests' }, { 'Retry-After': '42' })).message,
    ).toContain('in 42 seconds');
    expect(toApiError(httpError(429, null, { 'Retry-After': '1' })).message).toContain(
      'in 1 second,',
    );
    expect(toApiError(httpError(429)).message).toContain('Try again later');
  });

  it('explains network failures and errors without a body', () => {
    expect(toApiError(httpError(0)).message).toContain("Can't reach the server");
    expect(toApiError(httpError(503)).message).toBe('The server had a problem. Please try again.');
    expect(toApiError(httpError(400, 'not json')).message).toBe('The request failed.');
  });

  it('handles errors that are not HTTP errors', () => {
    expect(toApiError(new Error('boom'))).toEqual({
      status: 0,
      message: 'Something went wrong. Please try again.',
      fieldErrors: {},
    });
  });
});
