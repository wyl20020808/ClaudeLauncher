// Development-only research for Claude 4.4212.0. No account files or application APIs.
// Read pinned bundle assets in memory; execute only four audited pure helpers with synthetic data.
// This does not implement an importer or test the CLI's resume behavior.
// These are primarily route B checks. Route A1 acceptance results are recorded separately in the research document.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const vm = require('node:vm');

assert.equal(process.argv.length, 2, 'This probe accepts no input paths or apply mode');
const bundle = '/Applications/Claude.app/Contents/Resources/';
const plist = fs.readFileSync(bundle + '../Info.plist', 'utf8');
assert.match(plist, /<key>CFBundleShortVersionString<\/key>\s*<string>4\.4212\.0<\/string>/,
  'Unsupported Claude version: re-audit before running this probe');
const fingerprints = {
  'index.chunk-Docy0cKb.js': '782df344d6c999b584ecaf8cbd025c9f665501b579827e912ca91aa31412c1dd',
  'index.chunk-BReS2dO4.js': '9d5d663e167752a62bca6fc7c0a202155ad7bbd4ab1a0d6915fcf55bd0a7be73',
  'index.chunk-BXKLnhhL.js': '657a39c9789b2cbd8ffd7c28fdf0a5e6a633b910cc43fcd942a36a83c6438545',
  'index.chunk-DIg4rEdf.js': 'ed235726edad6dd5c3e92c0d5fd46a98a90b98c192b94fcc0af9a430938704e4',
  'index.chunk-C3r2jk0E.js': 'e1e869c68d1e17138bd9c4a1b4498c8e216e92da3a760e98099731572bb66bbb',
  'index.chunk-m4oMImJ-.js': '926694919e5ff5d05cb804945637ffbe6ae75bdeac5e461f30e7c3d5fa043ccb',
};
const assets = {};
const fd = fs.openSync(bundle + 'app.asar', 'r');
try {
  const header = Buffer.alloc(16);
  assert.equal(fs.readSync(fd, header, 0, 16, 0), 16);
  const headerSize = header.readUInt32LE(12);
  assert(headerSize > 0 && headerSize < 32 * 1024 * 1024);
  const bytes = Buffer.alloc(headerSize);
  assert.equal(fs.readSync(fd, bytes, 0, headerSize, 16), headerSize);
  const entries = JSON.parse(bytes).files['.vite'].files.build.files;
  for (const [name, hash] of Object.entries(fingerprints)) {
    const entry = entries[name];
    assert(entry && !entry.unpacked && !entry.link && entry.size < 16 * 1024 * 1024);
    const offset = Number(entry.offset);
    assert(Number.isSafeInteger(offset) && offset >= 0);
    const content = Buffer.alloc(entry.size);
    assert.equal(fs.readSync(fd, content, 0, content.length,
      8 + header.readUInt32LE(4) + offset), content.length);
    assert.equal(crypto.createHash('sha256').update(content).digest('hex'), hash,
      `Bundle asset changed: ${name}; re-audit required`);
    assets[name] = content.toString('utf8');
  }
} finally {
  fs.closeSync(fd);
}

// Use Node's bundled parser. Never import or execute the application modules.
const parserSource = process.binding('natives')['internal/deps/acorn/acorn/dist/acorn'];
assert.equal(typeof parserSource, 'string', 'Node with bundled Acorn is required');
const parserExports = {};
vm.runInContext(parserSource, vm.createContext({exports: parserExports,
  module: {exports: parserExports}}, {codeGeneration: {strings: false, wasm: false}}),
  {timeout: 3000});
function pureFunction(asset, name) {
  const source = assets[asset];
  const start = source.indexOf(`function ${name}(`);
  assert(start >= 0 && source.indexOf(`function ${name}(`, start + 1) === -1);
  const node = parserExports.parseExpressionAt(source, start, {ecmaVersion: 'latest'});
  assert.equal(node.type, 'FunctionExpression');
  assert.equal(node.id.name, name);
  return source.slice(start, node.end);
}
const context = vm.createContext({}, {codeGeneration: {strings: false, wasm: false}});
const constants = assets['index.chunk-m4oMImJ-.js'];
assert(constants.includes('code1p:"local-1p-code",terminalCli:"terminal-cli"'));
vm.runInContext('const t = Object.freeze({Xit: Object.freeze({' +
  'code1p:"local-1p-code",terminalCli:"terminal-cli"})});\n' +
  pureFunction('index.chunk-Docy0cKb.js', 'q') + '\n' +
  pureFunction('index.chunk-Docy0cKb.js', 'H') + '\n' +
  pureFunction('index.chunk-BReS2dO4.js', 'mN') + '\n' +
  pureFunction('index.chunk-BXKLnhhL.js', 'ot'), context, {timeout: 3000});
function evaluate(expression) {
  return JSON.parse(vm.runInContext(`JSON.stringify(${expression})`, context, {timeout: 1000}));
}
assert.deepEqual(evaluate('[typeof process,typeof require,typeof fetch,typeof setTimeout]'),
  ['undefined', 'undefined', 'undefined', 'undefined']);
console.log('PASS: installed version, six asset fingerprints, isolated pure-helper execution');

const ids = Array.from({length: 8}, (_, index) =>
  `00000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}`);
const cwd = '/synthetic-only/project'; // A string only; never created, read or trusted.
const rows = [
  {type: 'user', sessionId: ids[0], uuid: ids[1], parentUuid: null, cwd,
    message: {role: 'user', content: '请检查计算结果。'}},
  {type: 'assistant', sessionId: ids[0], uuid: ids[2], parentUuid: ids[1], cwd,
    message: {role: 'assistant', content: [{type: 'tool_use', id: 'synthetic-tool-1',
      name: 'Read', input: {file_path: '/synthetic-only/project/result.txt'}}]}},
  {type: 'user', sessionId: ids[0], uuid: ids[3], parentUuid: ids[2], cwd,
    toolUseResult: {agentId: 'synthetic-agent', text: '42'},
    message: {role: 'user', content: [{type: 'tool_result', tool_use_id: 'synthetic-tool-1', content: '42'}]}},
  {type: 'assistant', sessionId: ids[0], uuid: ids[4], parentUuid: ids[3], cwd,
    message: {role: 'assistant', content: [{type: 'text', text: '结果为 42。'}]}},
];
vm.runInContext(`const rows = ${JSON.stringify(rows)};`, context);
const cleaned = evaluate('rows.map(q)');
assert(cleaned.every(row => !Object.hasOwn(row, 'sessionId')));
assert(!Object.hasOwn(cleaned[2].toolUseResult, 'agentId'));
assert.deepEqual(cleaned.map(row => row.message), rows.map(row => row.message));
assert.deepEqual(cleaned.map(row => [row.uuid, row.parentUuid]), rows.map(row => [row.uuid, row.parentUuid]));
assert.equal(cleaned[1].message.content[0].id, cleaned[2].message.content[0].tool_use_id);
assert.deepEqual(evaluate('rows'), rows, 'The official helper must not mutate the source fixtures');
console.log('PASS: official row helper detaches session/agent references without summarizing text or breaking fixture links');

const unsafe = {...rows[0], emailAddress: 'synthetic@example.invalid',
  unknownFutureField: 'SYNTHETIC_PRIVATE_METADATA',
  message: {role: 'user', content: 'SYNTHETIC_SECRET_IN_BODY'}};
const stillUnsafe = evaluate(`q(${JSON.stringify(unsafe)})`);
assert.equal(stillUnsafe.emailAddress, unsafe.emailAddress);
assert.equal(stillUnsafe.unknownFutureField, unsafe.unknownFutureField);
assert.equal(stillUnsafe.message.content, unsafe.message.content);
assert.equal(evaluate('H({type:"system"}) === undefined'), true);
console.log('PASS: counterexamples confirm official helper is NOT a complete privacy filter; missing-cwd rows need separate handling');

const sourceMetadata = {cliSessionId: ids[5], title: '合成导入', isArchived: false,
  emailAddress: 'synthetic@example.invalid', cloudSessionId: ids[0],
  priorCliSessionIds: [ids[0]], remoteMcpServersConfig: {synthetic: true},
  sessionPermissionUpdates: [{type: 'synthetic'}], pendingFirstStart: {synthetic: true},
  resumeConfirmed: true};
const staging = `/synthetic-only/target/imported-staging/${ids[5]}.jsonl`;
const makeRecord = (desktopId, metadata = sourceMetadata) => evaluate(
  `mN(${JSON.stringify(desktopId)},${JSON.stringify(metadata)},${JSON.stringify(cwd)},` +
  `1000,2000,3000,${JSON.stringify(staging)})`);
const record = makeRecord(`local_${ids[6]}`);
assert.deepEqual(Object.keys(record).sort(), ['sessionId', 'cliSessionId', 'cwd', 'originCwd', 'title',
  'createdAt', 'lastActivityAt', 'indexedAt', 'isArchived', 'importedFrom', 'stagedTranscriptPath'].sort());
assert.equal(record.importedFrom, 'local-1p-code');
assert.equal(record.stagedTranscriptPath, staging);
assert(!Object.hasOwn(record, 'resumeConfirmed'), 'Source confirmation must not bypass target confirmation');
assert.equal(record.cliSessionId, ids[5]);
assert.notEqual(record.sessionId, makeRecord(`local_${ids[7]}`).sessionId);
const inherited = makeRecord(`local_${ids[6]}`, {...sourceMetadata,
  launcherMarked: true, adoptedFromOtherSurface: true});
assert.equal(inherited.launcherMarked, true);
assert.equal(inherited.adoptedFromOtherSurface, true);
console.log('PASS: import serializer constructs a new metadata record, omits source permissions/confirmation, but has optional flags callers must exclude');

const compact = {type: 'system', subtype: 'compact_boundary', compactMetadata: {
  preservedMessages: {anchorUuid: ids[1], uuids: [ids[2], ids[3]]},
  preservedSegment: {anchorUuid: ids[1], headUuid: ids[2], tailUuid: ids[3]},
}};
assert.deepEqual(evaluate(`ot(${JSON.stringify(compact)})`), {compactMetadata: compact.compactMetadata});
assert.deepEqual(evaluate('ot({type:"user"})'), {});
console.log('PASS: compact-boundary helper preserves message references; indiscriminate UUID removal is not valid');

// Static observations, not execution of registration, filesystem loading, confirmation or resume.
const registration = assets['index.chunk-C3r2jk0E.js'];
const orchestration = assets['index.chunk-DIg4rEdf.js'];
const reader = assets['index.chunk-BXKLnhhL.js'];
for (const marker of ['async registerExternalSession(', 'async confirmImportedSessionResume(',
  'blockedReason:"needs_import_confirm"', 'stagedTranscriptPath=void 0',
  'resumeConfirmed=!0', '$.resume=a.cliSessionId']) assert(registration.includes(marker), marker);
assert(orchestration.includes('cliSessionId:(0,d.randomUUID)()'));
assert(reader.includes('t.stagedTranscriptPath?(0,c.dirname)(t.stagedTranscriptPath)'));
assert(assets['index.chunk-BReS2dO4.js'].includes('isFirstTurn:!r.cliSessionId,messageBuffer:[]'));
console.log('PASS: pinned source contains staging lookup, fresh CLI ID, confirmation guard and resume selection (static observations only)');
console.log('NOT TESTED BY THIS PROBE: route A1 acceptance, full JSONL parsing, GUI recognition, actual CLI/model continuation, attachments, forks/rewinds, other versions/platforms.');
console.log('No real profiles, transcripts, credentials, network, child processes or disk writes were used by this probe.');
