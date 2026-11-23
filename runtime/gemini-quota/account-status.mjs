// A free-tier rejection must not override an active business tier/project.
export function accountFailure(account, configuredProject) {
  const project = account?.cloudaicompanionProject || configuredProject;
  if (typeof project === 'string' && project) return null;
  if (!account?.currentTier && !account?.paidTier && account?.ineligibleTiers?.some(
    tier => tier.tierId === 'free-tier' && tier.reasonCode === 'UNSUPPORTED_CLIENT'))
    return 'UNSUPPORTED_CLIENT';
  return 'PROJECT_REQUIRED';
}

export function requestFailure(error) {
  const status = Number(error?.response?.status ?? error?.status ?? error?.code);
  if (status === 429) return 'RATE_LIMITED';
  if (status === 403) return 'ACCESS_DENIED';
  if (status === 401 || error?.code === 'AUTH_REQUIRED' || error?.response?.data?.error === 'invalid_grant')
    return 'AUTH_REQUIRED';
  return error?.code === 'ERR_MODULE_NOT_FOUND' ? 'RUNTIME_REQUIRED' : 'NETWORK';
}
