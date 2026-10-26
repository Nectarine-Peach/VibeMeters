import assert from 'node:assert/strict';
import { test } from 'node:test';
import { accountFailure } from './account-status.mjs';

const rejected = [{ tierId: 'free-tier', reasonCode: 'UNSUPPORTED_CLIENT' }];
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
