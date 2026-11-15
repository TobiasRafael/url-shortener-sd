import { HttpErrorResponse } from '@angular/common/http';

/** What the UI shows for a failed call: a summary line and per-field messages. */
export interface ApiError {
  status: number;
  message: string;
  /** Validation messages keyed by camelCase field name (`longUrl`, `email`, ...). */
  fieldErrors: Record<string, string[]>;
}

interface ProblemDetails {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

/** Turns an HTTP failure (RFC 9457 problem details from the API, or none at all) into an ApiError. */
export function toApiError(error: unknown): ApiError {
  if (!(error instanceof HttpErrorResponse)) {
    return { status: 0, message: 'Something went wrong. Please try again.', fieldErrors: {} };
  }

  if (error.status === 0) {
    return {
      status: 0,
      message: "Can't reach the server. Check your connection and try again.",
      fieldErrors: {},
    };
  }

  const problem: ProblemDetails = isProblem(error.error) ? error.error : {};
  const fieldErrors = normalizeKeys(problem.errors ?? {});

  if (error.status === 429) {
    const seconds = Number(error.headers.get('Retry-After'));
    const wait =
      Number.isFinite(seconds) && seconds > 0
        ? `in ${seconds} second${seconds === 1 ? '' : 's'}`
        : 'later';
    return {
      status: 429,
      message: `Too many requests. Try again ${wait}, or sign in with an API key for higher limits.`,
      fieldErrors,
    };
  }

  const message =
    problem.detail ??
    problem.title ??
    (error.status >= 500 ? 'The server had a problem. Please try again.' : 'The request failed.');
  return { status: error.status, message, fieldErrors };
}

function isProblem(body: unknown): body is ProblemDetails {
  return typeof body === 'object' && body !== null;
}

/** The API's validation keys are camelCase already, but be lenient about `LongUrl` vs `longUrl`. */
function normalizeKeys(errors: Record<string, string[]>): Record<string, string[]> {
  return Object.fromEntries(
    Object.entries(errors).map(([key, messages]) => [
      key.charAt(0).toLowerCase() + key.slice(1),
      messages,
    ]),
  );
}
