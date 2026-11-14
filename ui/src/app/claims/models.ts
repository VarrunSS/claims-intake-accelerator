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

export type FindingKind = 'CoverageClause' | 'PossibleExclusion' | 'NotificationBreach' | 'PossibleDuplicate';

export type ReviewRoute = 'StraightToHandler' | 'SeniorReview';

/** A clause of a policy wording, as the wording search returned it. */
export interface ClauseCitation {
  wordingId: string;
  clauseRef: string;
  heading: string;
  text: string;
}

export interface CoverageFinding {
  kind: FindingKind;
  summary: string;
  citations: ClauseCitation[];
  relatedClaimRef: string | null;
}

/** Advice from the assessor. A handler decides. */
export interface ClaimAssessment {
  assessor: string;
  findings: CoverageFinding[];
  route: ReviewRoute;
  routeReason: string;
  notes: string[];
}

/** One tool call made while the claim was assessed. Arguments are redacted by the API. */
export interface AuditEntry {
  tool: string;
  arguments: Record<string, string>;
  resultSummary: string;
  durationMs: number;
  timestamp: string;
  succeeded: boolean;
}

export interface ClaimDetail {
  id: string;
  sourceDocumentId: string;
  status: ClaimStatus;
  receivedAt: string;
  fields: ClaimField[];
  issues: ClaimIssue[];
  sourcePages: string[];
  /** The decider's stable object id. Not for display; show decidedByName. */
  decidedBy: string | null;
  decidedByName: string | null;
  decidedAt: string | null;
  rejectionReason: string | null;
  legacyReference: string | null;
  assessment?: ClaimAssessment | null;
}

/** A failed API call, reduced to what the handler needs to see. */
export interface ApiError {
  status: number;
  title: string;
  detail: string;
  errors: string[];
}
