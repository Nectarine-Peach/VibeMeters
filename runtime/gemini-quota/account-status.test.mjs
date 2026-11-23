import assert from 'node:assert/strict';
import { test } from 'node:test';
import { accountFailure, requestFailure } from './account-status.mjs';

const rejected = [{ tierId: 'free-tier', reasonCode: 'UNSUPPORTED_CLIENT' }];
test('quota requests distinguish expired authorization, project access denial and throttling', () => {
  assert.equal(requestFailure({ response: { status: 400, data: { error: 'invalid_grant' } } }), 'AUTH_REQUIRED');
  assert.equal(requestFailure({ code: 'AUTH_REQUIRED' }), 'AUTH_REQUIRED');
  assert.equal(requestFailure({ response: { status: 401 } }), 'AUTH_REQUIRED');
  assert.equal(requestFailure({ response: { status: 403 } }), 'ACCESS_DENIED');
  assert.equal(requestFailure({ response: { status: 429 } }), 'RATE_LIMITED');
  assert.equal(requestFailure({ response: { status: 503 } }), 'NETWORK');
});
test('unsupported personal client selects Antigravity', () => {
  assert.equal(accountFailure({ ineligibleTiers: rejected }), 'UNSUPPORTED_CLIENT');
});
test('active business project is preserved alongside free-tier ineligibility', () => {
  assert.equal(accountFailure({ currentTier: { id: 'standard-tier' },
    cloudaicompanionProject: 'business-project', ineligibleTiers: rejected }), null);
});
test('configured business project is preserved', () => {
  assert.equal(accountFailure({ ineligibleTiers: rejected }, 'configured-project'), null);
});
test('active business tier missing project does not switch products', () => {
  assert.equal(accountFailure({ currentTier: { id: 'standard-tier' }, ineligibleTiers: rejected }), 'PROJECT_REQUIRED');
  assert.equal(accountFailure({ paidTier: { id: 'paid-tier' }, ineligibleTiers: rejected }), 'PROJECT_REQUIRED');
});
test('other ineligibility is not mislabeled as unsupported client', () => {
  assert.equal(accountFailure({ ineligibleTiers: [{ tierId: 'free-tier', reasonCode: 'INELIGIBLE_ACCOUNT' }] }), 'PROJECT_REQUIRED');
});
