import { HttpClient } from '@angular/common/http';
import { Injectable, Injector, computed, inject, signal } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import { firstValueFrom } from 'rxjs';
import { RUNTIME_CONFIG, isConfigured } from './config';

export type AuthMode = 'msal' | 'dev';
export type DevPersona = 'dev-handler' | 'dev-senior';

export const DEV_PERSONAS: { value: DevPersona; label: string }[] = [
  { value: 'dev-handler', label: 'Claims handler (dev)' },
  { value: 'dev-senior', label: 'Senior handler (dev)' },
];

/** The signed-in user, as GET /api/me returns it. */
export interface Me {
  id: string;
  name: string;
  roles: string[];
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly config = inject(RUNTIME_CONFIG);
  private readonly injector = inject(Injector);
  private readonly msal = isConfigured(this.config) ? this.injector.get(MsalService) : null;

  readonly mode: AuthMode = this.msal ? 'msal' : 'dev';

  /** The dev persona. Held in memory only; nothing is written to browser storage. */
  readonly persona = signal<DevPersona>('dev-handler');

  readonly me = signal<Me | null>(null);
  readonly isSenior = computed(() => this.me()?.roles.includes('SeniorHandler') ?? false);

  /** Goes up whenever the identity changes, so views can fetch their data again. */
  readonly session = signal(0);

  private readonly hasAccount = signal(false);
  readonly signedIn = computed(() => this.mode === 'dev' || this.hasAccount());

  constructor() {
    if (this.msal) {
      const client = this.msal.instance;
      const account = client.getActiveAccount() ?? client.getAllAccounts()[0] ?? null;
      if (account) client.setActiveAccount(account);
      this.hasAccount.set(!!account);
    }
  }

  /** The bearer token for the API. Rejects when nobody is signed in or the token cannot be refreshed. */
  async token(): Promise<string> {
    if (!this.msal) return this.persona();
    const client = this.msal.instance;
    const account = client.getActiveAccount() ?? client.getAllAccounts()[0];
    if (!account) throw new Error('Not signed in.');
    const result = await firstValueFrom(
      this.msal.acquireTokenSilent({ scopes: [this.config.auth.apiScope], account }),
    );
    return result.accessToken;
  }

  /** Reloads the signed-in user's name and roles. */
  async loadMe(): Promise<void> {
    if (!this.signedIn()) {
      this.me.set(null);
      return;
    }
    try {
      this.me.set(await firstValueFrom(this.injector.get(HttpClient).get<Me>('/api/me')));
    } catch {
      this.me.set(null);
    }
  }

  /** Dev mode only: switches persona, reloads /api/me, then tells views to fetch their data again. */
  async setPersona(persona: DevPersona): Promise<void> {
    if (this.mode !== 'dev') return;
    this.persona.set(persona);
    this.me.set(null);
    await this.loadMe();
    this.session.update((n) => n + 1);
  }

  signIn(): void {
    if (!this.msal) return;
    this.msal.loginRedirect({ scopes: [this.config.auth.apiScope] }).subscribe();
  }

  signOut(): void {
    if (!this.msal) return;
    this.msal.logoutRedirect().subscribe();
  }
}
