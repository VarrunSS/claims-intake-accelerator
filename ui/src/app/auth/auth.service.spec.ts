import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MsalService } from '@azure/msal-angular';
import { of, throwError } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';
import { RUNTIME_CONFIG, RuntimeConfig, UNCONFIGURED, isConfigured, loadRuntimeConfig } from './config';

const configured: RuntimeConfig = {
  auth: { clientId: 'client-1', tenantId: 'tenant-1', apiScope: 'api://api-1/access_as_user' },
};

function fakeMsal(accounts: object[] = [{ homeAccountId: 'acc-1' }]) {
  const instance = {
    getActiveAccount: vi.fn(() => null),
    getAllAccounts: vi.fn(() => accounts),
    setActiveAccount: vi.fn(),
  };
  return {
    instance,
    acquireTokenSilent: vi.fn(() => of({ accessToken: 'entra-token' })),
    loginRedirect: vi.fn(() => of(undefined)),
    logoutRedirect: vi.fn(() => of(undefined)),
  };
}

function setupDev() {
  TestBed.configureTestingModule({
    providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
  });
  return { auth: TestBed.inject(AuthService), http: TestBed.inject(HttpTestingController) };
}

function setupMsal(msal = fakeMsal()) {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      { provide: RUNTIME_CONFIG, useValue: configured },
      { provide: MsalService, useValue: msal },
    ],
  });
  return { auth: TestBed.inject(AuthService), http: TestBed.inject(HttpTestingController), msal };
}

describe('AuthService in dev mode', () => {
  it('defaults to the handler persona and uses its token', async () => {
    const { auth } = setupDev();
    expect(auth.mode).toBe('dev');
    expect(auth.persona()).toBe('dev-handler');
    expect(await auth.token()).toBe('dev-handler');
  });

  it('changes the token when the persona changes, reloads /api/me and bumps the session', async () => {
    const { auth, http } = setupDev();
    const before = auth.session();
    const done = auth.setPersona('dev-senior');
    const req = await vi.waitFor(() => http.expectOne('/api/me'));
    expect(req.request.headers.get('Authorization')).toBe('Bearer dev-senior');
    req.flush({ id: 'oid-2', name: 'Dev Senior', roles: ['SeniorHandler'] });
    await done;
    expect(await auth.token()).toBe('dev-senior');
    expect(auth.me()?.name).toBe('Dev Senior');
    expect(auth.session()).toBe(before + 1);
    http.verify();
  });

  it('derives isSenior from the roles in /api/me', async () => {
    const { auth, http } = setupDev();
    expect(auth.isSenior()).toBe(false);

    const handler = auth.loadMe();
    (await vi.waitFor(() => http.expectOne('/api/me'))).flush({ id: 'a', name: 'A', roles: ['ClaimsHandler'] });
    await handler;
    expect(auth.isSenior()).toBe(false);

    const senior = auth.loadMe();
    (await vi.waitFor(() => http.expectOne('/api/me'))).flush({ id: 'b', name: 'B', roles: ['ClaimsHandler', 'SeniorHandler'] });
    await senior;
    expect(auth.isSenior()).toBe(true);
  });

  it('clears the user when /api/me fails', async () => {
    const { auth, http } = setupDev();
    const first = auth.loadMe();
    (await vi.waitFor(() => http.expectOne('/api/me'))).flush({ id: 'a', name: 'A', roles: [] });
    await first;
    const second = auth.loadMe();
    (await vi.waitFor(() => http.expectOne('/api/me'))).flush(null, { status: 401, statusText: 'Unauthorized' });
    await second;
    expect(auth.me()).toBeNull();
  });

  it('does not touch localStorage or sessionStorage', async () => {
    const calls = [
      vi.spyOn(Storage.prototype, 'getItem'),
      vi.spyOn(Storage.prototype, 'setItem'),
      vi.spyOn(Storage.prototype, 'removeItem'),
    ];
    const { auth, http } = setupDev();
    const done = auth.setPersona('dev-senior');
    (await vi.waitFor(() => http.expectOne('/api/me'))).flush({ id: 'b', name: 'B', roles: ['SeniorHandler'] });
    await done;
    await auth.token();
    for (const c of calls) {
      expect(c).not.toHaveBeenCalled();
      c.mockRestore();
    }
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
});

describe('AuthService in msal mode', () => {
  it('is signed in when MSAL has an account, and gets the API-scope token', async () => {
    const { auth, msal } = setupMsal();
    expect(auth.mode).toBe('msal');
    expect(auth.signedIn()).toBe(true);
    expect(await auth.token()).toBe('entra-token');
    expect(msal.acquireTokenSilent).toHaveBeenCalledWith(
      expect.objectContaining({ scopes: ['api://api-1/access_as_user'] }),
    );
  });

  it('is signed out without an account, and has no token to give', async () => {
    const { auth } = setupMsal(fakeMsal([]));
    expect(auth.signedIn()).toBe(false);
    await expect(auth.token()).rejects.toThrow();
  });

  it('signs in and out through MSAL redirects with the API scope', () => {
    const { auth, msal } = setupMsal();
    auth.signIn();
    expect(msal.loginRedirect).toHaveBeenCalledWith({ scopes: ['api://api-1/access_as_user'] });
    auth.signOut();
    expect(msal.logoutRedirect).toHaveBeenCalled();
  });

  it('ignores persona changes', async () => {
    const { auth, http } = setupMsal();
    await auth.setPersona('dev-senior');
    expect(auth.persona()).toBe('dev-handler');
    http.verify();
  });
});

describe('authInterceptor', () => {
  it('adds the dev token to /api/ requests only', async () => {
    const { http } = setupDev();
    const client = TestBed.inject(HttpClient);
    client.get('/api/claims').subscribe();
    client.get('/config.json').subscribe();
    client.get('https://example.com/api/claims').subscribe();
    const api = await vi.waitFor(() => http.expectOne('/api/claims'));
    expect(api.request.headers.get('Authorization')).toBe('Bearer dev-handler');
    for (const url of ['/config.json', 'https://example.com/api/claims']) {
      expect(http.expectOne(url).request.headers.has('Authorization')).toBe(false);
    }
  });

  it('sends the dev-senior token after the persona changes', async () => {
    const { auth, http } = setupDev();
    auth.persona.set('dev-senior');
    TestBed.inject(HttpClient).get('/api/claims').subscribe();
    const req = await vi.waitFor(() => http.expectOne('/api/claims'));
    expect(req.request.headers.get('Authorization')).toBe('Bearer dev-senior');
  });

  it('adds the MSAL access token in msal mode', async () => {
    const { http } = setupMsal();
    TestBed.inject(HttpClient).get('/api/claims').subscribe();
    const req = await vi.waitFor(() => http.expectOne('/api/claims'));
    expect(req.request.headers.get('Authorization')).toBe('Bearer entra-token');
  });

  it('sends the request without a header when no token can be acquired', async () => {
    const msal = fakeMsal();
    msal.acquireTokenSilent = vi.fn(() => throwError(() => new Error('interaction_required')));
    const { http } = setupMsal(msal);
    TestBed.inject(HttpClient).get('/api/claims').subscribe();
    const req = await vi.waitFor(() => http.expectOne('/api/claims'));
    expect(req.request.headers.has('Authorization')).toBe(false);
  });
});

describe('runtime config', () => {
  const respond = (ok: boolean, body: unknown) =>
    vi.fn(async () => ({ ok, json: async () => body }) as unknown as Response);

  it('reads config.json', async () => {
    expect(await loadRuntimeConfig(respond(true, configured))).toEqual(configured);
  });

  it('treats a 404, bad JSON or a network failure as unconfigured', async () => {
    expect(await loadRuntimeConfig(respond(false, {}))).toEqual(UNCONFIGURED);
    expect(
      await loadRuntimeConfig(vi.fn(async () => ({ ok: true, json: async () => Promise.reject(new Error('bad')) }) as unknown as Response)),
    ).toEqual(UNCONFIGURED);
    expect(await loadRuntimeConfig(vi.fn(async () => Promise.reject(new Error('offline'))))).toEqual(UNCONFIGURED);
  });

  it('is configured only when clientId, tenantId and apiScope are all set', () => {
    expect(isConfigured(configured)).toBe(true);
    expect(isConfigured(UNCONFIGURED)).toBe(false);
    expect(isConfigured({ auth: { ...configured.auth, apiScope: '' } })).toBe(false);
  });
});
