# UrlShortener.Web

The web app for the URL shortener: Angular 22 (standalone, zoneless, signals) with Angular Material.

- **Shorten** a URL, anonymously or signed in, with an optional custom alias.
- **Get an API key** (register) and **sign in** with it. The key is kept in `sessionStorage`, or in
  `localStorage` if you tick "Remember me".
- **My links**: your 100 most recent links.
- **Link stats**: total clicks, last click and clicks per UTC day (7, 30, 90 or 365 days).
- **Top links**: the most clicked links across the service.

## Running it

Normally through Aspire, with everything else:

```sh
aspire run   # from the repo root, or run UrlShortener.AppHost
```

The AppHost installs packages, runs `npm start -- --port <port>` and passes the API's address
through service discovery. `ng serve` proxies `/api` to it ([proxy.conf.js](proxy.conf.js)), so
the browser only ever talks to the web app's origin. Short links point at the API, which serves the
redirects.

On its own, against an API started from its `http` launch profile (`http://localhost:5080`):

```sh
npm ci
npm start    # http://localhost:4200
```

## Checks

```sh
npm run lint          # ESLint (angular-eslint, including template accessibility rules)
npm run format:check  # Prettier
npm test              # Vitest
npm run build         # production build
```

CI runs all four, and the AppHost's running-app tests start this app for real through Aspire.
