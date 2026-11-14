import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ClaimList } from './claim-list';
import { ClaimsService } from './claims.service';
import { ClaimSummary } from './models';

const summary: ClaimSummary = {
  id: '11111111-1111-1111-1111-111111111111',
  sourceDocumentId: 'inbox/fire.pdf',
  policyNumber: 'TM-CP-100001',
  claimantName: 'Brindle & Marsh Joinery Ltd',
  status: 'NeedsReview',
  receivedAt: '2026-11-13T09:00:00Z',
  errorCount: 1,
  warningCount: 2,
  minConfidence: 0.55,
  legacyReference: null,
};

function setup(list: ReturnType<ClaimsService['list']>) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), { provide: ClaimsService, useValue: { list: () => list } }],
  });
  const fixture = TestBed.createComponent(ClaimList);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('ClaimList', () => {
  it('renders a row with status, confidence label and issue counts', async () => {
    const el = setup(of([summary]));
    await Promise.resolve();
    const text = el.querySelector('tbody tr')?.textContent ?? '';
    expect(text).toContain('inbox/fire.pdf');
    expect(text).toContain('Needs review');
    expect(text).toContain('Low 55%');
    expect(text).toContain('1 error');
    expect(text).toContain('2 warnings');
    expect(el.querySelector('tbody a')?.getAttribute('href')).toBe(`/claims/${summary.id}`);
  });

  it('shows an empty state', () => {
    const el = setup(of([]));
    expect(el.querySelector('.empty')?.textContent).toContain('No claims match');
    expect(el.querySelector('table')).toBeNull();
  });

  it('shows an error alert when loading fails', () => {
    const el = setup(throwError(() => ({ status: 0, title: 'Cannot reach the server', detail: 'Check it.', errors: [] })));
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Cannot reach the server');
  });
});
