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
  // Narrow module imports avoid initializing the CLI's model, tool, and UI registries.
  const root = new URL('.', import.meta.resolve('@google/gemini-cli-core'));
  const [{ getOauthClient }, { CodeAssistServer }, { AuthType }] = await Promise.all([
    import(new URL('src/code_assist/oauth2.js', root).href),
    import(new URL('src/code_assist/server.js', root).href),
    import(new URL('src/core/contentGenerator.js', root).href),
  ]);
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
  // Never return SDK messages, headers, response bodies, OAuth URLs, or account identifiers.
  const statusCode = Number(error?.response?.status || error?.status || error?.code);
  if (error?.code === 'AUTH_REQUIRED' || statusCode === 401 || statusCode === 403)
    return finish({ status: 'AuthenticationRequired', reason: 'AUTH_REQUIRED' });
  if (statusCode === 429) return finish({ status: 'RateLimited', reason: 'RATE_LIMITED' });
  return finish({ status: 'Unavailable', reason: error?.code === 'ERR_MODULE_NOT_FOUND' ? 'RUNTIME_REQUIRED' : 'NETWORK' });
}
}
await main();
