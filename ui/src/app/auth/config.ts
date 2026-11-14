import { InjectionToken } from '@angular/core';

/** Entra ID settings read from config.json at start-up. All three must be non-empty to turn on Entra sign-in. */
export interface AuthConfig {
  clientId: string;
  tenantId: string;
  apiScope: string;
}

export interface RuntimeConfig {
  auth: AuthConfig;
}

export const UNCONFIGURED: RuntimeConfig = { auth: { clientId: '', tenantId: '', apiScope: '' } };

export const RUNTIME_CONFIG = new InjectionToken<RuntimeConfig>('RUNTIME_CONFIG', {
  providedIn: 'root',
  factory: () => UNCONFIGURED,
});

export function isConfigured(config: RuntimeConfig): boolean {
  const a = config.auth;
  return !!a.clientId && !!a.tenantId && !!a.apiScope;
}

/** Reads config.json. A missing, unreadable or malformed file means "not configured". */
export async function loadRuntimeConfig(fetchFn: typeof fetch = fetch): Promise<RuntimeConfig> {
  try {
    const response = await fetchFn('config.json');
    if (!response.ok) return UNCONFIGURED;
    const body = (await response.json()) as { auth?: Partial<Record<keyof AuthConfig, unknown>> } | null;
    const text = (v: unknown) => (typeof v === 'string' ? v.trim() : '');
    return {
      auth: {
        clientId: text(body?.auth?.clientId),
        tenantId: text(body?.auth?.tenantId),
        apiScope: text(body?.auth?.apiScope),
      },
    };
  } catch {
    return UNCONFIGURED;
  }
}
