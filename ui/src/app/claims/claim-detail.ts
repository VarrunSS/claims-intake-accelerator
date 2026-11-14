import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { ClaimsService } from './claims.service';
import {
  confidenceBand,
  confidenceLabel,
  fieldLabel,
  findingLabel,
  findingNeedsAttention,
  formatArguments,
  routeLabel,
  segmentPage,
  toolLabel,
} from './helpers';
import { ApiError, AuditEntry, ClaimDetail, ClaimField, CoverageFinding } from './models';

@Component({
  selector: 'app-claim-detail',
  imports: [FormsModule, RouterLink, DatePipe],
  template: `
    <p><a routerLink="/claims">&larr; Back to claims</a></p>

    @if (loadError(); as e) {
      @if (e.status === 401) {
        <div class="alert" role="alert">
          {{ notSignedIn }}
          <button type="button" (click)="auth.signIn()">Sign in</button>
        </div>
      } @else {
        <div class="alert" role="alert"><strong>{{ e.title }}.</strong> {{ e.detail }}</div>
      }
    }

    @if (claim(); as c) {
      <h1>{{ c.sourceDocumentId }}</h1>
      <p class="muted">
        Received {{ c.receivedAt | date: 'd MMM y, HH:mm' }} &middot;
        <span class="chip status-{{ c.status }}">{{ c.status === 'NeedsReview' ? 'Needs review' : c.status }}</span>
        @if (c.legacyReference) {
          &middot; Legacy reference <strong>{{ c.legacyReference }}</strong>
        }
      </p>

      <div class="columns">
        <section aria-labelledby="source-heading" class="panel">
          <h2 id="source-heading">Source text</h2>
          @for (page of c.sourcePages; track $index) {
            <h3>Page {{ $index + 1 }}</h3>
            <pre class="source" tabindex="0" [attr.aria-label]="'Source text, page ' + ($index + 1)">@for (s of segments($index); track $index) {@if (s.marked) {<mark>{{ s.text }}</mark>} @else {<span>{{ s.text }}</span>}}</pre>
          } @empty {
            <p class="muted">No source text is available for this claim.</p>
          }
        </section>

        <section aria-labelledby="fields-heading" class="panel">
          <h2 id="fields-heading">Extracted fields</h2>
          <p class="muted">Select a field to highlight where it was found.</p>
          <ul class="fields">
            @for (f of c.fields; track f.name) {
              <li>
                <button
                  type="button"
                  class="field"
                  [class.active]="selected()?.name === f.name"
                  [attr.aria-pressed]="selected()?.name === f.name"
                  (click)="select(f)"
                  (focus)="select(f)"
                >
                  <span class="field-name">{{ label(f.name) }}</span>
                  <span class="field-value">{{ f.value ?? 'Not found' }}</span>
                  <span class="badge band-{{ band(f) }}">{{ conf(f) }}</span>
                </button>
              </li>
            }
          </ul>

          <h2>Validation issues</h2>
          @if (c.issues.length === 0) {
            <p>No issues found.</p>
          } @else {
            <ul class="issues">
              @for (i of c.issues; track $index) {
                <li class="issue-{{ i.severity }}">
                  <strong>{{ i.severity }}</strong>
                  @if (i.field) {
                    ({{ label(i.field) }})
                  }
                  {{ i.message }}
                </li>
              }
            </ul>
          }
        </section>
      </div>

      @if (c.assessment; as a) {
        <section aria-labelledby="assessment-heading" class="panel assessment">
          <h2 id="assessment-heading">Coverage assessment</h2>
          <p>
            <span class="chip route-{{ a.route }}">{{ routeText(a.route) }}</span>
            {{ a.routeReason }}
          </p>
          <p class="muted">
            Advice only, from the {{ a.assessor }} assessor. It points at clauses of the policy wording; the decision is
            yours.
          </p>

          @if (a.findings.length === 0) {
            <p>No findings.</p>
          } @else {
            <ul class="findings">
              @for (f of a.findings; track $index) {
                <li class="finding" [class.attention]="needsAttention(f)">
                  <span class="chip finding-{{ f.kind }}">{{ findingText(f) }}</span>
                  {{ f.summary }}
                  @if (f.relatedClaimRef) {
                    <span class="muted">(existing claim {{ f.relatedClaimRef }})</span>
                  }
                  @for (cite of f.citations; track cite.wordingId + cite.clauseRef) {
                    <details class="citation">
                      <summary>
                        <span class="clause-ref">{{ cite.wordingId }} clause {{ cite.clauseRef }}</span>
                        {{ cite.heading }}
                      </summary>
                      <blockquote>{{ cite.text }}</blockquote>
                    </details>
                  }
                </li>
              }
            </ul>
          }

          @if (a.notes.length > 0) {
            <h3>About this assessment</h3>
            <ul class="notes">
              @for (n of a.notes; track $index) {
                <li>{{ n }}</li>
              }
            </ul>
          }

          <details class="audit">
            <summary>Audit trail ({{ audit().length }} tool calls)</summary>
            @if (auditError()) {
              <p class="muted">The audit trail could not be loaded.</p>
            } @else if (audit().length === 0) {
              <p class="muted">No tool calls were recorded for this claim.</p>
            } @else {
              <table class="audit-table">
                <caption class="muted">
                  Every tool call made while assessing this claim. Arguments are reduced to ids and lengths.
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Time</th>
                    <th scope="col">Tool</th>
                    <th scope="col">Arguments</th>
                    <th scope="col">Result</th>
                    <th scope="col">Duration</th>
                  </tr>
                </thead>
                <tbody>
                  @for (e of audit(); track $index) {
                    <tr [class.failed]="!e.succeeded">
                      <td>{{ e.timestamp | date: 'HH:mm:ss' }}</td>
                      <td>
                        <code>{{ toolText(e.tool) }}</code>
                      </td>
                      <td>{{ args(e) }}</td>
                      <td>{{ e.succeeded ? e.resultSummary : 'Failed: ' + e.resultSummary }}</td>
                      <td>{{ e.durationMs }} ms</td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </details>
        </section>
      }

      <section aria-labelledby="decision-heading" class="panel decision">
        <h2 id="decision-heading">Decision</h2>

        @if (c.status === 'Rejected') {
          <p>Rejected by {{ c.decidedByName ?? 'unknown' }}. Reason: {{ c.rejectionReason }}</p>
        } @else if (c.status === 'Submitted') {
          <p>Approved by {{ c.decidedByName ?? 'unknown' }} and written to the claims system as <strong>{{ c.legacyReference }}</strong>.</p>
        } @else {
          <p class="deciding-as">Deciding as <strong>{{ auth.me()?.name ?? 'unknown user' }}</strong></p>

          @if (c.status === 'NeedsReview') {
            <label for="reason">Reason for rejecting (required to reject)</label>
            <textarea id="reason" rows="3" maxlength="500" [(ngModel)]="reason"></textarea>
          } @else {
            <p>
              Approved by {{ c.decidedByName ?? 'unknown' }}, but the write to the claims system did not complete. Retrying is safe: it
              cannot create a duplicate claim.
            </p>
          }

          <div class="actions">
            <button
              type="button"
              class="primary"
              [disabled]="!!approveBlocker(c) || busy()"
              aria-describedby="approve-hint"
              (click)="approve()"
            >
              {{ c.status === 'Approved' ? 'Retry write to claims system' : 'Approve' }}
            </button>
            @if (c.status === 'NeedsReview') {
              <button type="button" class="danger" [disabled]="busy()" (click)="reject()">Reject</button>
            }
          </div>
          <p id="approve-hint" class="muted">{{ approveBlocker(c) }}</p>
        }

        <p class="status" role="status" aria-live="polite">{{ message() }}</p>
        @if (actionError(); as e) {
          <div class="alert" role="alert">
            @if (e.status === 401) {
              {{ notSignedIn }}
              <button type="button" (click)="auth.signIn()">Sign in</button>
            } @else {
              <strong>{{ e.title }}.</strong> {{ e.detail }}
              @for (m of e.errors; track $index) {
                <div>{{ m }}</div>
              }
            }
          </div>
        }
      </section>
    }
  `,
})
export class ClaimDetailPage {
  private readonly service = inject(ClaimsService);
  protected readonly auth = inject(AuthService);
  protected readonly notSignedIn = 'You are not signed in or your session has expired.';

  /** Bound from the route parameter. */
  readonly id = input.required<string>();

  protected readonly claim = signal<ClaimDetail | null>(null);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly actionError = signal<ApiError | null>(null);
  protected readonly message = signal('');
  protected readonly busy = signal(false);
  protected readonly selected = signal<ClaimField | null>(null);
  protected readonly audit = signal<AuditEntry[]>([]);
  protected readonly auditError = signal(false);
  protected reason = '';

  private readonly activeSpans = computed(() => {
    const source = this.selected()?.source;
    return source ? [source] : [];
  });

  constructor() {
    effect(() => {
      // Fetch again when the signed-in identity changes (dev persona switch).
      this.auth.session();
      this.service.get(this.id()).subscribe({
        next: (c) => {
          this.claim.set(c);
          this.loadError.set(null);
          this.loadAudit(c);
        },
        error: (e: ApiError) => this.loadError.set(e),
      });
    });
  }

  /** The audit trail is secondary: if it cannot be loaded, the rest of the page still works. */
  private loadAudit(c: ClaimDetail): void {
    if (!c.assessment) {
      this.audit.set([]);
      return;
    }
    this.service.audit(c.id).subscribe({
      next: (entries) => {
        this.audit.set(entries);
        this.auditError.set(false);
      },
      error: () => this.auditError.set(true),
    });
  }

  protected routeText(route: string) {
    return routeLabel(route);
  }

  protected findingText(f: CoverageFinding) {
    return findingLabel(f.kind);
  }

  protected needsAttention(f: CoverageFinding) {
    return findingNeedsAttention(f.kind);
  }

  protected toolText(tool: string) {
    return toolLabel(tool);
  }

  protected args(e: AuditEntry) {
    return formatArguments(e.arguments);
  }

  protected segments(pageIndex: number) {
    const text = this.claim()?.sourcePages[pageIndex] ?? '';
    const spans = this.activeSpans().filter((s) => s.page === pageIndex + 1);
    return segmentPage(text, spans);
  }

  protected label(name: string) {
    return fieldLabel(name);
  }

  protected band(f: ClaimField) {
    return confidenceBand(f.confidence);
  }

  protected conf(f: ClaimField) {
    return confidenceLabel(f.confidence);
  }

  protected select(f: ClaimField) {
    this.selected.set(f);
  }

  /** Why Approve is unavailable, or an empty string when it is available. */
  protected approveBlocker(c: ClaimDetail): string {
    if (c.status !== 'NeedsReview' && c.status !== 'Approved') return 'This claim has already been decided.';
    if (c.issues.some((i) => i.severity === 'Error')) {
      return 'Approval is blocked because the claim has validation errors. Reject it instead.';
    }
    if (c.assessment?.route === 'SeniorReview' && !this.auth.isSenior()) {
      return 'This claim is routed to senior review. Only a senior handler can approve it. You can still reject it.';
    }
    return '';
  }

  protected approve(): void {
    const c = this.claim();
    if (!c || this.approveBlocker(c)) return;
    this.run(this.service.approve(c.id), 'Claim approved and written to the claims system.');
  }

  protected reject(): void {
    const c = this.claim();
    if (!c) return;
    if (!this.reason.trim()) {
      this.actionError.set({ status: 0, title: 'Reason needed', detail: 'Give a reason for rejecting this claim.', errors: [] });
      return;
    }
    this.run(this.service.reject(c.id, this.reason.trim()), 'Claim rejected.');
  }

  private run(call: Observable<ClaimDetail>, success: string): void {
    this.busy.set(true);
    this.actionError.set(null);
    this.message.set('');
    call.subscribe({
      next: (c) => {
        this.claim.set(c);
        this.busy.set(false);
        this.message.set(success);
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.actionError.set(e);
        // A 502 leaves the claim Approved, so reload to show the Retry button.
        if (e.status === 502 || e.status === 409) {
          this.service.get(this.id()).subscribe({ next: (c) => this.claim.set(c), error: () => undefined });
        }
      },
    });
  }
}
