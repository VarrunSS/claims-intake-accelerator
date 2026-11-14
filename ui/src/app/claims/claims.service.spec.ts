import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ClaimsService } from './claims.service';
import { ApiError } from './models';

describe('ClaimsService', () => {
  let service: ClaimsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ClaimsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists claims through the /api prefix with the status filter', () => {
    service.list('NeedsReview').subscribe();
    const req = http.expectOne((r) => r.url === '/api/claims');
    expect(req.request.params.get('status')).toBe('NeedsReview');
    req.flush([]);
  });

  it('reads the audit trail of a claim', () => {
    let entries: unknown;
    service.audit('abc').subscribe((e) => (entries = e));
    const req = http.expectOne('/api/claims/abc/audit');
    expect(req.request.method).toBe('GET');
    req.flush([{ tool: 'lookup_policy', arguments: {}, resultSummary: 'found', durationMs: 1, timestamp: '2026-11-14T09:00:00Z', succeeded: true }]);
    expect(entries).toHaveLength(1);
  });

  it('posts no body when approving', () => {
    service.approve('abc').subscribe();
    const req = http.expectOne('/api/claims/abc/approve');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush({});
  });

  it('posts only the reason when rejecting', () => {
    service.reject('abc', 'Duplicate').subscribe();
    const req = http.expectOne('/api/claims/abc/reject');
    expect(req.request.body).toEqual({ reason: 'Duplicate' });
    req.flush({});
  });

  it('maps a 403 and a 401 to an ApiError with the status', () => {
    const seen: ApiError[] = [];
    service.approve('abc').subscribe({ error: (e: ApiError) => seen.push(e) });
    http
      .expectOne('/api/claims/abc/approve')
      .flush({ title: 'Senior review required', detail: 'Only a senior handler can approve.' }, { status: 403, statusText: 'Forbidden' });
    service.get('abc').subscribe({ error: (e: ApiError) => seen.push(e) });
    http.expectOne('/api/claims/abc').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(seen.map((e) => e.status)).toEqual([403, 401]);
    expect(seen[0].title).toBe('Senior review required');
  });

  it('maps problem JSON to an ApiError', () => {
    let error: ApiError | undefined;
    service.approve('abc').subscribe({ error: (e: ApiError) => (error = e) });
    http
      .expectOne('/api/claims/abc/approve')
      .flush({ title: 'Legacy system unavailable', detail: 'Try again.', status: 502 }, { status: 502, statusText: 'Bad Gateway' });
    expect(error).toEqual({ status: 502, title: 'Legacy system unavailable', detail: 'Try again.', errors: [] });
  });

  it('flattens validation errors and explains a network failure', () => {
    let first: ApiError | undefined;
    let second: ApiError | undefined;
    service.approve('a').subscribe({ error: (e: ApiError) => (first = e) });
    http
      .expectOne('/api/claims/a/approve')
      .flush({ title: 'Validation', errors: { reason: ['A reason is required.'] } }, { status: 400, statusText: 'Bad Request' });
    expect(first?.errors).toEqual(['A reason is required.']);

    service.get('b').subscribe({ error: (e: ApiError) => (second = e) });
    http.expectOne('/api/claims/b').error(new ProgressEvent('error'));
    expect(second?.status).toBe(0);
    expect(second?.title).toBe('Cannot reach the server');
  });
});
