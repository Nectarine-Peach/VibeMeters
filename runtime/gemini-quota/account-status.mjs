// A free-tier rejection must not override an active business tier/project.
export function accountFailure(account, configuredProject) {
  const project = account?.cloudaicompanionProject || configuredProject;
  if (typeof project === 'string' && project) return null;
  if (!account?.currentTier && !account?.paidTier && account?.ineligibleTiers?.some(
    tier => tier.tierId === 'free-tier' && tier.reasonCode === 'UNSUPPORTED_CLIENT'))
    return 'UNSUPPORTED_CLIENT';
  return 'PROJECT_REQUIRED';
}
