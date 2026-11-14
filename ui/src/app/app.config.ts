import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, Provider, provideBrowserGlobalErrorListeners } from '@angular/core';
import { MSAL_INSTANCE, MsalService } from '@azure/msal-angular';
import { IPublicClientApplication } from '@azure/msal-browser';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { RUNTIME_CONFIG, RuntimeConfig, isConfigured } from './auth/config';

/** `msal` is the already initialised client; it is only given when Entra ID is configured. */
export function buildAppConfig(config: RuntimeConfig, msal?: IPublicClientApplication): ApplicationConfig {
  const msalProviders: Provider[] =
    msal && isConfigured(config) ? [{ provide: MSAL_INSTANCE, useValue: msal }, MsalService] : [];
  return {
    providers: [
      provideBrowserGlobalErrorListeners(),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideRouter(routes, withComponentInputBinding()),
      { provide: RUNTIME_CONFIG, useValue: config },
      ...msalProviders,
    ],
  };
}
