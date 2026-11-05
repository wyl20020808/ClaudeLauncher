// Development-only, offline probe for Windows Claude 4.16336.0.0.
// Reads installed program code into memory. Uses synthetic state and injected
// dependencies, without loading the app, account files, IPC or network services.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const resources = 'C:/Program Files/WindowsApps/Claude_4.16336.0.0_x64__pzs8sxrjxfjjc/app/resources';
function readAsset(archive, name, expectedHash) {
  const fd = fs.openSync(path.join(resources, archive), 'r');
  try {
    const header = Buffer.alloc(16);
    assert.equal(fs.readSync(fd, header, 0, 16, 0), 16);
    const size = header.readUInt32LE(12);
    assert(size > 0 && size < 32 * 1024 * 1024);
    const bytes = Buffer.alloc(size);
    assert.equal(fs.readSync(fd, bytes, 0, size, 16), size);
    let entry = JSON.parse(bytes);
    for (const part of name.split('/')) entry = entry.files[part];
    assert(entry && !entry.unpacked && !entry.link && entry.size < 16 * 1024 * 1024);
    const content = Buffer.alloc(entry.size);
    const offset = 8 + header.readUInt32LE(4) + Number(entry.offset);
    assert.equal(fs.readSync(fd, content, 0, content.length, offset), content.length);
    assert.equal(crypto.createHash('sha256').update(content).digest('hex'), expectedHash,
      'Claude bundle changed; re-audit before running this probe');
    return content.toString('utf8');
  } finally { fs.closeSync(fd); }
}

const parserSource = process.binding('natives')['internal/deps/acorn/acorn/dist/acorn'];
assert.equal(typeof parserSource, 'string');
const parserExports = {};
const parserContext = vm.createContext({ exports: parserExports, module: { exports: parserExports } });
vm.runInContext(parserSource, parserContext, { timeout: 3000 });
function functions(source, names) {
  const nodes = parserExports.parse(source, { ecmaVersion: 'latest', sourceType: 'module' }).body;
  return names.map(name => {
    const node = nodes.find(node => node.type === 'FunctionDeclaration' && node.id.name === name);
    assert(node, `Missing audited function ${name}`);
    return source.slice(node.start, node.end);
  }).join('\n');
}
function context(source, dependencies) {
  const result = vm.createContext(dependencies, { codeGeneration: { strings: false, wasm: false } });
  vm.runInContext(source, result, { timeout: 3000 });
  return result;
}
const main = readAsset('app.asar', '.vite/build/index.chunk-CdwXqeBD.js',
  '2ade0ec37b2441110079a9e288983937b10c7925da50ba03f3032ec822f50af9');
const sidebar = readAsset('ion-dist.asar', 'assets/v1/shared-22-BACJUrcb.js',
  '6629e87cd46c19aaace5887518795ac2e1417a958d4849ebe4f63c52176fdd3e');
const telemetry = readAsset('ion-dist.asar', 'assets/v1/shared-140-DfG6VwpK.js',
  'c23715333142677854aedb06a57a4993941dbe8da6b7dba8f687d7d8428433d3');
const settings = readAsset('ion-dist.asar', 'assets/v1/shared-21-CuohtEoj.js',
  '8513ed8b2bdb0e469685841a90fb35d63529a0c18733e6fd20f3d7a96f3a765d');
const routes = readAsset('app.asar', '.vite/build/index.chunk-Bqw5pOHo.js',
  '256c649a0a7e02a1e413ff957aec93aec0f89bd5938dad69f8318652a4adb4fd');

let checks = 0;
function check(name, condition) {
  assert(condition, name);
  checks++;
  console.log(`PASS ${name}`);
}
async function run() {
  const events = [];
  const state = {
    activeGroupScope: 'synthetic-account/synthetic-org',
    groups: [],
    // Only the store dependency is simulated. The handlers and serializer below
    // are the hash-checked functions from the installed package.
    addCustomGroup(name, mode) {
      let group = this.groups.find(group => group.name === name);
      if (!group) {
        group = { id: `cg-synthetic-${this.groups.length + 1}`, name, mode };
        this.groups.push(group);
      }
      return group;
    }
  };
  const store = { getState: () => state };
  const eventContext = context(functions(telemetry, ['rp']), {
    Z: store, Zn: value => value, tp: () => 'synthetic', np: () => 0
  });
  const ui = context(functions(sidebar, ['CR']), {
    $o: store, Oo: (value, name) => value.groups.find(group => group.name === name),
    uo: value => value.groups, xR: 100, SR: () => 'code', bp: () => {}, Xf: eventContext.rp
  });
  const calls = [];
  const uiOptions = { track: event => events.push(event), overlayEnabled: true };
  const result = (text, isError = false) => ({ content: [{ type: 'text', text }], isError });
  const handler = context(functions(main, ['K', 'H_', 'V_', 'q_', 'J', 'K_']), {
    t: { dh: text => result(text, true), fh: text => result(text), ph: text => result(text),
      fat: { CreateGroup: 'create_group' } },
    Og: 'list_groups', kg: 'create_group', o_: 200, f_: 'disabled', P: text => text,
    q: async (dependencies, command) => {
      calls.push(command);
      return ui.CR(uiOptions, command.name);
    }
  });
  const execute = handler.q_({ isEnabled: () => true,
    getSessions: async () => ({ listCustomGroups: () => state.groups }) });
  let response = await execute('create_group', { name: '  Offline probe  ' }, {});
  check('official tool handler creates a group with injected local dispatch', !response.isError && state.groups.length === 1);
  check('official tool handler trims the name', state.groups[0].name === 'Offline probe');
  check('official UI handler emits a group-change event', events.length === 1 && events[0].event_key === 'claudeai.epitaxy.sidebar.custom_group_changed');
  await execute('create_group', { name: 'Offline probe' }, {});
  check('same-name store result is reused without another create event', state.groups.length === 1 && events.length === 1);
  const priorCalls = calls.length;
  response = await execute('create_group', { name: ' ' }, {});
  check('empty name rejected before dispatch', response.isError && calls.length === priorCalls);
  response = await execute('create_group', { name: 'x'.repeat(201) }, {});
  check('overlong name rejected before dispatch', response.isError && calls.length === priorCalls);
  state.activeGroupScope = null;
  response = await execute('create_group', { name: 'Second group' }, {});
  check('UI handler refuses an unsigned-in sidebar', response.isError && state.groups.length === 1);

  const missingWindow = context(functions(main, ['Be']), {
    // Resolve only the two known module exports, with no Electron process.
    require: () => ({ wht: { mainView: null }, krt: { LocalSessions: {} } })
  });
  check('official dispatch fails without an app window', await missingWindow.Be('synthetic-request', { kind: 'create_group' }) === false);

  // The dispatcher itself needs a live renderer connection, not foreground input.
  // This does not make that connection available to an external launcher process.
  let rendererAlive = true, connected = true;
  const dispatched = [];
  const renderer = { isDestroyed: () => !rendererAlive };
  const dispatcher = { dispatchSidebarCommand: (id, command) => dispatched.push({ id, command }) };
  const internalDispatch = context(functions(main, ['Be']), {
    require: () => ({ wht: { mainView: { webContents: renderer } },
      krt: { LocalSessions: { getDispatcher: view => view === renderer && connected ? dispatcher : null } } })
  });
  check('official internal dispatch needs no show or focus method',
    await internalDispatch.Be('synthetic-background', { kind: 'create_group', name: 'Background probe' }) === true &&
    dispatched.length === 1 && dispatched[0].id === 'synthetic-background');
  rendererAlive = false;
  check('destroyed renderer refuses background dispatch',
    await internalDispatch.Be('synthetic-dead', { kind: 'create_group' }) === false && dispatched.length === 1);
  rendererAlive = true; connected = false;
  check('missing internal dispatcher refuses background dispatch',
    await internalDispatch.Be('synthetic-disconnected', { kind: 'create_group' }) === false && dispatched.length === 1);

  const serialized = context(functions(settings, ['yy']), {
    ul: value => value.scope, ml: () => ({ sections: [] }), Uc: value => value,
    // No session ids are supplied, so predicates cannot affect this assertion.
    uy: () => { throw Error('Unexpected session access'); },
    fy: () => { throw Error('Unexpected session access'); },
    dl: () => { throw Error('Unexpected session access'); }
  }).yy({}, { scope: { groups: state.groups, assignments: {}, order: {} } });
  check('official server serializer includes custom group name and id',
    serialized.customGroups[0].name === 'Offline probe' && serialized.customGroups[0].id === 'cg-synthetic-1');
  check('settings register the tested server serializer', settings.includes('serverPartialize:yy'));
  check('synthetic handlers have no process or network globals',
    vm.runInContext('typeof process === "undefined" && typeof require === "undefined" && typeof fetch === "undefined"', handler));

  // A UI adapter must resolve the actual session, including when titles collide.
  // This is the official code/continue resolver, not a replacement implementation.
  const sessions = [
    { sessionId: 'local_other', title: 'Same title', lastActivityAt: 3 },
    { sessionId: 'local_wanted', title: 'Same title', lastActivityAt: 1 },
    { sessionId: 'local_archived', title: 'Archived', isArchived: true, lastActivityAt: 5 }
  ];
  let loads = 0;
  const resolver = context(functions(routes, ['Ejr']), {
    UC: async () => ({
      waitForSessionsLoaded: async () => { loads++; },
      getAllSessions: async () => sessions,
      getSessionRoute: id => '/epitaxy/' + id
    })
  });
  check('official continue resolver uses exact ID despite identical titles',
    await resolver.Ejr('local_wanted') === '/epitaxy/local_wanted');
  check('missing explicit ID does not fall back to another session',
    await resolver.Ejr('local_missing') === undefined);
  check('archived session is not selected by continue',
    await resolver.Ejr('local_archived') === undefined);
  check('session loading is awaited for every lookup', loads === 3);
  console.log(`${checks} checks passed. Synthetic dependencies only; live IPC, persistence and network behavior remain unverified.`);
}
run().catch(error => { console.error(error.message); process.exitCode = 1; });
