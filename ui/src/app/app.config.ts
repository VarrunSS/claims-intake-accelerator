import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, Provider, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { RUNTIME_CONFIG, RuntimeConfig } from './auth/config';

/**
 * `authProviders` are the MSAL providers (see main.ts). They are passed in, not imported here, so that the MSAL packages
 * stay out of the main bundle: they are a lazy chunk that is only downloaded when Entra sign-in is configured.
 */
export function buildAppConfig(config: RuntimeConfig, authProviders: Provider[] = []): ApplicationConfig {
  return {
    providers: [
      provideBrowserGlobalErrorListeners(),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideRouter(routes, withComponentInputBinding()),
      { provide: RUNTIME_CONFIG, useValue: config },
      ...authProviders,
    ],
  };
}
