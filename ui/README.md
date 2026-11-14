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

## Notes

- Source text is rendered with text interpolation and `<mark>`, never `innerHTML`.
- The reviewer name is kept in `localStorage` as a convenience. It is not authentication; sign-in arrives with the
  security milestone.
