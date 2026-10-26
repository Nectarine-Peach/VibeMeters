// Offline checks run outside the checkout, with no access to the user's CLI home.
import assert from 'node:assert/strict';
import { mkdtemp, copyFile, writeFile, mkdir, readFile, rm, readdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

const source = path.resolve(process.argv[2]);
const temporaryRoot = path.resolve(os.tmpdir());
const temporary = await mkdtemp(path.join(temporaryRoot, 'VibeMeters-runtime-'));
try {
  const home = path.join(temporary, 'home');
  await mkdir(home);
  for (const file of ['quota.mjs', 'package.json'])
    await copyFile(path.join(source, file), path.join(temporary, file));
  const manifest = JSON.parse(await readFile(path.join(source, 'runtime-manifest.json'), 'utf8'));
  assert.equal(manifest.bundleSha256, createHash('sha256').update(await readFile(path.join(source, 'quota.mjs'))).digest('hex'));
  assert.equal(manifest.node, process.version);
  const preload = path.join(temporary, 'offline.mjs');
  await writeFile(preload, `
import http from 'node:http'; import https from 'node:https'; import net from 'node:net';
import cp from 'node:child_process'; import { syncBuiltinESMExports } from 'node:module';
const deny=()=>{throw new Error('OFFLINE_DIAGNOSTIC_BLOCKED_IO')};
http.request=http.get=https.request=https.get=net.connect=net.createConnection=deny;
cp.spawn=cp.exec=cp.execFile=cp.spawnSync=cp.execSync=cp.execFileSync=deny;
globalThis.fetch=deny; syncBuiltinESMExports();
`);
  const env = {};
  for (const key of ['SystemRoot', 'SYSTEMROOT', 'windir', 'WINDIR', 'COMSPEC', 'PATHEXT'])
    if (process.env[key]) env[key] = process.env[key];
  Object.assign(env, { HOME: home, USERPROFILE: home, APPDATA: home, LOCALAPPDATA: home,
    TEMP: temporary, TMP: temporary, PATH: '', NO_COLOR: '1' });
  let passed = 0;
  for (const [args, expected] of [[['--check-runtime'], 'Ready'], [[], 'AuthenticationRequired']]) {
    const child = spawnSync(process.execPath, ['--import', pathToFileURL(preload).href, path.join(temporary, 'quota.mjs'), ...args],
      { cwd: temporary, env, encoding: 'utf8', timeout: 15000, windowsHide: true });
    assert.equal(child.error, undefined, 'runtime must exit within the diagnostic deadline');
    assert.equal(child.status, 0, `isolated runtime process must succeed: ${child.stderr.slice(-2000)}`);
    assert.equal(child.stderr, '', 'runtime must not emit SDK diagnostics or secrets');
    assert.equal(JSON.parse(child.stdout).status, expected, `isolated check: ${child.stdout}`);
    passed++;
  }
  assert(!(await readdir(temporary)).includes('node_modules'));
  console.log(`PASS: ${passed} isolated bundled-runtime checks; manifest hash; no node_modules, network or browser`);
} finally {
  // Only remove the fresh directory owned by this diagnostic.
  if (!temporary.startsWith(temporaryRoot + path.sep) || !path.basename(temporary).startsWith('VibeMeters-runtime-'))
    throw new Error('Unsafe diagnostic cleanup path');
  await rm(temporary, { recursive: true, force: true });
}
