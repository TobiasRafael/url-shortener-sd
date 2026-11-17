import { Clipboard } from '@angular/cdk/clipboard';
import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltip } from '@angular/material/tooltip';

@Component({
  selector: 'app-copy-button',
  imports: [MatIconButton, MatIcon, MatTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      mat-icon-button
      type="button"
      [matTooltip]="label()"
      [attr.aria-label]="label()"
      (click)="copy()"
    >
      <mat-icon>content_copy</mat-icon>
    </button>
  `,
})
export class CopyButton {
  private readonly clipboard = inject(Clipboard);
  private readonly snackBar = inject(MatSnackBar);

  readonly value = input.required<string>();
  readonly label = input('Copy');

  copy(): void {
    const copied = this.clipboard.copy(this.value());
    this.snackBar.open(
      copied ? 'Copied to clipboard' : "Couldn't copy. Select the text and copy it manually.",
      undefined,
      {
        duration: 2500,
      },
    );
  }
}
