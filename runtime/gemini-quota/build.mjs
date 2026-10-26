import { build } from 'esbuild';
import { createRequire, isBuiltin } from 'node:module';
import { mkdir, writeFile, copyFile, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const root = path.dirname(fileURLToPath(import.meta.url));
const output = path.resolve(process.argv[2] || path.join(root, 'bundle'));
const require = createRequire(import.meta.url);
const config = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
if (process.version !== `v${config.bundledNodeVersion}` || process.platform !== 'win32' || process.arch !== 'x64')
  throw new Error(`Bundle requires Windows x64 Node ${config.bundledNodeVersion}`);
await mkdir(output, { recursive: true });
const result = await build({
  absWorkingDir: root, entryPoints: ['quota.mjs'], outfile: path.join(output, 'quota.mjs'),
  bundle: true, platform: 'node', target: 'node24', format: 'esm', minify: true,
  metafile: true, legalComments: 'eof', loader: { '.wasm': 'binary' },
  banner: { js: "import { createRequire as __vmCreateRequire } from 'node:module'; import { fileURLToPath as __vmFileURLToPath } from 'node:url'; import { dirname as __vmDirname } from 'node:path'; const require = __vmCreateRequire(import.meta.url); const __filename = __vmFileURLToPath(import.meta.url); const __dirname = __vmDirname(__filename);" },
});
// These optional accelerators/UI probes are guarded by upstream try/catch fallbacks.
// The existing production install omitted optional dependencies as well.
const optional = new Set(['supports-color', 'bufferutil', 'utf-8-validate', 'osx-temperature-sensor', 'encoding']);
for (const file of Object.values(result.metafile.outputs))
  for (const item of file.imports)
    if (item.external && !isBuiltin(item.path) && !optional.has(item.path))
      throw new Error(`Unexpected external dependency: ${item.path}`);
await writeFile(path.resolve(process.argv[3] || `${output}.meta.json`), JSON.stringify(result.metafile, null, 2));
const corePackage = require.resolve('@google/gemini-cli-core/package.json');
const core = JSON.parse(await readFile(corePackage, 'utf8'));
if (core.version !== '0.65.0') throw new Error('Unexpected official core version');
await copyFile(path.join(path.dirname(corePackage), 'LICENSE'), path.join(output, 'GEMINI-LICENSE.txt'));
// Preserve complete notices for packages that contribute code, including nested npm packages.
const packages = new Map();
for (const [input, detail] of Object.entries(Object.values(result.metafile.outputs)[0].inputs)) {
  if (!detail.bytesInOutput || !input.includes('node_modules/')) continue;
  let dir = path.dirname(path.resolve(root, input));
  while (dir.startsWith(root)) {
    try {
      const pkg = JSON.parse(await readFile(path.join(dir, 'package.json'), 'utf8'));
      if (pkg.name && pkg.version) { packages.set(dir, pkg); break; }
    } catch { /* Walk from an input file to its owning package. */ }
    dir = path.dirname(dir);
  }
}
let notices = '';
for (const [directory, pkg] of [...packages].sort((a,b) => a[1].name.localeCompare(b[1].name))) {
  notices += `\n===== ${pkg.name} ${pkg.version} (${pkg.license || 'see notice'}) =====\n`;
  const { readdir } = await import('node:fs/promises');
  for (const name of await readdir(directory))
    if (/^(licen[sc]e|copying|notice)(\.|$)/i.test(name))
      notices += await readFile(path.join(directory, name), 'utf8') + '\n';
}
await writeFile(path.join(output, 'THIRD-PARTY-NOTICES.txt'), notices);
// Upstream getVersion searches upwards for package.json: preserve its original identity.
await writeFile(path.join(output, 'package.json'), JSON.stringify({ name: core.name, version: core.version, type: 'module' }));
await writeFile(path.join(output, 'runtime-manifest.json'), JSON.stringify({ core: core.version,
  esbuild: JSON.parse(await readFile(require.resolve('esbuild/package.json'), 'utf8')).version,
  node: process.version, platform: process.platform, arch: process.arch,
  lockSha256: createHash('sha256').update(await readFile(path.join(root, 'package-lock.json'))).digest('hex'),
  bundleSha256: createHash('sha256').update(await readFile(path.join(output, 'quota.mjs'))).digest('hex'),
  optionalFallbacks: [...optional].sort() }, null, 2));
