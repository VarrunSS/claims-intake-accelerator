# Claims review UI

Angular 21 single-page app for handlers. It lists claims, shows the source text beside the extracted fields
(with confidence and validation issues), and lets a handler approve or reject with a reason. Plain CSS, standalone
components and signals, no UI library.

## Run

Start the API on http://localhost:5100 first (see the main README), then:

```bash
npm install
npm start          # http://localhost:4200
```

`npm start` uses `proxy.conf.json`: requests to `/api/...` are forwarded to `http://localhost:5100/...`, so the
browser sees one origin and the UI needs no CORS setup.

## Test and build

```bash
npm test           # Vitest with jsdom, one run, no browser needed
npm run build      # production build into dist/
```

Tests cover the API service (URLs, bodies, error mapping), the pure helpers (`segmentPage`, confidence bands) and
the list and detail components. They run in jsdom, so layout and real-browser behaviour are not tested.

## Signing in

Every `/api/*` call carries `Authorization: Bearer <token>`. The API takes the reviewer from the token, never from
the request body, so the UI has no "your name" field and stores no reviewer name in the browser.

The mode is chosen at start-up from `public/config.json` (fetched as `config.json` before the app starts; a missing
or invalid file means "not configured"):

```json
{ "auth": { "clientId": "", "tenantId": "", "apiScope": "api://<api-client-id>/access_as_user" } }
```

- **Entra ID (MSAL):** when `clientId`, `tenantId` and `apiScope` are all non-empty, the UI signs in with
  `@azure/msal-browser` (redirect flow, authority `https://login.microsoftonline.com/<tenantId>`, cache in
  `sessionStorage`) and sends the `apiScope` access token. Signed-out users see a "Sign in with Microsoft" button.
- **Development sign-in:** when the file is left with empty strings (as committed), a banner in the header says
  "Development sign-in (no Entra ID configured)" and offers two personas: "Claims handler (dev)" sends
  `Bearer dev-handler` and "Senior handler (dev)" sends `Bearer dev-senior`. The default is the handler. The choice
  lives in memory only; changing it reloads `/api/me` and the current view.

Claims routed to senior review can only be approved by a user with the `SeniorHandler` role; for anyone else the
Approve button is disabled with an explanation (the API enforces this too and answers 403). A 401 shows "You are not
signed in or your session has expired." with a sign-in button.

## Notes

- Source text is rendered with text interpolation and `<mark>`, never `innerHTML`.
- The production build is over the 500 kB initial-bundle warning budget because of MSAL; the 1 MB error budget is
  not affected.
