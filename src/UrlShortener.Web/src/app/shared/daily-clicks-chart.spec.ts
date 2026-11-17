import { TestBed } from '@angular/core/testing';
import { DailyClicks } from '../core/api-models';
import { DailyClicksChart } from './daily-clicks-chart';

describe('DailyClicksChart', () => {
  async function render(daily: DailyClicks[]): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(DailyClicksChart);
    fixture.componentRef.setInput('daily', daily);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  const heights = (root: HTMLElement) =>
    Array.from(root.querySelectorAll('rect.bar')).map((bar) => Number(bar.getAttribute('height')));

  it('draws one bar per day, scaled to the busiest day', async () => {
    const root = await render([
      { date: '2026-11-13', clicks: 5 },
      { date: '2026-11-14', clicks: 10 },
      { date: '2026-11-15', clicks: 0 },
    ]);

    expect(heights(root)).toEqual([100, 200, 0]);
    expect(root.querySelector('rect.bar title')?.textContent).toBe('2026-11-13: 5 clicks');
  });

  it('keeps small days visible next to a large one', async () => {
    const root = await render([
      { date: '2026-11-14', clicks: 1 },
      { date: '2026-11-15', clicks: 100000 },
    ]);

    expect(heights(root)[0]).toBe(2);
  });

  it('describes the data for screen readers', async () => {
    const root = await render([
      { date: '2026-11-14', clicks: 3 },
      { date: '2026-11-15', clicks: 4 },
    ]);

    expect(root.querySelector('svg')?.getAttribute('aria-label')).toBe(
      'Clicks per day from 2026-11-14 to 2026-11-15: 7 in total, at most 4 in one day.',
    );
  });

  it('draws empty bars when there are no clicks', async () => {
    const root = await render([
      { date: '2026-11-14', clicks: 0 },
      { date: '2026-11-15', clicks: 0 },
    ]);

    expect(heights(root)).toEqual([0, 0]);
  });
});
