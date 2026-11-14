import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import { ApiError, ClaimDetail, ClaimSummary } from './models';

/** Turns an HTTP failure (RFC 7807 problem JSON, or no response at all) into an ApiError. */
export function toApiError(error: unknown): ApiError {
  if (!(error instanceof HttpErrorResponse)) {
    return { status: 0, title: 'Unexpected error', detail: 'Something went wrong in the browser.', errors: [] };
  }
  if (error.status === 0) {
    return {
      status: 0,
      title: 'Cannot reach the server',
      detail: 'The review API did not respond. Check that it is running and try again.',
      errors: [],
    };
  }
  const body = error.error as { title?: string; detail?: string; errors?: Record<string, string[]> } | null;
  const errors = body?.errors ? Object.values(body.errors).flat() : [];
  return {
    status: error.status,
    title: body?.title ?? error.statusText ?? 'Request failed',
    detail: body?.detail ?? (errors.length ? '' : `The server returned ${error.status}.`),
    errors,
  };
}

@Injectable({ providedIn: 'root' })
export class ClaimsService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/claims';

  list(status = 'all'): Observable<ClaimSummary[]> {
    return this.http.get<ClaimSummary[]>(this.base, { params: { status } }).pipe(this.mapError());
  }

  get(id: string): Observable<ClaimDetail> {
    return this.http.get<ClaimDetail>(`${this.base}/${encodeURIComponent(id)}`).pipe(this.mapError());
  }

  approve(id: string, reviewer: string): Observable<ClaimDetail> {
    return this.http
      .post<ClaimDetail>(`${this.base}/${encodeURIComponent(id)}/approve`, { reviewer })
      .pipe(this.mapError());
  }

  reject(id: string, reviewer: string, reason: string): Observable<ClaimDetail> {
    return this.http
      .post<ClaimDetail>(`${this.base}/${encodeURIComponent(id)}/reject`, { reviewer, reason })
      .pipe(this.mapError());
  }

  private mapError<T>() {
    return catchError<T, Observable<never>>((e) => throwError(() => toApiError(e)));
  }
}
