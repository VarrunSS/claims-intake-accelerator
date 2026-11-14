export type ConfidenceBand = 'high' | 'medium' | 'low';

/** High from 0.90, medium from 0.80 (the API's low-confidence threshold), otherwise low. */
export function confidenceBand(confidence: number): ConfidenceBand {
  if (confidence >= 0.9) return 'high';
  if (confidence >= 0.8) return 'medium';
  return 'low';
}

export function confidenceLabel(confidence: number): string {
  const band = confidenceBand(confidence);
  return `${band[0].toUpperCase()}${band.slice(1)} ${Math.round(confidence * 100)}%`;
}

const LABELS: Record<string, string> = {
  policy_number: 'Policy number',
  claimant_name: 'Claimant',
  loss_date: 'Date of loss',
  loss_description: 'Description of loss',
  estimated_amount_gbp: 'Estimated amount (GBP)',
};

export function fieldLabel(name: string): string {
  return LABELS[name] ?? name.replace(/_/g, ' ').replace(/^./, (c) => c.toUpperCase());
}

export interface Segment {
  text: string;
  marked: boolean;
}

/**
 * Splits page text into plain and highlighted pieces. Spans are clamped to the text, empty spans are
 * dropped and overlapping spans are skipped, so bad data from the API cannot break the page.
 * Returns plain data; the template renders it with interpolation, never innerHTML.
 */
export function segmentPage(text: string, spans: { offset: number; length: number }[]): Segment[] {
  const ranges = spans
    .map((s) => {
      const start = Math.min(Math.max(Math.trunc(s.offset), 0), text.length);
      const end = Math.min(Math.max(Math.trunc(s.offset + s.length), start), text.length);
      return { start, end };
    })
    .filter((r) => r.end > r.start)
    .sort((a, b) => a.start - b.start);

  const segments: Segment[] = [];
  let cursor = 0;
  for (const r of ranges) {
    if (r.start < cursor) continue;
    if (r.start > cursor) segments.push({ text: text.slice(cursor, r.start), marked: false });
    segments.push({ text: text.slice(r.start, r.end), marked: true });
    cursor = r.end;
  }
  if (cursor < text.length || segments.length === 0) {
    segments.push({ text: text.slice(cursor), marked: false });
  }
  return segments;
}

const FINDING_LABELS: Record<string, string> = {
  CoverageClause: 'Cover',
  PossibleExclusion: 'Possible exclusion',
  NotificationBreach: 'Late notification',
  PossibleDuplicate: 'Possible duplicate',
};

export function findingLabel(kind: string): string {
  return FINDING_LABELS[kind] ?? kind;
}

/** Findings that should make a handler stop and look: everything except a plain cover clause. */
export function findingNeedsAttention(kind: string): boolean {
  return kind !== 'CoverageClause';
}

export function routeLabel(route: string): string {
  return route === 'SeniorReview' ? 'Needs senior review' : 'Straight to handler';
}

/** A short, readable form of a redacted audit argument list, for example "policy_number: TM-CP-100001". */
export function formatArguments(args: Record<string, string>): string {
  return Object.entries(args)
    .map(([k, v]) => `${k}: ${v}`)
    .join(', ');
}
