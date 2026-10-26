// Short-lived adapter using the official, app-private Gemini CLI core.
// Official code owns authentication and token renewal; credentials never leave it.
import { writeSync } from 'node:fs';
const suppressWrite = (chunk, encoding, callback) => {
  const done = typeof encoding === 'function' ? encoding : callback;
  if (typeof done === 'function') queueMicrotask(done);
  return true;
};
process.stdout.write = suppressWrite;
process.stderr.write = suppressWrite;
const finish = value => {
  if (!interactiveLogin) writeSync(1, JSON.stringify(value));
  // Graceful completion lets the official client's pending token-storage writes finish.
  process.exitCode = 0;
  // Do not retain this one-shot process if SDK background timers keep the event loop alive.
  setTimeout(() => process.exit(0), 1000).unref();
};
const failAuth = () => { const error = new Error('AUTH_REQUIRED'); error.code = 'AUTH_REQUIRED'; throw error; };
const interactiveLogin = process.argv.includes('--login');
const lifetime = setTimeout(() => process.exit(0), interactiveLogin ? 180000 : 30000);
lifetime.unref();
async function main() {
try {
  // Literal module paths permit bundling; defer SDK initialization until logs are suppressed.
  const { getOauthClient, CodeAssistServer, AuthType } = await import('./official-client.mjs');
  // This diagnostic loads the complete packaged module graph without reading credentials.
  if (process.argv.includes('--check-runtime')) {
    const valid = typeof getOauthClient === 'function' &&
      typeof CodeAssistServer?.prototype?.loadCodeAssist === 'function' &&
      typeof CodeAssistServer?.prototype?.retrieveUserQuota === 'function' &&
      AuthType.LOGIN_WITH_GOOGLE === 'oauth-personal';
    if (!valid) return finish({ status: 'Unavailable' });
    const calls = [];
    const server = new CodeAssistServer({ request: async options => {
      calls.push(options);
      return { data: options.url.endsWith(':loadCodeAssist')
        ? { cloudaicompanionProject: 'diagnostic-project' }
        : { buckets: [{ modelId: 'gemini-diagnostic', remainingFraction: 0.5 }] } };
    } }, 'diagnostic-project');
    const account = await server.loadCodeAssist({ metadata: { platform: 'WINDOWS_AMD64' } });
    const quota = await server.retrieveUserQuota({ project: account.cloudaicompanionProject });
    return finish({ status: calls.length === 2 && calls[0].url.endsWith(':loadCodeAssist') &&
      calls[1].url.endsWith(':retrieveUserQuota') &&
      JSON.parse(calls[1].body).project === 'diagnostic-project' && quota.buckets[0].remainingFraction === 0.5
      ? 'Ready' : 'Unavailable' });
  }
  if (typeof CodeAssistServer?.prototype?.retrieveUserQuota !== 'function')
    return finish({ status: 'Unavailable', reason: 'RUNTIME_REQUIRED' });
  const config = {
    getProxy: () => process.env.HTTPS_PROXY || process.env.https_proxy || undefined,
    // Only reached when the official client's normal cached authentication/renewal failed.
    isBrowserLaunchSuppressed: interactiveLogin ? () => false : failAuth,
    isInteractive: () => false,
    // The app's explicit Login button supplies consent; no hidden terminal prompt.
    getAcpMode: () => interactiveLogin,
  };
  const client = await getOauthClient(AuthType.LOGIN_WITH_GOOGLE, config);
  const configuredProject = process.env.GOOGLE_CLOUD_PROJECT || process.env.GOOGLE_CLOUD_PROJECT_ID || undefined;
  const server = new CodeAssistServer(client, configuredProject);
  const account = await server.loadCodeAssist({
    cloudaicompanionProject: configuredProject,
    metadata: { ideType: 'IDE_UNSPECIFIED', platform: 'WINDOWS_AMD64', pluginType: 'GEMINI', duetProject: configuredProject },
  });
  const project = account.cloudaicompanionProject || configuredProject;
  if (typeof project !== 'string' || !project) return finish({ status: 'Unavailable', reason: 'PROJECT_REQUIRED' });
  const quota = await server.retrieveUserQuota({ project });
  const windows = (Array.isArray(quota?.buckets) ? quota.buckets : []).filter(bucket =>
    typeof bucket.modelId === 'string' && bucket.modelId.length <= 100 &&
    typeof bucket.remainingFraction === 'number' && Number.isFinite(bucket.remainingFraction) &&
    bucket.remainingFraction >= 0 && bucket.remainingFraction <= 1
  ).map(bucket => ({
    name: bucket.modelId.replace(/^gemini-/, ''),
    remainingPercent: bucket.remainingFraction * 100,
    resetsAt: typeof bucket.resetTime === 'string' && Number.isFinite(Date.parse(bucket.resetTime)) ? bucket.resetTime : null,
  }));
  windows.sort((a, b) => a.remainingPercent - b.remainingPercent || a.name.localeCompare(b.name));
  const tier = account.paidTier || account.currentTier;
  return finish({ status: windows.length ? 'Ready' : 'Unavailable', reason: windows.length ? null : 'NO_QUOTA',
    plan: typeof tier?.name === 'string' ? tier.name : typeof tier?.id === 'string' ? tier.id : null, windows });
} catch (error) {
  if (process.argv.includes('--check-runtime'))
    return finish({ status: 'Unavailable', reason: 'RUNTIME_REQUIRED', diagnostic:
      error?.name === 'ReferenceError' ? String(error.message).replace(/[^a-zA-Z0-9 _]/g, '').slice(0, 100)
      : String(error?.code || error?.name || 'RUNTIME_LOAD_FAILED').slice(0, 100) });
  // Never return SDK messages, headers, response bodies, OAuth URLs, or account identifiers.
  const statusCode = Number(error?.response?.status || error?.status || error?.code);
  if (error?.code === 'AUTH_REQUIRED' || statusCode === 401 || statusCode === 403)
    return finish({ status: 'AuthenticationRequired', reason: 'AUTH_REQUIRED' });
  if (statusCode === 429) return finish({ status: 'RateLimited', reason: 'RATE_LIMITED' });
  return finish({ status: 'Unavailable', reason: error?.code === 'ERR_MODULE_NOT_FOUND' ? 'RUNTIME_REQUIRED' : 'NETWORK' });
}
}
await main();
