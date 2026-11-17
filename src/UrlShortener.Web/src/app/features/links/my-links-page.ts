import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltip } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { toApiError } from '../../core/api-error';
import { LinkResponse, MAX_MY_LINKS } from '../../core/api-models';
import { CopyButton } from '../../shared/copy-button';

@Component({
  selector: 'app-my-links-page',
  imports: [
    DatePipe,
    RouterLink,
    MatAnchor,
    MatButton,
    MatIcon,
    MatProgressBar,
    MatTableModule,
    MatTooltip,
    CopyButton,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './my-links-page.html',
  styleUrl: './links.scss',
})
export class MyLinksPage implements OnInit {
  private readonly api = inject(ApiService);

  // The API returns the newest links up to a limit, with no paging cursor.
  protected readonly limit = MAX_MY_LINKS;
  protected readonly columns = ['shortUrl', 'longUrl', 'createdAt', 'actions'];

  protected readonly loading = signal(true);
  protected readonly links = signal<LinkResponse[]>([]);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.links.set(await firstValueFrom(this.api.getMyLinks(this.limit)));
    } catch (failure) {
      this.error.set(toApiError(failure).message);
    } finally {
      this.loading.set(false);
    }
  }
}
