import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatCard, MatCardContent } from '@angular/material/card';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { toApiError } from '../../core/api-error';
import { AuthStore } from '../../core/auth.store';

/** Keys look like `usk_{userId}.{secret}`; the API does the real check. */
const API_KEY_SHAPE = /^usk_[^.\s]+\.\S+$/;

@Component({
  selector: 'app-sign-in-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatCard,
    MatCardContent,
    MatCheckbox,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatInput,
    MatLabel,
    MatProgressBar,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './sign-in-page.html',
  styleUrl: './account.scss',
})
export class SignInPage {
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  /** Where to go after signing in (set by the auth guard), from the query string. */
  readonly returnUrl = input<string>();

  protected readonly form = inject(NonNullableFormBuilder).group({
    apiKey: ['', [Validators.required, Validators.pattern(API_KEY_SHAPE)]],
    remember: [false],
  });

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async submit(): Promise<void> {
    if (this.form.invalid || this.busy()) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    const { apiKey, remember } = this.form.getRawValue();
    try {
      if (await this.auth.signIn(apiKey, remember)) {
        await this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
      } else {
        this.form.controls.apiKey.setErrors({ rejected: true });
      }
    } catch (failure) {
      this.error.set(toApiError(failure).message);
    } finally {
      this.busy.set(false);
    }
  }
}

/** Only in-app paths: an absolute or protocol-relative URL here would be an open redirect. */
export function safeReturnUrl(url: string | undefined): string {
  return url && url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/\\')
    ? url
    : '/links';
}
