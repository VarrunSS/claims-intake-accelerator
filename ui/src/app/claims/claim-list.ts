import { DatePipe } from '@angular/common';
import { Component, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { ClaimsService } from './claims.service';
import { confidenceBand, confidenceLabel } from './helpers';
import { ApiError, ClaimSummary } from './models';

@Component({
  selector: 'app-claim-list',
  imports: [RouterLink, DatePipe],
  template: `
    <div class="toolbar">
      <h1>Claims</h1>
      <div class="controls">
        <label for="status-filter">Show</label>
        <select id="status-filter" #filter (change)="load(filter.value)">
          @for (o of options; track o.value) {
            <option [value]="o.value" [selected]="o.value === status()">{{ o.label }}</option>
          }
        </select>
        <button type="button" (click)="load(status())">Refresh</button>
      </div>
    </div>

    <p class="status" role="status" aria-live="polite">
      @if (loading()) {
        Loading claims…
      } @else if (!error()) {
        {{ claims().length }} claim{{ claims().length === 1 ? '' : 's' }} shown.
      }
    </p>

    @if (error(); as e) {
      <div class="alert" role="alert"><strong>{{ e.title }}.</strong> {{ e.detail }}</div>
    }

    @if (!loading() && !error()) {
      @if (claims().length === 0) {
        <p class="empty">No claims match this filter. Drop a document into the inbox and refresh.</p>
      } @else {
        <table>
          <caption class="visually-hidden">Claims</caption>
          <thead>
            <tr>
              <th scope="col">Document</th>
              <th scope="col">Claimant and policy</th>
              <th scope="col">Status</th>
              <th scope="col">Confidence</th>
              <th scope="col">Issues</th>
              <th scope="col">Received</th>
            </tr>
          </thead>
          <tbody>
            @for (c of claims(); track c.id) {
              <tr>
                <th scope="row"><a [routerLink]="['/claims', c.id]">{{ c.sourceDocumentId }}</a></th>
                <td>
                  {{ c.claimantName ?? 'Unknown claimant' }}<br />
                  <span class="muted">{{ c.policyNumber ?? 'No policy number' }}</span>
                </td>
                <td><span class="chip status-{{ c.status }}">{{ statusText(c) }}</span></td>
                <td><span class="badge band-{{ band(c) }}">{{ label(c) }}</span></td>
                <td>
                  @if (c.errorCount + c.warningCount === 0) {
                    None
                  } @else {
                    @if (c.errorCount > 0) {
                      <span class="issue-error">{{ c.errorCount }} error{{ c.errorCount === 1 ? '' : 's' }}</span>
                    }
                    @if (c.warningCount > 0) {
                      <span class="issue-warning">{{ c.warningCount }} warning{{ c.warningCount === 1 ? '' : 's' }}</span>
                    }
                  }
                </td>
                <td>{{ c.receivedAt | date: 'd MMM y, HH:mm' }}</td>
              </tr>
            }
          </tbody>
        </table>
      }
    }
  `,
})
export class ClaimList {
  private readonly service = inject(ClaimsService);
  private readonly auth = inject(AuthService);

  protected readonly options = [
    { value: 'all', label: 'All claims' },
    { value: 'NeedsReview', label: 'Needs review' },
    { value: 'Approved', label: 'Approved, write pending' },
    { value: 'Submitted', label: 'Submitted' },
    { value: 'Rejected', label: 'Rejected' },
  ];

  protected readonly claims = signal<ClaimSummary[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly status = signal('all');

  constructor() {
    // Runs once, then again whenever the signed-in identity changes.
    effect(() => {
      this.auth.session();
      untracked(() => this.load(this.status()));
    });
  }

  protected load(status: string): void {
    this.status.set(status);
    this.loading.set(true);
    this.error.set(null);
    this.service.list(status).subscribe({
      next: (claims) => {
        this.claims.set(claims);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.claims.set([]);
        this.error.set(e);
        this.loading.set(false);
      },
    });
  }

  protected band(c: ClaimSummary) {
    return confidenceBand(c.minConfidence);
  }

  protected label(c: ClaimSummary) {
    return confidenceLabel(c.minConfidence);
  }

  protected statusText(c: ClaimSummary) {
    if (c.status === 'NeedsReview') return 'Needs review';
    if (c.status === 'Approved') return 'Approved, write pending';
    return c.status;
  }
}
