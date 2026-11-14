import { bootstrapApplication } from '@angular/platform-browser';
import { buildAppConfig } from './app/app.config';
import { App } from './app/app';
import { isConfigured, loadRuntimeConfig } from './app/auth/config';

async function start(): Promise<void> {
  const config = await loadRuntimeConfig();
  // MSAL is about a third of the app's size, and only Entra sign-in needs it: it loads as a separate chunk, on demand.
  const authProviders = isConfigured(config)
    ? await import('./app/auth/msal-providers').then((m) => m.createMsalProviders(config.auth))
    : [];
  await bootstrapApplication(App, buildAppConfig(config, authProviders));
}

start().catch((err) => console.error(err));
