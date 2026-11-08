import assert from 'node:assert/strict';
import { test } from 'node:test';
import { quotaRequestOptions, retryAfterDeadline } from './rate-limit.mjs';

test('quota requests leave 429 to the app without changing other SDK retry options', () => {
  const signal = new AbortController().signal;
  const options = {
    method: 'POST', signal,
    retryConfig: { retry: 3, statusCodesToRetry: [[429, 429], [499, 499], [500, 599]] },
  };
  assert.deepEqual(quotaRequestOptions(options), {
    method: 'POST', signal,
    retryConfig: { retry: 3, statusCodesToRetry: [[499, 499], [500, 599]] },
  });
  assert.deepEqual(options.retryConfig.statusCodesToRetry[0], [429, 429]);
});

test('retry deadline accepts seconds and HTTP dates without forwarding raw headers', () => {
  const now = Date.parse('2026-11-08T12:00:00Z');
  assert.equal(retryAfterDeadline(new Headers({ 'Retry-After': '3600' }), now), now + 3_600_000);
  assert.equal(retryAfterDeadline({ 'retry-after': 'Sun, 08 Nov 2026 14:00:00 GMT' }, now), now + 7_200_000);
  for (const value of ['', '0', '-5', '1.5', 'Sun, 08 Nov 2026 11:00:00 GMT', '9999999999999999999', 'invalid'])
    assert.equal(retryAfterDeadline({ 'retry-after': value }, now), null);
  assert.equal(retryAfterDeadline(undefined, now), null);
});
