import { InjectionToken } from '@angular/core';
import type { AccountInfo, AuthenticationResult } from '@azure/msal-browser';
import type { Observable } from 'rxjs';

/**
 * The part of MSAL the app uses, as a plain interface. Only types are imported here, so nothing from the MSAL packages is
 * pulled into the main bundle: main.ts loads MSAL with a dynamic import when Entra sign-in is configured, and provides
 * MsalService under this token. Dev mode never downloads it.
 */
export interface MsalClient {
  instance: {
    getActiveAccount(): AccountInfo | null;
    getAllAccounts(): AccountInfo[];
    setActiveAccount(account: AccountInfo | null): void;
  };
  acquireTokenSilent(request: { scopes: string[]; account: AccountInfo }): Observable<AuthenticationResult>;
  loginRedirect(request?: { scopes: string[] }): Observable<void>;
  logoutRedirect(): Observable<void>;
}

export const MSAL_CLIENT = new InjectionToken<MsalClient>('MSAL_CLIENT');
