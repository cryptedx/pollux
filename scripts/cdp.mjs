#!/usr/bin/env node
// Talks to a WebView2 page over the Chrome DevTools Protocol (Node 22+: global fetch and WebSocket).
// scripts/vm.sh uses it for panel-eval/panel-shot through an ssh tunnel; it works against any CDP port.
//
//   node scripts/cdp.mjs [--port 19222] [--page copilot.html] list
//   node scripts/cdp.mjs [--port 19222] [--page copilot.html] eval "<js>" ["<js>" ...]
//   node scripts/cdp.mjs [--port 19222] [--page copilot.html] shot <out.png>
//
// eval prints one JSON line per expression ({"value": ...} or {"exception": "..."}), plus console output
// and uncaught exceptions the page reports while it runs. Exit code 1 when an expression threw.
import { writeFileSync } from 'node:fs';

const usage = 'usage: cdp.mjs [--port N] [--page <url substring>] (list | eval <js>... | shot <out.png>)';
const argv = process.argv.slice(2);
let port = 19222;
let page = 'copilot.html';
while (argv[0]?.startsWith('--')) {
  const flag = argv.shift();
  const value = argv.shift();
  if (value === undefined) fail(usage);
  if (flag === '--port') port = Number(value);
  else if (flag === '--page') page = value;
  else fail(`unknown option ${flag}\n${usage}`);
}
const [command, ...rest] = argv;
if (!command) fail(usage);

function fail(message) {
  console.error(message);
  process.exit(2);
}

let targets;
try {
  targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
} catch (e) {
  fail(`no DevTools endpoint on 127.0.0.1:${port} (${e.cause?.code ?? e.message}). Is Pollux running with remote debugging and the tunnel up?`);
}
const pages = targets.filter(t => t.type === 'page');

if (command === 'list') {
  for (const t of pages) console.log(`${t.id}  ${t.title}  ${t.url}`);
  process.exit(0);
}

const target = pages.find(t => t.url.includes(page));
if (!target) {
  fail(`no page whose URL contains "${page}". Open pages:\n${pages.map(t => `  ${t.url}`).join('\n') || '  (none)'}\n` +
    'For the Copilot panel: scripts/vm.sh command copilot.togglePanel');
}

const ws = new WebSocket(target.webSocketDebuggerUrl);
let nextId = 0;
const pending = new Map();
const call = (method, params = {}) => new Promise((resolve, reject) => {
  const id = ++nextId;
  pending.set(id, { resolve, reject });
  ws.send(JSON.stringify({ id, method, params }));
});
ws.onmessage = event => {
  const m = JSON.parse(event.data);
  if (m.id && pending.has(m.id)) {
    const { resolve, reject } = pending.get(m.id);
    pending.delete(m.id);
    if (m.error) reject(new Error(`${m.error.message} (${m.error.code})`));
    else resolve(m.result);
  } else if (m.method === 'Runtime.consoleAPICalled') {
    console.log(`console.${m.params.type}:`, ...m.params.args.map(a => a.value ?? a.description));
  } else if (m.method === 'Runtime.exceptionThrown') {
    console.log('page exception:', m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text);
  }
};
await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error('WebSocket failed')); });

let failed = false;
try {
  if (command === 'eval') {
    if (rest.length === 0) fail(usage);
    await call('Runtime.enable');
    for (const expression of rest) {
      const r = await call('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, replMode: true });
      if (r.exceptionDetails) {
        failed = true;
        console.log(JSON.stringify({ exception: r.exceptionDetails.exception?.description ?? r.exceptionDetails.text }));
      } else {
        console.log(JSON.stringify({ value: r.result.value ?? r.result.description ?? null }));
      }
    }
    // Console output of async work the expressions started arrives a little later.
    await new Promise(r => setTimeout(r, 300));
  } else if (command === 'shot') {
    if (!rest[0]) fail(usage);
    const r = await call('Page.captureScreenshot', { format: 'png' });
    writeFileSync(rest[0], Buffer.from(r.data, 'base64'));
    console.log(rest[0]);
  } else {
    fail(`unknown command ${command}\n${usage}`);
  }
} finally {
  ws.close();
}
process.exit(failed ? 1 : 0);
