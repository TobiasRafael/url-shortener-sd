import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LinkResponse } from '../../core/api-models';
import { ShortenPage } from './shorten-page';

const LINK: LinkResponse = {
  code: 'aZ3kP9q',
  shortUrl: 'http://localhost:5080/aZ3kP9q',
  longUrl: 'https://example.com/a/long/path',
  createdAt: '2026-11-15T12:00:00Z',
  isCustom: false,
};

describe('ShortenPage', () => {
  let fixture: ComponentFixture<ShortenPage>;
  let root: HTMLElement;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [ShortenPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ShortenPage);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  afterEach(() => http.verify());

  /** Lets submit() finish after a response (its continuation runs after the request settles), then renders. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  async function fill(longUrl: string, alias = ''): Promise<void> {
    const [urlInput, aliasInput] = Array.from(root.querySelectorAll('input'));
    urlInput.value = longUrl;
    urlInput.dispatchEvent(new Event('input'));
    aliasInput.value = alias;
    aliasInput.dispatchEvent(new Event('input'));
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
  }

  it('shortens a URL and shows the short link', async () => {
    await fill('  https://example.com/a/long/path ', 'my-alias');

    const request = http.expectOne('/api/v1/links');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      longUrl: 'https://example.com/a/long/path',
      customAlias: 'my-alias',
    });
    request.flush(LINK, { status: 201, statusText: 'Created' });
    await settle();

    expect(root.querySelector('.result .short-url a')?.textContent).toBe(LINK.shortUrl);
    expect(root.querySelector('.result-label')?.textContent).toContain('Your short link is ready');
    // The emptied form is ready for the next URL, not flagged as invalid.
    expect(root.querySelector('input')?.value).toBe('');
    expect(root.querySelector('mat-error')).toBeNull();
  });

  it('sends no alias when the field is empty, and says when the link already existed', async () => {
    await fill('https://example.com/a/long/path');

    const request = http.expectOne('/api/v1/links');
    expect(request.request.body.customAlias).toBeNull();
    request.flush(LINK, { status: 200, statusText: 'OK' });
    await settle();

    expect(root.querySelector('.result-label')?.textContent).toContain(
      'You already shortened this URL',
    );
  });

  it("doesn't call the API with an invalid URL", async () => {
    await fill('not a url');

    http.expectNone('/api/v1/links');
    expect(root.querySelector('mat-error')?.textContent).toContain('http:// or https://');
  });

  it('shows a taken alias under the alias field', async () => {
    await fill('https://example.com', 'taken');

    const problem = {
      title: 'Alias already taken',
      status: 409,
      errors: { customAlias: ["The alias 'taken' is already in use."] },
    };
    http.expectOne('/api/v1/links').flush(problem, { status: 409, statusText: 'Conflict' });
    await settle();

    expect(root.querySelector('mat-error')?.textContent).toContain(
      "The alias 'taken' is already in use.",
    );
    expect(root.querySelector('.error')).toBeNull();
  });

  it('explains rate limiting in the banner', async () => {
    await fill('https://example.com');

    http
      .expectOne('/api/v1/links')
      .flush(
        { title: 'Too many requests', status: 429 },
        { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '30' } },
      );
    await settle();

    expect(root.querySelector('.error')?.textContent).toContain('Try again in 30 seconds');
  });
});
