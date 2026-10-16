// node --test DN42Atlas.Tests/I2pViewerTests.cjs
// Dependency-free behavior checks of the separate I2P enhancement, using rendered-attribute-shaped fixtures.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const template = fs.readFileSync(path.join(__dirname, '../DN42Atlas/I2pReporting/viewer.html'), 'utf8');
const script = template.match(/<script>([\s\S]*?)<\/script>/)[1];

function element(id) {
    const attributes = new Map(), classes = new Set();
    return { id, value: '', hidden: false, disabled: false, events: {}, textContent: '',
        classList: { add(...names) { names.forEach(n => classes.add(n)); }, remove(...names) { names.forEach(n => classes.delete(n)); },
            toggle(n, active) { classes[active ? 'add' : 'delete'](n); }, contains(n) { return classes.has(n); } },
        setAttribute(n, v) { attributes.set(n, v); }, removeAttribute(n) { attributes.delete(n); }, getAttribute(n) { return attributes.get(n); },
        addEventListener(n, cb) { this.events[n] = cb; },
        getBoundingClientRect() { return { height: this.height ?? 140 }; },
        focus() { this.focused = true; }, select() { this.selectedText = true; }, scrollIntoView() { this.scrolled = true; },
        click() { if (!this.disabled) return this.events.click?.(); }
    };
}
function viewer({ hash = '', empty = false, restored = {}, failSort = false, clipboard, width = 1200 } = {}) {
    const ui = Object.fromEntries(['services', 'search', 'state', 'robots', 'discovery', 'controls', 'previous', 'next', 'random',
        'copy-link', 'copy-status', 'copy-fallback', 'copy-url', 'reset', 'counter', 'directory-count', 'no-matches', 'no-selection'].map(id => [id, element(id)]));
    Object.entries(restored).forEach(([id, value]) => { ui[id].value = value; });
    ui.controls.hidden = true;
    const data = empty ? [] : [
        ['a', 'plugins.i2p', 'alpha.b32.i2p', 'Zulu', 'HomepageResponseObserved', 'AllowsRoot', '200', '2', 'true', 'false'],
        ['b', 'plugins.i2p', 'beta.b32.i2p', '', 'HomepageAttemptFailed', 'AllowsRoot', '', '0', 'true', 'true'],
        ['c', 'alpha.i2p', 'gamma.b32.i2p', 'Alpha', 'HomepageResponseObserved', 'AllowsRoot', '200', '1', 'false', 'false'],
        ['d', 'delta.i2p', 'delta.b32.i2p', '', 'RequestTimedOut', 'NotObserved', '', '0', 'false', 'true']
    ];
    const rows = data.map(([id, hostname, destination, title, state, robots, http, discovered, multiple, incomplete]) => {
        const row = element('row-' + id), link = element('link-' + id);
        row.dataset = { service: 'i2p-service-' + id, hostname, identity: hostname + '\n' + destination, title, state, robots, http,
            discovered, multiple, incomplete, hasTitle: String(Boolean(title)),
            search: (hostname + ' ' + destination + ' ' + title + ' text/html outside.i2p').toLowerCase() };
        link.setAttribute('href', '#' + row.dataset.service);
        row.link = link;
        row.querySelector = () => link;
        return row;
    });
    const panels = rows.map(r => element(r.dataset.service));
    panels.forEach(p => { ui[p.id] = p; });
    const headers = ['hostname', 'title', 'state', 'http', 'discovered'].map(key => {
        const header = element('header-' + key), button = element('button-' + key), indicator = element('indicator-' + key);
        header.dataset = { sort: key, label: key };
        header.querySelector = s => s === 'button' ? button : indicator;
        return header;
    });
    ui.services.children = [...rows];
    ui.services.appendChild = row => {
        if (failSort) throw new Error('Injected sort failure');
        ui.services.children.splice(ui.services.children.indexOf(row), 1); ui.services.children.push(row);
    };
    const root = element('html'); root.style = { setProperty() {} };
    const document = { events: {}, documentElement: root, getElementById: id => ui[id],
        addEventListener(n, cb) { this.events[n] = cb; },
        querySelectorAll(s) {
            if (s === '#services tr') return rows;
            if (s === '.service-detail') return panels;
            if (s === '#services tr, .service-detail') return [...rows, ...panels];
            if (s === '#services a') return rows.map(r => r.link);
            if (s === '.sort-button') return headers.map(h => h.querySelector('button'));
            return headers;
        } };
    const location = new URL('https://atlas.example/i2p-index.html?keep=yes' + hash);
    const history = { calls: [], state: null, pushState(state, _, url) { this.calls.push('push'); location.href = new URL(url, location).href; },
        replaceState(state, _, url) { this.calls.push('replace'); location.href = new URL(url, location).href; } };
    const window = { innerWidth: width, innerHeight: 900, events: {}, addEventListener(n, cb) { this.events[n] = cb; } };
    const frames = [], math = Object.create(Math), navigator = { clipboard };
    let random = 0; math.random = () => random;
    vm.runInNewContext(script, { document, window, location, history, navigator, URL, Math: math,
        clearTimeout() {}, setTimeout() {}, requestAnimationFrame(fn) { frames.push(fn); }, console: { error() {} } });
    const pendingBeforeInit = root.classList.contains('js-pending');
    document.events.DOMContentLoaded();
    window.events.load?.();
    while (frames.length) frames.shift()();
    return { ui, rows, panels, headers, history, window, root, location, pendingBeforeInit,
        visible: () => ui.services.children.filter(r => !r.hidden).map(r => r.dataset.service.slice(12)),
        selected: () => panels.find(p => !p.hidden)?.id.slice(12),
        filter(id, value) { ui[id].value = value; ui[id].events[id === 'search' ? 'input' : 'change'](); },
        sort(key) { headers.find(h => h.dataset.sort === key).querySelector('button').click(); },
        visit(id, event = 'popstate') { location.hash = '#i2p-service-' + id; window.events[event](); },
        random(value) { random = value; ui.random.click(); },
        frames() { while (frames.length) frames.shift()(); },
        event(id, part = 'blank', options = {}) {
            const row = rows.find(r => r.dataset.service === 'i2p-service-' + id);
            const target = { isContentEditable: false, closest(selector) {
                if (selector === 'tr[data-service]') return row;
                if (selector === 'a[href^="#i2p-service-"]') return part === 'internal' ? row.link : null;
                if (selector.startsWith('a, button,')) return ['internal', 'external', 'button', 'summary', 'input'].includes(part) ? {} : null;
                return null;
            } };
            const event = { target, button: 0, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...options };
            ui.services.events.click(event); document.events.click(event); return event;
        },
        key(key, interactive = false, options = {}) {
            const event = { key, target: { closest: () => interactive ? {} : null }, preventDefault() { this.prevented = true; }, ...options };
            document.events.keydown(event); return event;
        }
    };
}

test('fresh Any filters ignore restored values; static content is prepared before revealing enhancement', () => {
    const v = viewer({ restored: { search: 'stale', state: 'RequestTimedOut', robots: 'NotObserved', discovery: 'title' } });
    assert.equal(v.pendingBeforeInit, true); assert.equal(v.root.classList.contains('js-pending'), false);
    assert.equal(v.ui.controls.hidden, false); assert.deepEqual(v.visible(), ['c', 'd', 'a', 'b']);
    for (const id of ['search', 'state', 'robots', 'discovery']) assert.equal(v.ui[id].value, '');
});
test('both destinations of one friendly hostname remain separately searchable and selectable', () => {
    const v = viewer();
    v.filter('search', 'plugins.i2p'); assert.deepEqual(v.visible(), ['a', 'b']);
    v.filter('search', 'BETA.B32.I2P'); assert.deepEqual(v.visible(), ['b']); assert.equal(v.selected(), 'b');
    v.filter('search', 'zULu'); assert.deepEqual(v.visible(), ['a']);
    v.filter('search', 'text/html'); assert.equal(v.visible().length, 4);
    v.filter('search', 'OUTSIDE.i2p'); assert.equal(v.visible().length, 4);
});
test('every sort toggles aria-sort and preserves selection, with public identity breaking ties', () => {
    const v = viewer({ hash: '#i2p-service-b' });
    const calls = [...v.history.calls];
    for (const key of ['hostname', 'title', 'state', 'http', 'discovered']) {
        v.sort(key);
        assert.equal(v.selected(), 'b');
        assert.equal(v.headers.filter(h => h.getAttribute('aria-sort')).length, 1);
        const header = v.headers.find(h => h.dataset.sort === key);
        const before = header.getAttribute('aria-sort');
        v.sort(key); assert.notEqual(header.getAttribute('aria-sort'), before);
    }
    assert.deepEqual(v.history.calls, calls);
    const n = viewer(); n.sort('http'); assert.deepEqual(n.visible(), ['c', 'a', 'd', 'b']);
    n.sort('discovered'); assert.deepEqual(n.visible(), ['d', 'b', 'c', 'a']);
});
test('search, state, robots and discovery compose with the current sorted order', () => {
    const v = viewer();
    v.sort('title'); v.filter('state', 'HomepageResponseObserved');
    assert.deepEqual(v.visible(), ['c', 'a']);
    v.filter('search', 'plugins'); assert.deepEqual(v.visible(), ['a']);
    v.filter('robots', 'NotObserved'); assert.deepEqual(v.visible(), []);
    assert.equal(v.ui.next.disabled, true); assert.equal(v.ui['copy-link'].disabled, true);
    v.ui.reset.click(); assert.deepEqual(v.visible(), ['d', 'b', 'c', 'a']);
    v.filter('discovery', 'multiple'); assert.deepEqual(v.visible(), ['b', 'a']);
    v.filter('discovery', 'title'); assert.deepEqual(v.visible(), ['c', 'a']);
    v.filter('discovery', 'hosts'); assert.deepEqual(v.visible(), ['c', 'a']);
    v.filter('discovery', 'incomplete'); assert.deepEqual(v.visible(), ['d', 'b']);
});
test('initial destination hashes reveal exactly the requested identity', () => {
    const v = viewer({ hash: '#i2p-service-b' });
    assert.equal(v.selected(), 'b'); assert.equal(v.panels.filter(p => !p.hidden).length, 1);
    assert.equal(v.location.hash, '#i2p-service-b');
    assert.equal(v.ui['i2p-service-b'].scrolled, true);
    assert.equal(v.rows.find(r => r.dataset.service === 'i2p-service-b').link.getAttribute('aria-current'), 'true');
});
test('history deep links relax only conflicting predicates and retain sorting', () => {
    const v = viewer();
    v.filter('search', 'plugins'); v.filter('state', 'HomepageResponseObserved'); v.filter('robots', 'AllowsRoot');
    v.filter('discovery', 'title'); v.sort('title');
    v.visit('b'); assert.equal(v.selected(), 'b');
    assert.equal(v.ui.search.value, 'plugins'); assert.equal(v.ui.robots.value, 'AllowsRoot');
    assert.equal(v.ui.state.value, ''); assert.equal(v.ui.discovery.value, '');
    v.ui.search.value = 'browser restored'; v.frames(); assert.equal(v.ui.search.value, 'plugins');
    v.visit('a', 'hashchange'); assert.equal(v.selected(), 'a');
    v.window.events.pageshow(); assert.equal(v.ui.search.value, 'plugins');
});
test('Previous/Next wrap within displayed filtered/sorted set, and Random uses only that set', () => {
    const v = viewer();
    v.filter('state', 'HomepageResponseObserved'); v.sort('title');
    assert.deepEqual(v.visible(), ['c', 'a']);
    v.ui.next.click(); assert.equal(v.selected(), 'a');
    v.ui.next.click(); assert.equal(v.selected(), 'c');
    v.ui.previous.click(); assert.equal(v.selected(), 'a');
    v.random(0); assert.equal(v.selected(), 'c'); v.random(.99); assert.equal(v.selected(), 'a');
    assert.ok(v.panels.every(p => !p.scrolled && !p.focused));
    assert.match(v.location.hash, /^#i2p-service-/);
    assert.doesNotMatch(script, /location\.hash\s*=(?!=)/);
});
test('native internal links support keyboard clicks; row delegation ignores interactive/modifier activations', () => {
    const v = viewer();
    v.event('b'); assert.equal(v.selected(), 'b');
    const count = v.history.calls.length;
    for (const part of ['external', 'button', 'summary', 'input']) v.event('d', part);
    for (const option of [{ ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { altKey: true }, { button: 1 }, { defaultPrevented: true }])
        v.event('d', 'internal', option);
    assert.equal(v.selected(), 'b'); assert.equal(v.history.calls.length, count);
    assert.equal(v.event('a', 'internal', { detail: 0 }).defaultPrevented, true);
    assert.equal(v.selected(), 'a'); assert.equal(v.ui['i2p-service-a'].focused, true);
});
test('keyboard shortcuts avoid editable and interactive controls', () => {
    const v = viewer(); v.key('ArrowRight'); assert.equal(v.selected(), 'd');
    v.key('ArrowRight', true); assert.equal(v.selected(), 'd');
    v.key('ArrowRight', false, { ctrlKey: true }); assert.equal(v.selected(), 'd');
    v.key('R'); assert.equal(v.selected(), 'c');
});
test('copy uses a full stable URL and provides accessible manual fallback', async () => {
    let copied;
    const v = viewer({ clipboard: { async writeText(value) { copied = value; } } });
    await v.ui['copy-link'].click();
    assert.equal(copied, 'https://atlas.example/i2p-index.html?keep=yes#i2p-service-c');
    assert.equal(v.ui['copy-status'].textContent, 'Copied');
    const fallback = viewer(); await fallback.ui['copy-link'].click();
    assert.equal(fallback.ui['copy-fallback'].hidden, false);
    assert.equal(fallback.ui['copy-url'].focused, true);
    assert.equal(fallback.ui['copy-url'].selectedText, true);
    assert.match(fallback.ui['copy-url'].value, /#i2p-service-c$/);
});
test('a late clipboard result cannot announce a different selection as copied', async () => {
    let finish;
    const v = viewer({ clipboard: { writeText() { return new Promise(resolve => { finish = resolve; }); } } });
    const pending = v.ui['copy-link'].click(); v.ui.next.click(); finish(); await pending;
    assert.equal(v.ui['copy-status'].textContent, '');
});
test('empty reports, no matches and malformed hashes remain usable', () => {
    const v = viewer({ empty: true, hash: '#%broken' });
    assert.equal(v.selected(), undefined); assert.deepEqual(v.visible(), []);
    v.sort('http'); v.ui.reset.click(); v.random(.5); v.ui.next.click();
    assert.equal(v.ui.next.disabled, true); assert.equal(v.ui['no-selection'].hidden, false);
    const noMatches = viewer(); noMatches.filter('search', 'absent'); noMatches.ui.reset.click();
    assert.equal(noMatches.visible().length, 4);
});
test('initialization failure reveals every static row/detail and disables enhancement', () => {
    const v = viewer({ failSort: true });
    assert.ok(v.rows.every(r => !r.hidden)); assert.ok(v.panels.every(p => !p.hidden));
    assert.equal(v.ui.controls.hidden, true); assert.equal(v.root.classList.contains('js-pending'), false);
    assert.ok(v.headers.every(h => h.querySelector('button').disabled));
});
test('narrow/short viewports do not pin a large controls bar', () => {
    for (const width of [320, 375, 430, 900]) assert.equal(viewer({ width }).ui.controls.classList.contains('is-sticky'), false);
    const v = viewer(); assert.equal(v.ui.controls.classList.contains('is-sticky'), true);
    v.ui.controls.height = 600; v.window.events.resize(); assert.equal(v.ui.controls.classList.contains('is-sticky'), false);
});
