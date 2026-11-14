import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ClaimDetailPage } from './claim-detail';
import { ClaimsService } from './claims.service';
import { ClaimDetail } from './models';

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
  decidedAt: null,
  rejectionReason: null,
  legacyReference: null,
};

async function setup(claim: ClaimDetail, approve = vi.fn(() => of({ ...claim, status: 'Submitted' as const, legacyReference: 'LC-000001' }))) {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [provideRouter([]), { provide: ClaimsService, useValue: { get: () => of(claim), approve, reject: vi.fn() } }],
  });
  const fixture = TestBed.createComponent(ClaimDetailPage);
  fixture.componentRef.setInput('id', claim.id);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, el: fixture.nativeElement as HTMLElement, approve };
}

function button(el: HTMLElement, text: string) {
  return [...el.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as HTMLButtonElement;
}

function typeName(el: HTMLElement, name: string) {
  const input = el.querySelector('#reviewer') as HTMLInputElement;
  input.value = name;
  input.dispatchEvent(new Event('input'));
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

  it('calls approve with the reviewer name and shows the legacy reference', async () => {
    const { fixture, el, approve } = await setup(base);
    typeName(el, 'a.patel');
    fixture.detectChanges();
    const approveButton = button(el, 'Approve');
    expect(approveButton.disabled).toBe(false);
    approveButton.click();
    fixture.detectChanges();
    expect(approve).toHaveBeenCalledWith(base.id, 'a.patel');
    expect(el.textContent).toContain('LC-000001');
  });

  it('highlights the source span of the selected field', async () => {
    const { fixture, el } = await setup(base);
    button(el, 'Policy number').click();
    fixture.detectChanges();
    expect(el.querySelector('mark')?.textContent).toBe('TM-CP-100001');
  });

  it('offers a retry when the claim is approved but not yet written', async () => {
    const { el } = await setup({ ...base, status: 'Approved', decidedBy: 'a.patel' });
    expect(button(el, 'Retry write')).toBeTruthy();
  });
});
