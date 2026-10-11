// Small, dependency-free Markdown renderer for the Copilot Panel (headings, paragraphs, lists, quotes, rules,
// fenced and inline code, bold, italic, links). All text is HTML-escaped first and raw HTML is never passed
// through; links become <a data-href> (only http/https) and are opened by Pollux, never navigated in the page.
(function (root) {
  const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

  function inline(text) {
    const codes = [];
    let s = text.replace(/`([^`]+)`/g, (_, c) => { codes.push(c); return '\u0000' + (codes.length - 1) + '\u0000'; });
    s = esc(s);
    s = s.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, (_, label, url) => '<a data-href="' + url + '">' + label + '</a>');
    s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    s = s.replace(/(^|[^*])\*([^*\s][^*]*)\*/g, '$1<em>$2</em>');
    // Placeholders are only ever ones made above: render() strips NUL from the input. A missing index renders empty.
    return s.replace(/\u0000(\d+)\u0000/g, (_, i) => i in codes ? '<code>' + esc(codes[+i]) + '</code>' : '');
  }

  // Small syntax highlighter for fenced code: diffs by line; other languages by token (comments, strings, numbers,
  // keywords). Works on raw text and escapes every piece, so the result is safe HTML.
  const KEYWORDS = new Set(('abstract and as async await bool break case catch class const continue def default del do double ' +
    'elif else enum except export extends false final finally fn for foreach from func function get go if impl implements import ' +
    'in int interface internal is let match module namespace new nil none not null or override package param private protected ' +
    'pub public readonly record return self set static string struct super switch this throw true try type typeof use using ' +
    'var void while with yield begin end then fi done esac local echo exit function').split(' '));
  const HASH_COMMENT = /^(py|python|sh|bash|zsh|shell|ps|ps1|powershell|pwsh|rb|ruby|yaml|yml|toml|r|perl|makefile|dockerfile|conf|ini)$/i;
  const span = (cls, text) => '<span class="hl-' + cls + '">' + esc(text) + '</span>';

  function highlight(code, lang) {
    lang = (lang || '').toLowerCase();
    if (lang === 'diff' || lang === 'patch') {
      return code.split('\n').map(l =>
        /^(\+\+\+|---)/.test(l) ? span('meta', l) : l[0] === '+' ? span('add', l) : l[0] === '-' ? span('del', l) :
        l.startsWith('@@') ? span('meta', l) : esc(l)).join('\n');
    }
    if (!lang || lang === 'text' || lang === 'plaintext' || lang === 'md' || lang === 'markdown') return esc(code);
    const hash = HASH_COMMENT.test(lang);
    const re = hash
      ? /(#[^\n]*)|("(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*')|(\b\d[\d_.]*\b)|([A-Za-z_$][\w$-]*)/g
      : /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|("(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`)|(\b\d[\d_.]*\b)|([A-Za-z_$][\w$]*)/g;
    let out = '', last = 0, m;
    while ((m = re.exec(code))) {
      out += esc(code.slice(last, m.index));
      if (m[1]) out += span('comment', m[1]);
      else if (m[2]) out += span('string', m[2]);
      else if (m[3]) out += span('number', m[3]);
      else out += KEYWORDS.has(m[4].toLowerCase()) ? span('keyword', m[4]) : esc(m[4]);
      last = re.lastIndex;
    }
    return out + esc(code.slice(last));
  }

  function render(md) {
    // NUL never belongs in Chat text and is the inline-code placeholder delimiter: drop it so the model's output
    // cannot forge placeholders (S-16).
    const lines = String(md).replace(/\u0000/g, '').replace(/\r\n?/g, '\n').split('\n');
    const out = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      const fence = line.match(/^```\s*([\w+-]*)\s*$/);
      if (fence) {
        const body = [];
        i++;
        while (i < lines.length && !/^```\s*$/.test(lines[i])) body.push(lines[i++]);
        i++;
        const cls = fence[1] ? ' class="lang-' + esc(fence[1]) + '"' : '';
        out.push('<pre><code' + cls + '>' + highlight(body.join('\n'), fence[1]) + '</code></pre>');
        continue;
      }
      const h = line.match(/^(#{1,3})\s+(.*)$/);
      if (h) { out.push('<h' + h[1].length + '>' + inline(h[2]) + '</h' + h[1].length + '>'); i++; continue; }
      if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { out.push('<hr>'); i++; continue; }
      if (/^>\s?/.test(line)) {
        const q = [];
        while (i < lines.length && /^>\s?/.test(lines[i])) q.push(lines[i++].replace(/^>\s?/, ''));
        out.push('<blockquote>' + inline(q.join(' ')) + '</blockquote>');
        continue;
      }
      const li = line.match(/^\s*([-*]|\d+\.)\s+(.*)$/);
      if (li) {
        const ordered = /\d/.test(li[1]);
        const items = [];
        while (i < lines.length && (m = lines[i].match(/^\s*([-*]|\d+\.)\s+(.*)$/)) && /\d/.test(m[1]) === ordered) { items.push('<li>' + inline(m[2]) + '</li>'); i++; }
        out.push((ordered ? '<ol>' : '<ul>') + items.join('') + (ordered ? '</ol>' : '</ul>'));
        continue;
      }
      if (line.trim() === '') { i++; continue; }
      const para = [];
      while (i < lines.length && lines[i].trim() !== '' && !/^(```|#{1,3}\s|>\s?|\s*([-*]|\d+\.)\s+)/.test(lines[i])) para.push(lines[i++]);
      out.push('<p>' + inline(para.join(' ')) + '</p>');
    }
    return out.join('\n');
  }

  var m;
  root.renderMarkdown = render;
  root.highlightCode = highlight;
  if (typeof module !== 'undefined') { module.exports = render; module.exports.highlight = highlight; }
})(typeof window !== 'undefined' ? window : globalThis);
