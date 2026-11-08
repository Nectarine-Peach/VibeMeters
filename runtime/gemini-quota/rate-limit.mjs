// Code Assist configures 429 as a separate range. Leave the rest of the official client's
// retry configuration intact and let VibeMeters enforce the server's cooldown.
export function quotaRequestOptions(options) {
  const ranges = options.retryConfig?.statusCodesToRetry;
  if (!ranges) return options;
  return {
    ...options,
    retryConfig: {
      ...options.retryConfig,
      statusCodesToRetry: ranges.filter(([first, last]) => first !== 429 || last !== 429),
    },
  };
}

export function retryAfterDeadline(headers, now = Date.now()) {
  const header = typeof headers?.get === 'function'
    ? headers.get('Retry-After')
    : headers?.['retry-after'] ?? headers?.['Retry-After'];
  if (typeof header !== 'string') return null;
  const value = header.trim();
  const deadline = /^\d+$/.test(value)
    ? now + Number(value) * 1000
    : value.endsWith(' GMT') ? Date.parse(value) : NaN;
  return Number.isSafeInteger(deadline) && deadline > now ? deadline : null;
}
