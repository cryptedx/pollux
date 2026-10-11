const host = window.chrome.webview;
const send = m => host.postMessage(JSON.stringify(m));
const $ = id => document.getElementById(id);
const chat = $('chat'), scroller = chat.parentElement;
const KIND = { location: 'Folder', selection: 'Selection', pane: 'Other Pane', tab: 'Tab', file: 'File' };
let defaults = [];            // chips Pollux proposes for the next message (location, selection of the active Pane)
let extras = [];              // chips the user added with @ or by dropping entries
const dismissed = new Set();  // default chips the user removed (a changed selection has a new id and comes back)
const answers = new Map();   // message id -> { el, text }
const tools = new Map();     // tool call id -> element
const approvals = new Map(); // approval id -> element
let busy = false, typing = null, pendingRender = new Set(), frame = 0;

function atBottom() { return scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 40; }
function append(el) {
  const stick = atBottom();
  $('empty')?.remove();
  if (typing && typing.parentElement === chat) chat.insertBefore(el, typing); else chat.appendChild(el);
  if (stick) scroller.scrollTop = 1e9;
  return el;
}
function div(cls, text) { const d = document.createElement('div'); d.className = cls; if (text !== undefined) d.textContent = text; return d; }

function answer(id) {
  let a = answers.get(id);
  if (!a) { a = { el: append(div('msg')), text: '' }; answers.set(id, a); }
  return a;
}
// Renders Markdown into el; if the renderer fails on malformed output the raw text is shown instead (S-16).
function setMarkdown(el, text) {
  try { el.innerHTML = renderMarkdown(text); }
  catch (e) { el.textContent = text; }
}
// Streaming re-renders at most once per frame.
function scheduleRender(a) {
  pendingRender.add(a);
  if (frame) return;
  frame = requestAnimationFrame(() => {
    const stick = atBottom();
    try { for (const x of pendingRender) setMarkdown(x.el, x.text); }
    finally { pendingRender.clear(); frame = 0; } // a failed render must not stop later streaming
    if (stick) scroller.scrollTop = 1e9;
  });
}

function setBusy(on) {
  busy = on;
  $('send').hidden = on; $('stop').hidden = !on;
  if (on && !typing) { typing = div('typing'); chat.appendChild(typing); scroller.scrollTop = 1e9; }
  if (!on && typing) { typing.remove(); typing = null; }
}

// ---- context chips: exactly the chips shown here are the ones sent with the message ----
function shownChips() { return [...defaults.filter(c => !dismissed.has(c.id)), ...extras.filter(c => !defaults.some(d => d.id === c.id))]; }
function chipEl(c, removable) {
  const el = div('ctx' + (removable ? '' : ' sent'));
  el.title = c.title || c.label;
  const k = document.createElement('span'); k.className = 'kind'; k.textContent = KIND[c.kind] || 'File';
  const l = document.createElement('span'); l.className = 'label'; l.textContent = c.label;
  el.append(k, l);
  if (removable) {
    const x = document.createElement('button'); x.textContent = '\u2715'; x.title = 'Remove from this message';
    x.onclick = () => { dismissed.add(c.id); extras = extras.filter(e => e.id !== c.id); renderChips(); $('input').focus(); };
    el.appendChild(x);
  }
  return el;
}
function renderChips() { const box = $('chips'); box.replaceChildren(...shownChips().map(c => chipEl(c, true))); }

// ---- @ mentions ----
let mention = null; // { start, query, items, index }
function mentionAt() {
  const i = $('input'), pos = i.selectionStart, before = i.value.slice(0, pos);
  const m = /(^|\s)@([^\s@]*)$/.exec(before);
  return m ? { start: pos - m[2].length - 1, query: m[2] } : null;
}
function closeMention() { mention = null; $('mentions').hidden = true; }
function renderMentions() {
  const box = $('mentions');
  if (!mention || !mention.items.length) { box.hidden = true; return; }
  box.replaceChildren(...mention.items.map((c, n) => {
    const it = div('item' + (n === mention.index ? ' sel' : ''));
    it.title = c.title || c.label;
    const k = document.createElement('span'); k.className = 'kind'; k.textContent = KIND[c.kind] || 'File';
    const l = document.createElement('span'); l.className = 'label'; l.textContent = c.label;
    it.append(k, l);
    it.onmousedown = e => { e.preventDefault(); pickMention(c); };
    return it;
  }));
  box.hidden = false;
}
function pickMention(c) {
  const i = $('input');
  if (mention) { i.value = i.value.slice(0, mention.start) + i.value.slice(i.selectionStart); i.selectionStart = i.selectionEnd = mention.start; }
  dismissed.delete(c.id);
  if (!extras.some(e => e.id === c.id)) extras.push(c);
  closeMention(); renderChips(); autosize(); i.focus();
}
function updateMention() {
  const m = mentionAt();
  if (!m) { closeMention(); return; }
  mention = { ...m, items: mention ? mention.items : [], index: 0 };
  send({ type: 'mention', query: m.query });
}

function showApproval(m) {
  const card = div('approval');
  card.appendChild(div('kind', 'Copilot wants to: ' + m.kind));
  card.appendChild(div('what', m.title));
  if (m.detail) { const pre = document.createElement('pre'); const c = document.createElement('code'); c.textContent = m.detail; pre.appendChild(c); card.appendChild(pre); }
  if (m.diff) { const pre = document.createElement('pre'); const c = document.createElement('code'); c.innerHTML = highlightCode(m.diff, 'diff'); pre.appendChild(c); card.appendChild(pre); }
  // Paths only when they add to what the detail already shows (e.g. the files a command touches).
  const paths = (m.paths || []).filter(p => p !== m.detail);
  if (paths.length) card.appendChild(div('paths', 'Paths: ' + paths.join(', ')));
  if (m.reason) card.appendChild(div('reason', 'Guard Rules: ' + m.reason));
  const buttons = div('buttons');
  const yes = document.createElement('button'); yes.className = 'primary'; yes.textContent = 'Allow';
  const chatWide = document.createElement('button'); chatWide.className = 'secondary'; chatWide.textContent = 'Allow for this Chat';
  const no = document.createElement('button'); no.className = 'secondary'; no.textContent = 'Deny';
  const answer = (approved, forChat) => { yes.disabled = chatWide.disabled = no.disabled = true; send({ type: 'approve', id: m.id, approved, forChat }); };
  yes.onclick = () => answer(true, false);
  chatWide.onclick = () => answer(true, true);
  no.onclick = () => answer(false, false);
  // "Allow for this Chat" only for questions of the Permission Mode; Guard Rules questions are asked every time.
  if (m.allowForChat) buttons.append(yes, chatWide, no); else buttons.append(yes, no);
  card.appendChild(buttons);
  approvals.set(m.id, card);
  append(card);
  yes.focus();
}

let showList = false, showLog = false, stateOn = false, chatList = [], currentChat = '';
const DECISION = { allowed: 'Allowed', asked: 'Asked, allowed', blocked: 'Blocked' };
const DECIDER = { mode: 'Permission Mode', guardRule: 'Guard Rule', user: 'You' };
// The Action Log of the open Chat, newest first; text only (textContent), never markup.
function renderLog(entries) {
  const items = $('logItems');
  items.textContent = '';
  if (!entries.length) items.appendChild(div('none', 'No tool calls in this Chat yet.'));
  for (const e of [...entries].reverse()) {
    const row = div('act ' + e.decision);
    const head = div('head');
    const badge = div('badge', DECISION[e.decision] || e.decision);
    head.append(div('tool-name', e.tool), badge, div('time', new Date(e.time).toLocaleTimeString()));
    row.appendChild(head);
    if (e.command) row.appendChild(div('cmd', e.command));
    const where = [...e.paths, ...e.urls];
    if (where.length) row.appendChild(div('detail', where.join('\n')));
    row.appendChild(div('why', 'Decided by ' + (DECIDER[e.decidedBy] || e.decidedBy) + (e.reason ? ': ' + e.reason : '')));
    items.appendChild(row);
  }
}
function renderList() {
  const other = showList || showLog;
  $('list').style.display = showList && !stateOn ? 'block' : 'none';
  $('log').style.display = showLog && !showList && !stateOn ? 'block' : 'none';
  chat.style.display = other || stateOn ? 'none' : 'block';
  $('composer').style.display = other || stateOn ? 'none' : 'flex';
  $('quota').style.display = other || stateOn ? 'none' : '';
  const items = $('items');
  items.textContent = '';
  if (!chatList.length) items.appendChild(div('none', 'No Chats yet.'));
  for (const c of chatList) {
    // Opening and deleting are two separate buttons (keyboard: Tab to either, Enter or Space).
    const row = div('item' + (c.id === currentChat ? ' current' : ''));
    const title = c.title || 'Chat';
    const open = document.createElement('button');
    open.type = 'button'; open.className = 'open'; open.title = 'Open Chat';
    if (c.id === currentChat) open.setAttribute('aria-current', 'true');
    open.append(div('name', title), div('meta', new Date(c.modified).toLocaleString() + (c.workingFolder ? ' \u00B7 ' + c.workingFolder : '')));
    open.onclick = () => { showList = false; renderList(); send({ type: 'openChat', id: c.id }); };
    const del = document.createElement('button');
    del.type = 'button'; del.className = 'del'; del.textContent = '\u2715'; del.title = 'Delete Chat';
    del.setAttribute('aria-label', 'Delete Chat ' + title);
    del.onclick = () => send({ type: 'deleteChat', id: c.id });
    row.append(open, del);
    items.appendChild(row);
  }
}
function fillModes(select, modes, current, what) {
  select.replaceChildren(...modes.map(o => {
    const opt = document.createElement('option');
    opt.value = o.id;
    opt.textContent = o.blocked ? o.label + ' (blocked)' : o.label;
    opt.disabled = !!o.blocked;
    if (o.blocked) opt.title = o.blocked;
    return opt;
  }));
  select.value = current;
  const blocked = modes.filter(o => o.blocked).map(o => o.label + ': ' + o.blocked);
  select.title = [what, ...blocked].join('\n');
}
// What the empty Chat says about approvals follows the Chat's Permission Mode (mode-neutral until Pollux sends it).
let permissionMode = null;
function emptyText() {
  switch (permissionMode) {
    case 'manual': return 'Ask Copilot anything. Every tool it wants to use asks you first.';
    case 'assisted': return 'Ask Copilot anything. A second model approves the tools it judges safe and asks you about the rest; the Guard Rules still apply.';
    case 'allowAll': return 'Ask Copilot anything. Allow All: tools run without asking you, except where the Guard Rules block or ask.';
    default: return 'Ask Copilot anything. The Guard Rules apply in every Permission Mode.';
  }
}
function resetChat() {
  answers.clear(); tools.clear(); approvals.clear(); pendingRender.clear();
  setBusy(false);
  chat.textContent = '';
  chat.appendChild(Object.assign(div('', emptyText()), { id: 'empty' }));
  handlers.header({ workingFolder: null, sandbox: false, tainted: false });
}

const handlers = {
  clear: () => { showList = false; resetChat(); renderList(); if (showLog) send({ type: 'actionLog' }); },
  actionLog: m => renderLog(m.entries),
  chats: m => { chatList = m.chats; currentChat = m.current; renderList(); },
  theme: m => document.documentElement.classList.toggle('dark', !!m.dark),
  header: m => {
    $('folder').textContent = m.workingFolder || 'no Working Folder';
    $('folder').title = (m.workingFolder ? 'Working Folder: ' + m.workingFolder : 'No Working Folder: every write asks') + '\nWrites outside it always ask. Click to change.';
    $('tainted').hidden = !m.tainted;
    $('tainted').title = m.tainted ? 'Tainted Chat: ' + (m.taintedBy || 'it has read untrusted content') + '\nAsks before shell, network and write actions. Click to clear.' : 'Tainted Chat';
    $('taintedBy').textContent = m.taintedBy ? ' (' + m.taintedBy + ')' : '';
    if (!m.tainted) $('taintMenu').hidden = true;
  },
  // Agent Mode and Permission Mode switchers: Pollux sends the Chat's modes and every mode on offer; a Permission Mode
  // blocked by the organization's policy is disabled with the reason. A switch is only a request: Pollux answers with
  // the modes the Chat really has (so a refused switch snaps back).
  modes: m => {
    fillModes($('agent'), m.agentModes, m.agentMode, 'Agent Mode');
    fillModes($('permission'), m.permissionModes, m.permissionMode, 'Permission Mode');
    const allowAll = m.permissionMode === 'allowAll';
    permissionMode = m.permissionMode;
    const empty = $('empty');
    if (empty) empty.textContent = emptyText();
    $('permission').classList.toggle('allowAll', allowAll);
    $('allowAllWarning').hidden = !allowAll;
  },
  // Sandbox toggle: on/off for the Chat; locked (disabled, reason as tooltip) when the organization requires it or this Windows build cannot run it.
  sandbox: m => {
    const b = $('sandbox');
    b.textContent = 'Sandbox: ' + (m.enabled ? 'on' : 'off');
    b.classList.toggle('on', !!m.enabled);
    b.disabled = !!m.locked;
    b.dataset.enabled = m.enabled ? '1' : '';
    b.title = m.locked ? 'Sandbox: ' + m.locked : "Sandbox: runs commands in the CLI's local sandbox. Click to switch.";
  },
  models: m => {
    const sel = $('model'), keep = sel.value;
    sel.textContent = '';
    const add = (id, name) => { const o = document.createElement('option'); o.value = id; o.textContent = name; sel.appendChild(o); };
    add('', 'Default model');
    for (const x of m.models) add(x.id, x.name);
    sel.value = [...sel.options].some(o => o.value === keep) ? keep : '';
    sel.hidden = m.models.length === 0;
  },
  model: m => { const sel = $('model'); if ([...sel.options].some(o => o.value === (m.id || ''))) sel.value = m.id || ''; },
  quota: m => {
    const q = $('quota');
    q.hidden = !m.text;
    q.textContent = m.text || '';
    q.className = m.level === 'low' || m.level === 'exhausted' ? m.level : '';
  },
  state: m => {
    const on = !!m.kind;
    stateOn = on;
    $('state').style.display = on ? 'block' : 'none';
    renderList();
    $('stateTitle').textContent = m.title;
    $('stateMessage').textContent = m.message;
    const b = $('stateAction');
    b.hidden = !m.action;
    b.textContent = m.actionLabel || '';
    b.onclick = m.action ? () => send({ type: 'action', id: m.action }) : null;
    if (!on) $('input').focus();
  },
  markdown: m => setMarkdown(append(div('msg')), m.text),
  user: m => {
    const el = div('user'); el.appendChild(document.createTextNode(m.text));
    if (m.chips && m.chips.length) { const box = div('ctxs'); box.append(...m.chips.map(c => chipEl(c, false))); el.appendChild(box); }
    append(el);
    extras = []; renderChips(); // added chips belong to that message only
  },
  context: m => { defaults = m.chips || []; renderChips(); },
  mentions: m => {
    if (!mention || m.query !== mention.query) return;
    mention.items = m.chips || []; mention.index = 0; renderMentions();
  },
  chipsAdded: m => {
    for (const c of m.chips || []) { dismissed.delete(c.id); if (!extras.some(e => e.id === c.id)) extras.push(c); }
    renderChips();
  },
  answerDelta: m => { const a = answer(m.id); a.text += m.text; scheduleRender(a); },
  answer: m => { const a = answer(m.id); a.text = m.text; scheduleRender(a); },
  tool: m => {
    let el = tools.get(m.id);
    if (!el) { el = append(div('tool')); el.dataset.name = m.name || 'tool'; tools.set(m.id, el); }
    const name = el.dataset.name;
    el.className = 'tool' + (m.status === 'failed' ? ' failed' : '');
    el.textContent = m.status === 'running' ? '▶ ' + name + '…' : m.status === 'done' ? '✓ ' + name : '✕ ' + name + (m.error ? ': ' + m.error : '');
    if (showLog) send({ type: 'actionLog' });
  },
  chatError: m => append(div('error', m.message)),
  warning: m => { const w = div('warning', '\u26A0 ' + m.message); w.setAttribute('role', 'alert'); append(w); },
  busy: m => setBusy(!!m.busy),
  approval: showApproval,
  approvalDone: m => {
    if (showLog) send({ type: 'actionLog' });
    const card = approvals.get(m.id);
    if (!card) return;
    approvals.delete(m.id);
    card.querySelector('.buttons').replaceWith(div('outcome', m.answer === 'allowForChat' ? 'Allowed for this Chat' : m.approved ? 'Allowed once' : 'Denied'));
  },
};

host.addEventListener('message', e => {
  const m = JSON.parse(e.data);
  const h = handlers[m.type];
  if (h) h(m);
});

function submit() {
  const text = $('input').value;
  if (busy || !text.trim()) return;
  send({ type: 'send', text, chips: shownChips().map(c => c.id) });
  $('input').value = ''; autosize(); closeMention();
}
function autosize() { const i = $('input'); i.style.height = 'auto'; i.style.height = Math.min(i.scrollHeight, 160) + 'px'; }
$('input').addEventListener('input', () => { autosize(); updateMention(); });
$('input').addEventListener('focus', () => send({ type: 'contextRequest' }));
$('input').addEventListener('blur', () => setTimeout(closeMention, 100));
$('input').addEventListener('keydown', e => {
  if (mention && mention.items.length) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const n = mention.items.length;
      mention.index = (mention.index + (e.key === 'ArrowDown' ? 1 : n - 1)) % n; renderMentions();
      return;
    }
    if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); pickMention(mention.items[mention.index]); return; }
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeMention(); return; }
  }
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); }
  if (e.key === 'Backspace' && !$('input').value) { const c = shownChips().at(-1); if (c) { dismissed.add(c.id); extras = extras.filter(x => x.id !== c.id); renderChips(); } }
});
// Entries dragged from a Pane: the host reads their paths from the dropped files.
for (const t of ['dragenter', 'dragover']) document.addEventListener(t, e => { e.preventDefault(); $('composer').classList.add('dropping'); });
document.addEventListener('dragleave', e => { if (!e.relatedTarget) $('composer').classList.remove('dropping'); });
document.addEventListener('drop', e => {
  e.preventDefault(); $('composer').classList.remove('dropping');
  if (e.dataTransfer && e.dataTransfer.files.length) host.postMessageWithAdditionalObjects(JSON.stringify({ type: 'drop' }), [...e.dataTransfer.files]);
});
document.addEventListener('keydown', e => { if (e.key === 'Escape' && busy) send({ type: 'cancel' }); });
$('model').addEventListener('change', e => send({ type: 'selectModel', id: e.target.value || null }));
$('send').addEventListener('click', submit);
$('stop').addEventListener('click', () => send({ type: 'cancel' }));
$('newChat').addEventListener('click', () => { showList = false; send({ type: 'newChat' }); });
// Working Folder: the active Pane's folder or one picked in a dialog; Pollux answers with the header it really has.
const folderMenu = $('folderMenu');
const toggleFolderMenu = open => {
  folderMenu.hidden = !open;
  if (open) { folderMenu.style.left = $('folder').offsetLeft + 'px'; $('folderActive').focus(); }
};
$('folder').addEventListener('click', e => { e.stopPropagation(); toggleFolderMenu(folderMenu.hidden); });
$('folder').addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleFolderMenu(true); } });
$('folderActive').addEventListener('click', () => { toggleFolderMenu(false); send({ type: 'useActiveFolder' }); });
$('folderChoose').addEventListener('click', () => { toggleFolderMenu(false); send({ type: 'chooseWorkingFolder' }); });
document.addEventListener('click', e => { if (!folderMenu.hidden && !folderMenu.contains(e.target)) toggleFolderMenu(false); });
folderMenu.addEventListener('keydown', e => { if (e.key === 'Escape') { toggleFolderMenu(false); $('folder').focus(); } });
// Tainted marker: explains why and lets the user clear it (Pollux answers with the header it really has).
const taintMenu = $('taintMenu');
$('tainted').addEventListener('click', e => {
  e.stopPropagation();
  taintMenu.hidden = !taintMenu.hidden;
  if (!taintMenu.hidden) { taintMenu.style.left = $('tainted').offsetLeft + 'px'; $('clearTainted').focus(); }
});
$('clearTainted').addEventListener('click', () => { taintMenu.hidden = true; send({ type: 'clearTainted' }); });
document.addEventListener('click', e => { if (!taintMenu.hidden && !taintMenu.contains(e.target)) taintMenu.hidden = true; });
taintMenu.addEventListener('keydown', e => { if (e.key === 'Escape') { taintMenu.hidden = true; $('tainted').focus(); } });
$('chats').addEventListener('click', () => { showList = !showList; renderList(); });
$('actionLogButton').addEventListener('click', () => { showLog = !showLog; showList = false; renderList(); if (showLog) send({ type: 'actionLog' }); });
$('close').addEventListener('click', () => send({ type: 'close' }));
$('sandbox').addEventListener('click', e => send({ type: 'setSandbox', enabled: !e.currentTarget.dataset.enabled }));
$('agent').addEventListener('change', e => send({ type: 'setAgentMode', mode: e.target.value }));
$('permission').addEventListener('change', e => send({ type: 'setPermissionMode', mode: e.target.value }));
document.addEventListener('click', e => {
  const a = e.target.closest('a[data-href]');
  if (a) { e.preventDefault(); send({ type: 'openLink', url: a.getAttribute('data-href') }); }
});
send({ type: 'ready' });
