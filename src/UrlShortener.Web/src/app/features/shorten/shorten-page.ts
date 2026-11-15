import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import {
  FormGroupDirective,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatCard, MatCardContent } from '@angular/material/card';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { toApiError } from '../../core/api-error';
import { CUSTOM_ALIAS_PATTERN, CreateLinkResult, LONG_URL_MAX_LENGTH } from '../../core/api-models';
import { AuthStore } from '../../core/auth.store';
import { CopyButton } from '../../shared/copy-button';
import { applyApiError, httpUrl } from '../../shared/form-errors';

@Component({
  selector: 'app-shorten-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButton,
    MatCard,
    MatCardContent,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatInput,
    MatLabel,
    MatProgressBar,
    CopyButton,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shorten-page.html',
  styleUrl: './shorten-page.scss',
})
export class ShortenPage {
  private readonly api = inject(ApiService);
  protected readonly auth = inject(AuthStore);

  protected readonly maxLength = LONG_URL_MAX_LENGTH;
  protected readonly form = inject(NonNullableFormBuilder).group({
    longUrl: ['', [Validators.required, Validators.maxLength(LONG_URL_MAX_LENGTH), httpUrl]],
    customAlias: ['', [Validators.pattern(CUSTOM_ALIAS_PATTERN)]],
  });

  private readonly formDirective = viewChild.required(FormGroupDirective);

  protected readonly submitting = signal(false);
  protected readonly result = signal<CreateLinkResult | null>(null);
  protected readonly error = signal<string | null>(null);

  async submit(): Promise<void> {
    if (this.form.invalid || this.submitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.error.set(null);
    this.result.set(null);
    const { longUrl, customAlias } = this.form.getRawValue();
    try {
      const result = await firstValueFrom(
        this.api.createLink({ longUrl: longUrl.trim(), customAlias: customAlias.trim() || null }),
      );
      this.result.set(result);
      // resetForm, not form.reset(): it also clears the directive's "submitted" state, which
      // Material uses to show errors, so the emptied field doesn't light up as invalid.
      this.formDirective().resetForm();
    } catch (failure) {
      this.error.set(applyApiError(this.form, toApiError(failure)));
    } finally {
      this.submitting.set(false);
    }
  }
}
