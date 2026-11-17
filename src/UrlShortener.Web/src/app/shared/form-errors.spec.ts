import { FormControl, FormGroup } from '@angular/forms';
import { applyApiError, httpUrl } from './form-errors';

describe('httpUrl', () => {
  const check = (value: string) => httpUrl(new FormControl(value));

  it('accepts absolute http and https URLs', () => {
    expect(check('https://example.com/a?b=c')).toBeNull();
    expect(check('http://localhost:8080')).toBeNull();
    expect(check('  https://example.com  ')).toBeNull();
  });

  it('rejects other schemes and relative URLs', () => {
    expect(check('ftp://example.com')).toEqual({ httpUrl: true });
    expect(check('javascript:alert(1)')).toEqual({ httpUrl: true });
    expect(check('example.com')).toEqual({ httpUrl: true });
    expect(check('/relative')).toEqual({ httpUrl: true });
  });

  it('leaves empty values to the required validator', () => {
    expect(check('')).toBeNull();
  });
});

describe('applyApiError', () => {
  const form = () =>
    new FormGroup({ longUrl: new FormControl(''), customAlias: new FormControl('') });

  it('shows field messages under their fields, with no banner', () => {
    const f = form();

    const banner = applyApiError(f, {
      status: 409,
      message: 'Alias already taken',
      fieldErrors: { customAlias: ['Taken.'] },
    });

    expect(banner).toBeNull();
    expect(f.controls.customAlias.getError('server')).toBe('Taken.');
    expect(f.controls.customAlias.touched).toBe(true);
  });

  it('puts messages for unknown fields in the banner', () => {
    const f = form();

    const banner = applyApiError(f, {
      status: 400,
      message: 'Validation failed',
      fieldErrors: { longUrl: ['Bad URL.'], body: ['A JSON body is required.'] },
    });

    expect(banner).toBe('A JSON body is required.');
    expect(f.controls.longUrl.getError('server')).toBe('Bad URL.');
  });

  it('falls back to the summary when there are no field messages', () => {
    expect(
      applyApiError(form(), { status: 429, message: 'Too many requests.', fieldErrors: {} }),
    ).toBe('Too many requests.');
  });
});
