// Development-only probe. No account files, credentials, network, or Claude processes are accessed.
// Read selected pure functions from the installed 4.29.0 bundle in memory; never copy the bundle.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const vm = require('node:vm');
const assert = require('node:assert/strict');

function readAsset(name, expectedHash) {
  const fd = fs.openSync('/Applications/Claude.app/Contents/Resources/ion-dist.asar', 'r');
  try {
    const header = Buffer.alloc(16);
    assert.equal(fs.readSync(fd, header, 0, 16, 0), 16);
    const size = header.readUInt32LE(12);
    assert(size > 0 && size < 32 * 1024 * 1024);
    const bytes = Buffer.alloc(size);
    assert.equal(fs.readSync(fd, bytes, 0, size, 16), size);
    const entry = JSON.parse(bytes).files.assets.files.v1.files[name];
    assert(entry && !entry.unpacked && !entry.link && entry.size < 16 * 1024 * 1024);
    const content = Buffer.alloc(entry.size);
    assert.equal(fs.readSync(fd, content, 0, content.length,
      8 + header.readUInt32LE(4) + Number(entry.offset)), content.length);
    assert.equal(crypto.createHash('sha256').update(content).digest('hex'), expectedHash,
      'Claude bundle changed: re-audit before running this probe');
    return content.toString('utf8');
  } finally { fs.closeSync(fd); }
}

// Use Node's bundled parser, with no package download. Fail closed on runtimes without it.
const parserSource = process.binding('natives')['internal/deps/acorn/acorn/dist/acorn'];
assert.equal(typeof parserSource, 'string', 'This development probe requires Node with bundled Acorn');
const parserExports = {};
const parserContext = vm.createContext({exports: parserExports, module: {exports: parserExports}});
vm.runInContext(parserSource, parserContext, {timeout: 3000});
const parse = parserContext.exports.parse;
function selectedFunctions(source, names) {
  const nodes = parse(source, {ecmaVersion: 'latest', sourceType: 'module'}).body;
  return names.map(name => {
    const node = nodes.find(node => node.type === 'FunctionDeclaration' && node.id.name === name);
    assert(node, `Missing audited function: ${name}`);
    return source.slice(node.start, node.end);
  }).join('\n');
}

const sidebar = readAsset('shared-22-Djh-bTk2.js',
  '634f0b15c2b1208bc7611145d339c7a279d354e131edf8d799cc8e45e6a1e0a3');
const grouping = readAsset('shared-46-l01HYYr-.js', '6fd6aac8fd897be0530034c396c0f90e5e6ec344196d0be5483db3411bcc6546');
const types = readAsset('shared-20-Cg7pinC8.js', '01c9981cb53a3df7f3df60343b4bdcdba618902266da3adf49aa5d0d7fb5c420');
const selected = selectedFunctions(sidebar, ['eD','tD','rD','lD','WE','UE','GE','PD','sD','FD','ID','LD'])
  + '\n' + selectedFunctions(grouping, ['OV','DV','EV'])
  + '\n' + selectedFunctions(types, ['Yw']);

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'claude-sidebar-seed-probe-'));
try {
  const folder = path.join(root, 'Project');
  fs.mkdirSync(folder);
  const makeSeed = () => ({
    sessionId: `local_${crypto.randomUUID()}`,
    cwd: folder,
    originCwd: folder,
    title: '项目目录入口（无对话）',
    createdAt: Date.now(),
    lastActivityAt: Date.now(),
    isArchived: true,
  });
  const first = makeSeed(), second = makeSeed();
  assert.notEqual(first.sessionId, second.sessionId);
  const allowed = ['sessionId','cwd','originCwd','title','createdAt','lastActivityAt','isArchived'].sort();
  for (const [number, record] of [first, second].entries()) {
    assert.deepEqual(Object.keys(record).sort(), allowed);
    assert.match(record.sessionId, /^local_[a-f0-9-]{36}$/);
    const file = path.join(root, `${number}-${record.sessionId}.json`);
    fs.writeFileSync(file, JSON.stringify(record), {flag: 'wx', mode: 0o600});
    assert.equal(fs.statSync(file).mode & 0o777, 0o600);
    assert.deepEqual(JSON.parse(fs.readFileSync(file)), record);
  }
  // Event adaptation is synthetic: these tests exercise grouping, not the backend loader or GUI.
  const context = vm.createContext({recordJSON: JSON.stringify(first)},
    {codeGeneration: {strings: false, wasm: false}});
  const result = vm.runInContext(selected + `
    const Go=OV, Yc=Yw, VD=[], HE=['local','remote','ssh','bridge','slack'];
    const kD='preset::', AD='kind::';
    const ud=(a,b)=>a.localeCompare(b);
    const record=JSON.parse(recordJSON);
    const event={...record,type:'local',repoInfo:{name:'Project'}};
    const buckets=new Map();
    FD(buckets,lD(event),event,null);
    const groups=LD(buckets,new Map([[record.cwd,'Project']]),new Map(),[]);
    JSON.stringify(groups);
  `, context, {timeout: 1000});
  const groups = JSON.parse(result);
  assert.equal(groups.length, 1);
  assert.equal(groups[0].label, 'Project');
  assert.equal(groups[0].folder.projectKey, folder);
  assert.equal(groups[0].folder.ran.kind, 'local');
  assert.equal(groups[0].sessions.length, 0);
  assert.equal(groups[0].hiddenSessions.length, 1);
  assert.equal(groups[0].hiddenSessions[0].sessionId, first.sessionId);
  console.log('PASS: seven-field whitelist; fresh IDs per synthetic account; 0600 JSON round trip');
  console.log('PASS: audited folder-group functions retain one folder with zero active rows');
  console.log('UNVERIFIED: backend acceptance, full sidebar rendering, restart persistence, new-chat handoff');
} finally {
  fs.rmSync(root, {recursive: true});
  assert(!fs.existsSync(root));
  console.log('CLEAN: only generated temporary fixtures removed; real profiles never opened');
}
