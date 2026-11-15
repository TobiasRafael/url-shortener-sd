import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor } from '@angular/material/button';
import { MatButtonToggle, MatButtonToggleGroup } from '@angular/material/button-toggle';
import { MatCard, MatCardContent } from '@angular/material/card';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { toApiError } from '../../core/api-error';
import { CopyButton } from '../../shared/copy-button';
import { DailyClicksChart } from '../../shared/daily-clicks-chart';

export const STATS_WINDOWS = [7, 30, 90, 365] as const;

@Component({
  selector: 'app-link-stats-page',
  imports: [
    DatePipe,
    DecimalPipe,
    RouterLink,
    MatAnchor,
    MatButtonToggle,
    MatButtonToggleGroup,
    MatCard,
    MatCardContent,
    MatIcon,
    MatProgressBar,
    CopyButton,
    DailyClicksChart,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './link-stats-page.html',
  styleUrls: ['./links.scss', './link-stats-page.scss'],
})
export class LinkStatsPage {
  private readonly api = inject(ApiService);

  /** The short code, from the route. */
  readonly code = input.required<string>();

  protected readonly windows = STATS_WINDOWS;
  protected readonly days = signal<number>(30);

  protected readonly stats = rxResource({
    params: () => ({ code: this.code(), days: this.days() }),
    stream: ({ params }) => this.api.getStats(params.code, params.days),
  });

  protected readonly windowTotal = computed(() =>
    this.stats.hasValue() ? this.stats.value().daily.reduce((sum, day) => sum + day.clicks, 0) : 0,
  );

  protected readonly error = computed(() => {
    const failure = this.stats.error();
    if (!failure) {
      return null;
    }
    const apiError = toApiError(failure);
    return apiError.status === 404 ? "That short link doesn't exist." : apiError.message;
  });
}
