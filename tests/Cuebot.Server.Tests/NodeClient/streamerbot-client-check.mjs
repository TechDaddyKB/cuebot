// Drives a Cuebot Streamer.bot-compatible WebSocket server with the official @streamerbot/client, the way overlays
// use it: connect with a password, subscribe through client.on(), receive events, call every request the client has.
// Responses and Twitch event payloads are checked against the client's own TypeScript types (index.d.ts, read with
// the TypeScript compiler API); the client's response handlers (getGlobal, getUserGlobal, customEventResponse) run on
// the real responses.
//
// Usage: node streamerbot-client-check.mjs <config.json>
//   config: { clientDir, port, password, events: ["Twitch.ChatMessage", ...] }
//   clientDir holds node_modules/@streamerbot/client and node_modules/typescript (npm install them there).
// Prints "READY" once subscribed (the caller then fires the events), then "RESULT <json>"; exit code 1 on failures.

import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const config = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const clientRoot = path.join(config.clientDir, 'node_modules', '@streamerbot', 'client');
const { StreamerbotClient } = await import(pathToFileURL(path.join(clientRoot, 'dist', 'index.js')).href);
const ts = createRequire(path.join(config.clientDir, 'package.json'))('typescript');

const failures = [];
const checked = [];
const fail = (what, message) => failures.push(`${what}: ${message}`);

// ---- The client's types ---------------------------------------------------------------------------------------

const dts = path.join(clientRoot, 'dist', 'index.d.ts');
const program = ts.createProgram([dts], {
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts'], strict: true, noEmit: true, skipLibCheck: true, types: [],
});
const checker = program.getTypeChecker();
const aliases = new Map();
ts.forEachChild(program.getSourceFile(dts), (node) => {
  if (ts.isTypeAliasDeclaration(node)) aliases.set(node.name.text, node);
});

function aliasType(name) {
  const decl = aliases.get(name);
  if (!decl) throw new Error(`index.d.ts has no type ${name}`);
  return checker.getTypeFromTypeNode(decl.type);
}

const propertyType = (prop) =>
  checker.getTypeOfSymbol ? checker.getTypeOfSymbol(prop) : checker.getTypeOfSymbolAtLocation(prop, prop.valueDeclaration);

/** Runtime check of a JSON value against a TypeScript type; adds "path: problem" strings to errors. */
function check(value, type, at, errors, depth = 0) {
  if (depth > 25) return;
  const f = type.flags;
  if (f & (ts.TypeFlags.Any | ts.TypeFlags.Unknown | ts.TypeFlags.TypeParameter)) return;
  if (type.isUnion()) {
    const attempts = type.types.map((member) => {
      const e = [];
      check(value, member, at, e, depth + 1);
      return e;
    });
    if (attempts.some((e) => e.length === 0)) return;
    // Report the closest member: not "undefined" for a present value, not the error response for status "ok".
    const viable = attempts.filter((e, i) => !(value !== undefined && type.types[i].flags & ts.TypeFlags.Undefined)
      && !e.some((x) => x.startsWith(`${at}.status:`)));
    errors.push(...(viable.length ? viable : attempts).reduce((best, e) => (e.length < best.length ? e : best)));
    return;
  }
  if (type.isIntersection()) {
    for (const member of type.types) check(value, member, at, errors, depth + 1);
    return;
  }
  if (f & ts.TypeFlags.Undefined || f & ts.TypeFlags.Void) {
    if (value !== undefined) errors.push(`${at}: expected undefined, got ${JSON.stringify(value)?.slice(0, 60)}`);
    return;
  }
  if (f & ts.TypeFlags.Null) {
    if (value !== null) errors.push(`${at}: expected null`);
    return;
  }
  if (f & ts.TypeFlags.StringLike) {
    if (typeof value !== 'string') errors.push(`${at}: expected string, got ${typeof value}`);
    else if (type.isStringLiteral() && value !== type.value) errors.push(`${at}: expected "${type.value}", got "${value}"`);
    return;
  }
  if (f & ts.TypeFlags.NumberLike) {
    if (typeof value !== 'number') errors.push(`${at}: expected number, got ${typeof value}`);
    else if (type.isNumberLiteral() && value !== type.value) errors.push(`${at}: expected ${type.value}`);
    return;
  }
  if (f & ts.TypeFlags.BooleanLike) {
    if (typeof value !== 'boolean') errors.push(`${at}: expected boolean, got ${typeof value}`);
    else if (f & ts.TypeFlags.BooleanLiteral && String(value) !== type.intrinsicName) errors.push(`${at}: expected ${type.intrinsicName}`);
    return;
  }
  if (f & ts.TypeFlags.Never) {
    errors.push(`${at}: no value allowed`);
    return;
  }
  if (checker.isArrayType(type) || checker.isTupleType(type)) {
    if (!Array.isArray(value)) {
      errors.push(`${at}: expected array, got ${value === null ? 'null' : typeof value}`);
      return;
    }
    const element = checker.getTypeArguments(type)[0];
    if (element) value.forEach((item, i) => check(item, element, `${at}[${i}]`, errors, depth + 1));
    return;
  }
  if (f & (ts.TypeFlags.Object | ts.TypeFlags.NonPrimitive)) {
    const props = checker.getPropertiesOfType(type);
    const indexes = checker.getIndexInfosOfType(type);
    if (props.length === 0 && indexes.length === 0) {
      if (value === null || value === undefined) errors.push(`${at}: expected a value`);
      return; // {} accepts anything else
    }
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
      errors.push(`${at}: expected object, got ${Array.isArray(value) ? 'array' : value === null ? 'null' : typeof value}`);
      return;
    }
    const names = new Set();
    for (const prop of props) {
      const name = prop.getName();
      names.add(name);
      const optional = (prop.flags & ts.SymbolFlags.Optional) !== 0;
      if (!(name in value)) {
        if (!optional) errors.push(`${at}.${name}: missing`);
        continue;
      }
      check(value[name], propertyType(prop), `${at}.${name}`, errors, depth + 1);
    }
    for (const info of indexes) {
      for (const [key, item] of Object.entries(value)) {
        if (!names.has(key)) check(item, info.type, `${at}.${key}`, errors, depth + 1);
      }
    }
  }
}

/**
 * Places where the request reference and the client's types disagree and Cuebot follows the reference: GetBroadcaster
 * lists kick (the reference shows it; the client's BroadcasterPlatform has no kick).
 */
const allowed = [/^GetBroadcaster\.(connected|disconnected)\[\d+\]: expected "(twitch|youtube|trovo|vstream)", got "kick"$/];

function expectType(what, value, typeName) {
  const errors = [];
  check(value, aliasType(typeName), what, errors);
  const real = errors.filter((e) => !allowed.some((a) => a.test(e)));
  for (const e of real) fail(typeName, e);
  checked.push(`${what} as ${typeName}${real.length ? ' (FAILED)' : ''}`);
}

const twitchEvents = aliasType('TwitchEvents');

// ---- Connect, subscribe, receive ------------------------------------------------------------------------------

const received = new Map();
let errorCount = 0;
let onConnect;
const connected = new Promise((resolve) => { onConnect = resolve; });
const client = new StreamerbotClient({
  host: '127.0.0.1', port: config.port, password: config.password, immediate: false, autoReconnect: false,
  logLevel: 'error', onConnect: (info) => onConnect(info), onError: () => { errorCount++; },
});

const withTimeout = (promise, ms, what) => Promise.race([
  promise, new Promise((_, reject) => setTimeout(() => reject(new Error(`${what} timed out`)), ms)),
]);

try {
  await withTimeout(client.connect(), 10000, 'connect');
  const info = await withTimeout(connected, 10000, 'handshake');
  expectType('Hello.info', info, 'StreamerbotInfo');
  if (!client.authenticated) fail('Authenticate', 'the client is not authenticated');

  // client.on() subscribes right away when connected (only to sources GetEvents lists) and resolves afterwards.
  for (const name of config.events) {
    await client.on(name, (payload) => { if (!received.has(name)) received.set(name, payload); });
  }
  process.stdout.write('READY\n');

  const deadline = Date.now() + 20000;
  while (received.size < config.events.length && Date.now() < deadline) await new Promise((r) => setTimeout(r, 50));
  for (const name of config.events) {
    const payload = received.get(name);
    if (!payload) {
      fail(name, 'event not received through client.on()');
      continue;
    }
    const [source, type] = name.split('.');
    if (payload.event?.source !== source || payload.event?.type !== type) fail(name, `event is ${JSON.stringify(payload.event)}`);
    if (typeof payload.timeStamp !== 'string' || Number.isNaN(Date.parse(payload.timeStamp))) fail(name, 'timeStamp is not a date');
    if (typeof payload.data !== 'object' || payload.data === null) fail(name, 'data is not an object');
    if (source !== 'Twitch') continue;
    const prop = checker.getPropertyOfType(twitchEvents, type);
    if (!prop) {
      fail(name, 'not a Twitch event in the client types');
      continue;
    }
    const dataType = propertyType(prop);
    if (dataType.flags & ts.TypeFlags.Undefined) continue; // the client documents no payload
    const errors = [];
    check(payload.data, dataType, `${name}.data`, errors);
    for (const e of errors) fail(`TwitchEvents["${type}"]`, e);
    checked.push(`${name}.data as TwitchEvents["${type}"]${errors.length ? ' (FAILED)' : ''}`);
  }

  // ---- Every request the client has ---------------------------------------------------------------------------

  const call = async (what, run, typeName) => {
    try {
      const response = await withTimeout(run(), 15000, what);
      if (response?.status !== 'ok') fail(what, `status ${response?.status}: ${response?.error}`);
      if (typeName) expectType(what, response, typeName);
      return response;
    } catch (e) {
      fail(what, e.message);
      return undefined;
    }
  };

  const events = await call('GetEvents', () => client.getEvents());
  if (events && Object.values(events.events).some((types) => !Array.isArray(types) || types.some((t) => typeof t !== 'string'))) {
    fail('GetEvents', 'events is not {source: string[]}');
  }
  await call('Subscribe', () => client.subscribe({ Twitch: ['Follow'] }), 'SubscribeResponse');
  await call('UnSubscribe', () => client.unsubscribe({ Twitch: ['Follow'] }), 'UnsubscribeResponse');
  await call('GetInfo', () => client.getInfo(), 'GetInfoResponse');
  const actions = await call('GetActions', () => client.getActions(), 'GetActionsResponse');
  const responder = actions?.actions.find((a) => a.name === config.responseAction);
  await call('DoAction', () => client.doAction({ name: config.responseAction }, { from: 'node' }), 'DoActionResponse');
  const custom = await call('DoAction customEventResponse',
    () => client.doAction(responder?.id ?? config.responseAction, { from: 'node' }, { customEventResponse: true }), 'DoActionResponse');
  if (custom && custom.customEventResponseArgs?.from !== 'node') fail('DoAction customEventResponse', `args ${JSON.stringify(custom.customEventResponseArgs)}`);
  await call('GetBroadcaster', () => client.getBroadcaster(), 'GetBroadcasterResponse');
  await call('GetMonitoredYouTubeBroadcasts', () => client.getMonitoredYouTubeBroadcasts(), 'GetMonitoredYouTubeBroadcastsResponse');
  await call('GetCredits', () => client.getCredits(), 'GetCreditsResponse');
  await call('TestCredits', () => client.testCredits(), 'TestCreditsResponse');
  await call('ClearCredits', () => client.clearCredits(), 'ClearCreditsResponse');
  await call('GetActiveViewers', () => client.getActiveViewers(), 'GetActiveViewersResponse');
  await call('ExecuteCodeTrigger', () => client.executeCodeTrigger('channel_created', { from: 'node' }), 'ExecuteCodeTriggerResponse');
  const triggered = await call('ExecuteCodeTrigger customEventResponse',
    () => client.executeCodeTrigger('channel_created', { from: 'trigger' }, { customEventResponse: true }), 'ExecuteCodeTriggerResponse');
  if (triggered && triggered.customEventResponseArgs?.from !== 'trigger') fail('ExecuteCodeTrigger customEventResponse', 'no response args');
  await call('GetCodeTriggers', () => client.getCodeTriggers(), 'GetCodeTriggersResponse');
  await call('GetCommands', () => client.getCommands(), 'GetCommandsResponse');
  const twitchEmotes = await call('TwitchGetEmotes', () => client.getEmotes('twitch'), 'TwitchGetEmotesResponse');
  if (twitchEmotes && !twitchEmotes.emotes.sevenTvEmotes.some((e) => e.name === 'catJAM')) fail('TwitchGetEmotes', 'no 7TV emote');
  await call('YouTubeGetEmotes', () => client.getEmotes('youtube'), 'YouTubeGetEmotesResponse');
  await call('GetGlobals', () => client.getGlobals(), 'GetGlobalsResponse');
  const global = await call('GetGlobal', () => client.getGlobal('score'));
  if (global && global.variable?.value !== 4) fail('GetGlobal', `client.getGlobal gave ${JSON.stringify(global)}`);
  for (const platform of ['twitch', 'youtube', 'kick', 'trovo']) {
    await call(`GetUserGlobals ${platform}`, () => client.getUserGlobals(platform, 'tuna'), 'GetUserGlobalsResponse');
  }
  await call('GetUserGlobal', () => client.getUserGlobal('twitch', '42'), 'GetUserGlobalResponse');
  const userGlobal = await call('GetUserGlobal with name', () => client.getUserGlobal('twitch', '42', 'tuna'));
  if (userGlobal && userGlobal.variable?.value !== 3) fail('GetUserGlobal with name', `client.getUserGlobal gave ${JSON.stringify(userGlobal)}`);
  await call('SendMessage', () => client.sendMessage('twitch', 'hello from node', { bot: true, internal: false, replyId: 'm1' }), 'SendMessageResponse');
  const pronouns = await call('GetUserPronouns', () => client.getUserPronouns('twitch', 'ann'), 'GetUserPronounsResponse');
  if (pronouns && pronouns.pronoun?.pronounSubject !== 'She') fail('GetUserPronouns', `pronoun ${JSON.stringify(pronouns.pronoun)}`);
} catch (e) {
  fail('client', e.stack ?? String(e));
} finally {
  try { await client.disconnect(); } catch { /* closing anyway */ }
}

process.stdout.write(`RESULT ${JSON.stringify({ failures, checked, received: [...received.keys()], errors: errorCount })}\n`);
process.exit(failures.length ? 1 : 0);
