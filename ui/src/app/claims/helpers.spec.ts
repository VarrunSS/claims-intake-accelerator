import {
  confidenceBand,
  confidenceLabel,
  fieldLabel,
  findingLabel,
  findingNeedsAttention,
  formatArguments,
  routeLabel,
  segmentPage,
} from './helpers';

describe('confidenceBand', () => {
  it('uses 0.90 and 0.80 as the boundaries', () => {
    expect(confidenceBand(0.95)).toBe('high');
    expect(confidenceBand(0.9)).toBe('high');
    expect(confidenceBand(0.89)).toBe('medium');
    expect(confidenceBand(0.8)).toBe('medium');
    expect(confidenceBand(0.79)).toBe('low');
    expect(confidenceBand(0)).toBe('low');
  });

  it('labels with words as well as a percentage', () => {
    expect(confidenceLabel(0.55)).toBe('Low 55%');
  });
});

describe('fieldLabel', () => {
  it('knows the claim fields and tidies unknown ones', () => {
    expect(fieldLabel('loss_date')).toBe('Date of loss');
    expect(fieldLabel('some_new_field')).toBe('Some new field');
  });
});

describe('segmentPage', () => {
  it('returns one plain segment when there are no spans', () => {
    expect(segmentPage('hello', [])).toEqual([{ text: 'hello', marked: false }]);
  });

  it('marks the span and keeps the text around it', () => {
    expect(segmentPage('Policy TM-1 here', [{ offset: 7, length: 4 }])).toEqual([
      { text: 'Policy ', marked: false },
      { text: 'TM-1', marked: true },
      { text: ' here', marked: false },
    ]);
  });

  it('clamps a span that runs past the end', () => {
    expect(segmentPage('abcdef', [{ offset: 4, length: 50 }])).toEqual([
      { text: 'abcd', marked: false },
      { text: 'ef', marked: true },
    ]);
  });

  it('ignores spans that are negative, empty or entirely outside the text', () => {
    expect(segmentPage('abc', [{ offset: 10, length: 2 }, { offset: 1, length: 0 }])).toEqual([
      { text: 'abc', marked: false },
    ]);
    expect(segmentPage('abc', [{ offset: -2, length: 3 }])).toEqual([
      { text: 'a', marked: true },
      { text: 'bc', marked: false },
    ]);
  });

  it('skips overlapping spans instead of duplicating text', () => {
    const joined = segmentPage('abcdef', [{ offset: 0, length: 4 }, { offset: 2, length: 3 }])
      .map((s) => s.text)
      .join('');
    expect(joined).toBe('abcdef');
  });

  it('treats markup in the text as plain text', () => {
    expect(segmentPage('<b>x</b>', [{ offset: 0, length: 3 }])[0]).toEqual({ text: '<b>', marked: true });
  });
});

describe('assessment helpers', () => {
  it('names each kind of finding and falls back to the raw kind', () => {
    expect(findingLabel('PossibleExclusion')).toBe('Possible exclusion');
    expect(findingLabel('NotificationBreach')).toBe('Late notification');
    expect(findingLabel('PossibleDuplicate')).toBe('Possible duplicate');
    expect(findingLabel('CoverageClause')).toBe('Cover');
    expect(findingLabel('Something new')).toBe('Something new');
  });

  it('treats everything except a cover clause as needing attention', () => {
    expect(findingNeedsAttention('CoverageClause')).toBe(false);
    expect(findingNeedsAttention('PossibleExclusion')).toBe(true);
    expect(findingNeedsAttention('PossibleDuplicate')).toBe(true);
  });

  it('labels the route in words', () => {
    expect(routeLabel('SeniorReview')).toBe('Needs senior review');
    expect(routeLabel('StraightToHandler')).toBe('Straight to handler');
  });

  it('formats redacted audit arguments on one line', () => {
    expect(formatArguments({ policy_number: 'TM-CP-100001', query: '[text:12 chars]' })).toBe(
      'policy_number: TM-CP-100001, query: [text:12 chars]',
    );
    expect(formatArguments({})).toBe('');
  });
});
