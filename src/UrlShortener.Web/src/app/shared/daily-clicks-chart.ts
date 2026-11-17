import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DailyClicks } from '../core/api-models';

interface Bar {
  date: string;
  /** Tooltip text, e.g. "2026-11-15: 1,204 clicks". */
  label: string;
  x: number;
  y: number;
  height: number;
}

const WIDTH = 720;
const HEIGHT = 200;
const GAP_RATIO = 0.2;
const NUMBER = new Intl.NumberFormat('en-US');

/** Clicks per UTC day as a bar chart. Plain SVG: one series doesn't need a chart library. */
@Component({
  selector: 'app-daily-clicks-chart',
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './daily-clicks-chart.scss',
  template: `
    <figure>
      <svg
        [attr.viewBox]="'0 0 ' + width + ' ' + height"
        preserveAspectRatio="none"
        role="img"
        [attr.aria-label]="summary()"
      >
        <line class="baseline" x1="0" [attr.y1]="height" [attr.x2]="width" [attr.y2]="height" />
        @for (bar of bars(); track bar.date) {
          <rect
            class="bar"
            [attr.x]="bar.x"
            [attr.y]="bar.y"
            [attr.width]="barWidth()"
            [attr.height]="bar.height"
          >
            <title>{{ bar.label }}</title>
          </rect>
        }
      </svg>
      <figcaption>
        <span>{{ firstDate() }}</span>
        <span>Peak: {{ peak() | number }} / day</span>
        <span>{{ lastDate() }}</span>
      </figcaption>
    </figure>
  `,
})
export class DailyClicksChart {
  readonly daily = input.required<DailyClicks[]>();

  protected readonly width = WIDTH;
  protected readonly height = HEIGHT;

  protected readonly peak = computed(() => Math.max(0, ...this.daily().map((d) => d.clicks)));
  protected readonly slot = computed(() => WIDTH / Math.max(1, this.daily().length));
  protected readonly barWidth = computed(() => this.slot() * (1 - GAP_RATIO));

  protected readonly bars = computed<Bar[]>(() => {
    const peak = this.peak();
    const slot = this.slot();
    return this.daily().map((day, index) => {
      // Days with clicks get at least a sliver, so a 1 next to a 10,000 is still visible.
      const height = peak === 0 || day.clicks === 0 ? 0 : Math.max(2, (day.clicks / peak) * HEIGHT);
      return {
        date: day.date,
        label: `${day.date}: ${NUMBER.format(day.clicks)} ${day.clicks === 1 ? 'click' : 'clicks'}`,
        x: index * slot + (slot * GAP_RATIO) / 2,
        y: HEIGHT - height,
        height,
      };
    });
  });

  protected readonly firstDate = computed(() => this.daily()[0]?.date ?? '');
  protected readonly lastDate = computed(() => this.daily().at(-1)?.date ?? '');

  protected readonly summary = computed(() => {
    const days = this.daily();
    const total = days.reduce((sum, d) => sum + d.clicks, 0);
    return `Clicks per day from ${this.firstDate()} to ${this.lastDate()}: ${total} in total, at most ${this.peak()} in one day.`;
  });
}
