export type ClaimStatus = 'NeedsReview' | 'Approved' | 'Rejected' | 'Submitted';

export interface SourceSpan {
  documentId: string;
  page: number;
  offset: number;
  length: number;
}

export interface ClaimSummary {
  id: string;
  sourceDocumentId: string;
  policyNumber: string | null;
  claimantName: string | null;
  status: ClaimStatus;
  receivedAt: string;
  errorCount: number;
  warningCount: number;
  minConfidence: number;
  legacyReference: string | null;
}

export interface ClaimField {
  name: string;
  value: string | null;
  confidence: number;
  source: SourceSpan | null;
}

export interface ClaimIssue {
  code: string;
  severity: 'Warning' | 'Error';
  field: string | null;
  message: string;
}

export interface ClaimDetail {
  id: string;
  sourceDocumentId: string;
  status: ClaimStatus;
  receivedAt: string;
  fields: ClaimField[];
  issues: ClaimIssue[];
  sourcePages: string[];
  decidedBy: string | null;
  decidedAt: string | null;
  rejectionReason: string | null;
  legacyReference: string | null;
}

/** A failed API call, reduced to what the handler needs to see. */
export interface ApiError {
  status: number;
  title: string;
  detail: string;
  errors: string[];
}
