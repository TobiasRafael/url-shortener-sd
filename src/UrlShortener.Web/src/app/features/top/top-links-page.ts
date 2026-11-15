import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { toApiError } from '../../core/api-error';

const TOP_LIMIT = 10;

@Component({
  selector: 'app-top-links-page',
  imports: [DatePipe, DecimalPipe, RouterLink, MatButton, MatIcon, MatProgressBar, MatTableModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './top-links-page.html',
  styleUrl: '../links/links.scss',
})
export class TopLinksPage {
  private readonly api = inject(ApiService);

  protected readonly limit = TOP_LIMIT;
  protected readonly columns = ['rank', 'shortUrl', 'longUrl', 'totalClicks', 'lastClickedAt'];

  protected readonly top = rxResource({ stream: () => this.api.getTopLinks(TOP_LIMIT) });
  protected readonly error = computed(() =>
    this.top.error() ? toApiError(this.top.error()).message : null,
  );
}
