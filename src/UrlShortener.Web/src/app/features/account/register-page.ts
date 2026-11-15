import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatCard, MatCardContent } from '@angular/material/card';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatError, MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { toApiError } from '../../core/api-error';
import { RegisterUserResponse } from '../../core/api-models';
import { AuthStore } from '../../core/auth.store';
import { CopyButton } from '../../shared/copy-button';
import { applyApiError } from '../../shared/form-errors';

@Component({
  selector: 'app-register-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatCard,
    MatCardContent,
    MatCheckbox,
    MatError,
    MatFormField,
    MatIcon,
    MatInput,
    MatLabel,
    MatProgressBar,
    CopyButton,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './register-page.html',
  styleUrl: './account.scss',
})
export class RegisterPage {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);

  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(254)]],
  });
  protected readonly remember = signal(false);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly registered = signal<RegisterUserResponse | null>(null);

  async submit(): Promise<void> {
    if (this.form.invalid || this.busy()) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    const { name, email } = this.form.getRawValue();
    try {
      this.registered.set(
        await firstValueFrom(this.api.register({ name: name.trim(), email: email.trim() })),
      );
    } catch (failure) {
      this.error.set(applyApiError(this.form, toApiError(failure)));
    } finally {
      this.busy.set(false);
    }
  }

  /** Signs in with the key that was just issued. */
  async continue(): Promise<void> {
    const registered = this.registered();
    if (!registered || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      if (await this.auth.signIn(registered.apiKey, this.remember())) {
        await this.router.navigateByUrl('/links');
      } else {
        this.error.set('The new key was not accepted. Copy it and try signing in.');
      }
    } catch (failure) {
      this.error.set(toApiError(failure).message);
    } finally {
      this.busy.set(false);
    }
  }
}
