import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthStore } from '../../core/auth.store';
import { SignInPage, safeReturnUrl } from './sign-in-page';

describe('safeReturnUrl', () => {
  it('allows in-app paths', () => {
    expect(safeReturnUrl('/links/abc?x=1')).toBe('/links/abc?x=1');
  });

  it('falls back to my links for anything that could leave the app', () => {
    expect(safeReturnUrl(undefined)).toBe('/links');
    expect(safeReturnUrl('https://evil.example')).toBe('/links');
    expect(safeReturnUrl('//evil.example')).toBe('/links');
    expect(safeReturnUrl('/\\evil.example')).toBe('/links');
  });
});

describe('SignInPage', () => {
  const signIn = vi.fn<(key: string, remember: boolean) => Promise<boolean>>();
  let fixture: ComponentFixture<SignInPage>;
  let root: HTMLElement;
  let navigate: ReturnType<typeof vi.spyOn>;

  beforeEach(async () => {
    signIn.mockReset();
    TestBed.configureTestingModule({
      imports: [SignInPage],
      providers: [provideRouter([]), { provide: AuthStore, useValue: { signIn } }],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(SignInPage);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  async function submit(key: string): Promise<void> {
    const input = root.querySelector('input[type=password]') as HTMLInputElement;
    input.value = key;
    input.dispatchEvent(new Event('input'));
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
  }

  it('signs in and goes to the page the guard came from', async () => {
    fixture.componentRef.setInput('returnUrl', '/links/abc');
    signIn.mockResolvedValue(true);

    await submit('usk_u1.secret');

    expect(signIn).toHaveBeenCalledWith('usk_u1.secret', false);
    expect(navigate).toHaveBeenCalledWith('/links/abc');
  });

  it('says so when the key is rejected', async () => {
    signIn.mockResolvedValue(false);

    await submit('usk_u1.wrong');

    expect(navigate).not.toHaveBeenCalled();
    expect(root.querySelector('mat-error')?.textContent).toContain("That API key isn't valid.");
  });

  it("doesn't call the API for something that isn't a key", async () => {
    await submit('hello');

    expect(signIn).not.toHaveBeenCalled();
    expect(root.querySelector('mat-error')?.textContent).toContain("doesn't look like an API key");
  });
});
