import { PublicClientApplication } from '@azure/msal-browser';
import { bootstrapApplication } from '@angular/platform-browser';
import { buildAppConfig } from './app/app.config';
import { App } from './app/app';
import { isConfigured, loadRuntimeConfig } from './app/auth/config';

async function start(): Promise<void> {
  const config = await loadRuntimeConfig();
  let msal: PublicClientApplication | undefined;
  if (isConfigured(config)) {
    msal = new PublicClientApplication({
      auth: {
        clientId: config.auth.clientId,
        authority: `https://login.microsoftonline.com/${config.auth.tenantId}`,
        redirectUri: window.location.origin,
      },
      cache: { cacheLocation: 'sessionStorage' },
    });
    await msal.initialize();
    // Completes a sign-in redirect, if this page load is the return from Entra ID.
    const result = await msal.handleRedirectPromise();
    if (result?.account) msal.setActiveAccount(result.account);
  }
  await bootstrapApplication(App, buildAppConfig(config, msal));
}

start().catch((err) => console.error(err));
