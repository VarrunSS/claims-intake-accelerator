import { Provider } from '@angular/core';
import { MSAL_INSTANCE, MsalService } from '@azure/msal-angular';
import { PublicClientApplication } from '@azure/msal-browser';
import { AuthConfig } from './config';
import { MSAL_CLIENT } from './msal-client';

/**
 * Creates the MSAL client, completes a sign-in redirect if this page load is the return from Entra ID, and returns the providers
 * that make MsalService available to AuthService. This module is the only place the MSAL packages are imported, and main.ts
 * loads it with a dynamic import, so it is a separate chunk that dev mode and an unconfigured build never download.
 */
export async function createMsalProviders(auth: AuthConfig): Promise<Provider[]> {
  const msal = new PublicClientApplication({
    auth: {
      clientId: auth.clientId,
      authority: `https://login.microsoftonline.com/${auth.tenantId}`,
      redirectUri: window.location.origin,
    },
    cache: { cacheLocation: 'sessionStorage' },
  });
  await msal.initialize();
  const result = await msal.handleRedirectPromise();
  if (result?.account) msal.setActiveAccount(result.account);
  return [{ provide: MSAL_INSTANCE, useValue: msal }, MsalService, { provide: MSAL_CLIENT, useExisting: MsalService }];
}
