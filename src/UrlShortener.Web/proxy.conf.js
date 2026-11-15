// @ts-check
// Development only: `ng serve` forwards /api to the API, so the browser stays on one origin
// (no CORS, no API URL baked into the bundle). Under Aspire the API's address comes from service
// discovery; standalone `npm start` falls back to the API's launch profile.
// http is preferred: the proxy then doesn't have to trust the API's development certificate.
const target =
  process.env['services__api__http__0'] ??
  process.env['services__api__https__0'] ??
  'http://localhost:5080';

module.exports = {
  '/api': {
    target,
    secure: false,
    // The API builds short URLs from the Host it sees, so it must see its own (the redirect
    // endpoint lives on the API, not on the web app).
    changeOrigin: true,
  },
};
