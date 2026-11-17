import { AbstractControl, FormGroup, ValidationErrors, ValidatorFn } from '@angular/forms';
import { ApiError } from '../core/api-error';

/** An absolute http(s) URL, the only kind the API accepts. */
export const httpUrl: ValidatorFn = (control: AbstractControl<string>): ValidationErrors | null => {
  const value = control.value?.trim();
  if (!value) {
    return null; // `required` reports empty values
  }
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:' ? null : { httpUrl: true };
  } catch {
    return { httpUrl: true };
  }
};

/**
 * Puts the API's validation messages on the matching controls as a `server` error, and returns
 * what the form's banner should say instead: messages for fields the form doesn't have, or the
 * summary when no message landed on a field. Null when everything is shown under its field.
 */
export function applyApiError(form: FormGroup, error: ApiError): string | null {
  const unmatched: string[] = [];
  let onFields = false;
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    const control = form.get(field);
    if (control) {
      control.setErrors({ ...control.errors, server: messages.join(' ') });
      control.markAsTouched();
      onFields = true;
    } else {
      unmatched.push(...messages);
    }
  }
  if (unmatched.length > 0) {
    return unmatched.join(' ');
  }
  return onFields ? null : error.message;
}
