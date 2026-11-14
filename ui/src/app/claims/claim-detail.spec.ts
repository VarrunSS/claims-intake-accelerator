import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ClaimDetailPage } from './claim-detail';
import { ClaimsService } from './claims.service';
import { AuthService } from '../auth/auth.service';
import { AuditEntry, ClaimAssessment, ClaimDetail } from './models';

const base: ClaimDetail = {
  id: '22222222-2222-2222-2222-222222222222',
  sourceDocumentId: 'inbox/leak.txt',
  status: 'NeedsReview',
  receivedAt: '2026-11-13T09:00:00Z',
  fields: [
    { name: 'policy_number', value: 'TM-CP-100001', confidence: 0.97, source: { documentId: 'inbox/leak.txt', page: 1, offset: 8, length: 12 } },
    { name: 'loss_date', value: null, confidence: 0, source: null },
  ],
  issues: [],
  sourcePages: ['Policy: TM-CP-100001 burst pipe'],
  decidedBy: null,
  decidedByName: null,
  decidedAt: null,
  rejectionReason: null,
  legacyReference: null,
};

const floodClause = {
  wordingId: 'TM-PW-OF-2026',
  clauseRef: '4.1',
  heading: 'Flood',
  text: 'We will not pay for loss or damage caused by flood.',
};

const assessment: ClaimAssessment = {
  assessor: 'rules',
  findings: [
    { kind: 'CoverageClause', summary: 'The loss appears to fall under clause 3.2 (Escape of water).', citations: [{ ...floodClause, clauseRef: '3.2', heading: 'Escape of water', text: 'Sudden escape of water from a pipe.' }], relatedClaimRef: null },
    { kind: 'PossibleExclusion', summary: 'The wording excludes flood: clause 4.1 may exclude this loss.', citations: [floodClause], relatedClaimRef: null },
    { kind: 'PossibleDuplicate', summary: 'An existing claim matches.', citations: [], relatedClaimRef: 'LC-000001' },
  ],
  route: 'SeniorReview',
  routeReason: 'Needs senior review: a possible exclusion, a possible duplicate.',
  notes: ['The notification period could not be read from the wording.'],
};

const trail: AuditEntry[] = [
  { tool: 'lookup_policy', arguments: { policy_number: 'TM-CP-100009' }, resultSummary: 'found; wording TM-PW-OF-2026', durationMs: 2.5, timestamp: '2026-11-14T09:00:01Z', succeeded: true },
  { tool: 'search_policy_wording', arguments: { query: '[text:44 chars]', wording_id: 'TM-PW-OF-2026', top: '5' }, resultSummary: '3 clauses: TM-PW-OF-2026/4.1', durationMs: 1.1, timestamp: '2026-11-14T09:00:02Z', succeeded: true },
  { tool: 'check_duplicate', arguments: { policy_number: 'TM-CP-100009' }, resultSummary: 'HttpRequestException', durationMs: 30, timestamp: '2026-11-14T09:00:03Z', succeeded: false },
];

async function setup(
  claim: ClaimDetail,
  approve = vi.fn(() => of({ ...claim, status: 'Submitted' as const, legacyReference: 'LC-000001' })),
  audit = vi.fn(() => of([] as AuditEntry[])),
  roles: string[] = ['ClaimsHandler'],
) {
  const auth = {
    me: signal({ id: 'oid-1', name: 'Alex Patel', roles }),
    isSenior: () => roles.includes('SeniorHandler'),
    session: signal(0),
    signIn: vi.fn(),
  };
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: AuthService, useValue: auth },
      { provide: ClaimsService, useValue: { get: () => of(claim), approve, reject: vi.fn(), audit } },
    ],
  });
  const fixture = TestBed.createComponent(ClaimDetailPage);
  fixture.componentRef.setInput('id', claim.id);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, approve, audit };
}

function button(el: HTMLElement, text: string) {
  return [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as HTMLButtonElement;
}

describe('ClaimDetailPage', () => {
  it('disables Approve and says why when the claim has errors', async () => {
    const { el } = await setup({
      ...base,
      issues: [{ code: 'MissingLossDate', severity: 'Error', field: 'loss_date', message: 'No date of loss was found.' }],
    });
    expect(button(el, 'Approve').disabled).toBe(true);
    expect(el.querySelector('#approve-hint')?.textContent).toContain('validation errors');
    expect(el.textContent).toContain('No date of loss was found.');
  });

  it('has no name input, never touches browser storage, and says who is deciding', async () => {
    const get = vi.spyOn(Storage.prototype, 'getItem');
    const set = vi.spyOn(Storage.prototype, 'setItem');
    const { el } = await setup(base);
    expect(el.querySelector('#reviewer')).toBeNull();
    expect(el.querySelector('input[type="text"]')).toBeNull();
    expect(el.querySelector('.deciding-as')?.textContent).toContain('Deciding as Alex Patel');
    expect(get).not.toHaveBeenCalled();
    expect(set).not.toHaveBeenCalled();
    get.mockRestore();
    set.mockRestore();
  });

  it('calls approve with the claim id only and shows the legacy reference', async () => {
    const { fixture, el, approve } = await setup(base);
    const approveButton = button(el, 'Approve');
    expect(approveButton.disabled).toBe(false);
    approveButton.click();
    fixture.detectChanges();
    expect(approve).toHaveBeenCalledWith(base.id);
    expect(el.textContent).toContain('LC-000001');
  });

  it('highlights the source span of the selected field', async () => {
    const { fixture, el } = await setup(base);
    button(el, 'Policy number').click();
    fixture.detectChanges();
    expect(el.querySelector('mark')?.textContent).toBe('TM-CP-100001');
  });

  it('offers a retry when the claim is approved but not yet written', async () => {
    const { el } = await setup({ ...base, status: 'Approved', decidedBy: 'oid-1', decidedByName: 'Alex Patel' });
    expect(button(el, 'Retry write')).toBeTruthy();
    expect(el.textContent).toContain('Approved by Alex Patel');
    expect(el.textContent).not.toContain('oid-1');
  });

  it('shows the decider by name, never by object id', async () => {
    const { el } = await setup({ ...base, status: 'Rejected', decidedBy: 'oid-9', decidedByName: 'Sam Jones', rejectionReason: 'Duplicate' });
    expect(el.querySelector('.decision')?.textContent).toContain('Rejected by Sam Jones');
    expect(el.textContent).not.toContain('oid-9');
  });

  it('says unknown when the decider has no display name', async () => {
    const { el } = await setup({ ...base, status: 'Rejected', decidedBy: 'oid-9', decidedByName: null, rejectionReason: 'x' });
    expect(el.querySelector('.decision')?.textContent).toContain('Rejected by unknown');
    expect(el.textContent).not.toContain('oid-9');
  });

  describe('senior review routing', () => {
    const routed: ClaimDetail = { ...base, assessment };
    const message = 'This claim is routed to senior review. Only a senior handler can approve it. You can still reject it.';

    it('disables Approve for a handler, explains why, and still allows Reject', async () => {
      const { el } = await setup(routed);
      expect(button(el, 'Approve').disabled).toBe(true);
      expect(el.querySelector('#approve-hint')?.textContent).toContain(message);
      expect(button(el, 'Reject').disabled).toBe(false);
    });

    it('enables Approve for a senior handler', async () => {
      const { el } = await setup(routed, undefined, undefined, ['SeniorHandler']);
      expect(button(el, 'Approve').disabled).toBe(false);
      expect(el.querySelector('#approve-hint')?.textContent?.trim()).toBe('');
    });
  });

  it('shows the not-signed-in message with a sign-in button on a 401', async () => {
    const { fixture, el } = await setup(base, vi.fn(() => throwError(() => ({ status: 401, title: 'Unauthorized', detail: '', errors: [] }))));
    button(el, 'Approve').click();
    fixture.detectChanges();
    const alert = el.querySelector('.decision .alert') as HTMLElement;
    expect(alert.textContent).toContain('You are not signed in or your session has expired.');
    expect(alert.querySelector('button')?.textContent).toContain('Sign in');
  });

  it('shows the problem title and detail on a 403', async () => {
    const { fixture, el } = await setup(
      base,
      vi.fn(() => throwError(() => ({ status: 403, title: 'Senior review required', detail: 'Only a senior handler can approve this claim.', errors: [] }))),
    );
    button(el, 'Approve').click();
    fixture.detectChanges();
    expect(el.querySelector('.decision .alert')?.textContent).toContain('Senior review required');
    expect(el.querySelector('.decision .alert')?.textContent).toContain('Only a senior handler');
  });

  it('labels human decision entries in the audit trail', async () => {
    const failed: AuditEntry = { tool: 'decision.submit_failed', arguments: { actor_oid: 'oid-1', role: 'ClaimsHandler', route: 'StraightToHandler' }, resultSummary: 'legacy down', durationMs: 0, timestamp: '2026-11-14T09:05:00Z', succeeded: false };
    const { el } = await setup({ ...base, assessment }, undefined, vi.fn(() => of([{ ...failed, tool: 'decision.approved', succeeded: true }, failed])));
    const rows = [...el.querySelectorAll('table.audit-table tbody tr')];
    expect(rows[0].textContent).toContain('Decision: approved');
    expect(rows[1].textContent).toContain('Decision: submit failed');
  });

  describe('coverage assessment', () => {
    const assessed: ClaimDetail = { ...base, assessment };

    it('shows the route, the reason and that it is advice only', async () => {
      const { el } = await setup(assessed);
      const section = el.querySelector('.assessment') as HTMLElement;
      expect(section.querySelector('.chip.route-SeniorReview')?.textContent).toContain('Needs senior review');
      expect(section.textContent).toContain('a possible exclusion, a possible duplicate');
      expect(section.textContent).toContain('Advice only');
    });

    it('lists each finding with its kind, and marks the ones that need attention', async () => {
      const { el } = await setup(assessed);
      const findings = [...el.querySelectorAll('li.finding')];
      expect(findings.map((f) => f.querySelector('.chip')?.textContent?.trim())).toEqual([
        'Cover',
        'Possible exclusion',
        'Possible duplicate',
      ]);
      expect(findings.map((f) => f.classList.contains('attention'))).toEqual([false, true, true]);
      expect(findings[2].textContent).toContain('existing claim LC-000001');
    });

    it('puts the cited clause text in an expandable section, closed until opened', async () => {
      const { fixture, el } = await setup(assessed);
      const citation = el.querySelectorAll('li.finding')[1].querySelector('details.citation') as HTMLDetailsElement;
      expect(citation.querySelector('summary')?.textContent).toContain('TM-PW-OF-2026 clause 4.1');
      expect(citation.querySelector('summary')?.textContent).toContain('Flood');
      expect(citation.querySelector('blockquote')?.textContent).toBe('We will not pay for loss or damage caused by flood.');
      expect(citation.open).toBe(false);
      citation.open = true;
      fixture.detectChanges();
      expect(citation.open).toBe(true);
    });

    it('shows notes about the assessment itself', async () => {
      const { el } = await setup(assessed);
      expect(el.querySelector('ul.notes')?.textContent).toContain('notification period could not be read');
    });

    it('shows nothing when the claim has no assessment, and does not ask for an audit trail', async () => {
      const { el, audit } = await setup(base);
      expect(el.querySelector('.assessment')).toBeNull();
      expect(audit).not.toHaveBeenCalled();
    });

    it('says when there are no findings', async () => {
      const { el } = await setup({ ...base, assessment: { ...assessment, findings: [], route: 'StraightToHandler', routeReason: 'Nothing found.', notes: [] } });
      expect(el.querySelector('.assessment')?.textContent).toContain('No findings.');
      expect(el.querySelector('.chip.route-StraightToHandler')?.textContent).toContain('Straight to handler');
    });

    it('loads the audit trail for the claim and shows each tool call with redacted arguments', async () => {
      const { el, audit } = await setup(assessed, undefined, vi.fn(() => of(trail)));
      expect(audit).toHaveBeenCalledWith(base.id);
      expect(el.querySelector('details.audit summary')?.textContent).toContain('3 tool calls');
      const rows = [...el.querySelectorAll('table.audit-table tbody tr')];
      expect(rows).toHaveLength(3);
      expect(rows[0].textContent).toContain('lookup_policy');
      expect(rows[0].textContent).toContain('policy_number: TM-CP-100009');
      expect(rows[1].textContent).toContain('query: [text:44 chars]');
      expect(rows[1].textContent).toContain('3 clauses: TM-PW-OF-2026/4.1');
      expect(rows[2].classList.contains('failed')).toBe(true);
      expect(rows[2].textContent).toContain('Failed: HttpRequestException');
    });

    it('still shows the assessment when the audit trail cannot be loaded', async () => {
      const { el } = await setup(assessed, undefined, vi.fn(() => throwError(() => ({ status: 500, title: 'x', detail: '', errors: [] }))));
      expect(el.querySelector('li.finding')).toBeTruthy();
      expect(el.querySelector('details.audit')?.textContent).toContain('could not be loaded');
    });

    it('renders markup in a summary or a clause as plain text', async () => {
      const hostile: ClaimAssessment = {
        ...assessment,
        findings: [{ kind: 'PossibleExclusion', summary: '<img src=x onerror=alert(1)>', citations: [{ ...floodClause, text: '<script>alert(1)</script>' }], relatedClaimRef: null }],
      };
      const { el } = await setup({ ...base, assessment: hostile });
      expect(el.querySelector('.assessment img')).toBeNull();
      expect(el.querySelector('.assessment script')).toBeNull();
      expect(el.querySelector('blockquote')?.textContent).toBe('<script>alert(1)</script>');
    });
  });
});
