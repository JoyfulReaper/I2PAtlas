// node --test I2PAtlas.Tests/I2pCrawlViewerTests.cjs
// Offline, dependency-free checks of the crawl-specific enhancement on rendered-attribute-shaped DOM fixtures.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const template = fs.readFileSync(path.join(__dirname, '../I2PAtlas/I2pReporting/crawl-viewer.html'), 'utf8');
const script = template.match(/<script>([\s\S]*?)<\/script>/)[1];

function element(id) {
    const attributes = new Map();
    return { id, hidden: false, disabled: false, value: '', textContent: '', dataset: {}, events: {},
        setAttribute(k, v) { attributes.set(k, v); }, removeAttribute(k) { attributes.delete(k); }, getAttribute(k) { return attributes.get(k); },
        addEventListener(k, fn) { this.events[k] = fn; }, click() { if (!this.disabled) this.events.click?.(); } };
}
function viewer({ data, restored = {}, failSort = false, allEntries = false, javascript = true } = {}) {
    const ui = Object.fromEntries(['crawl-entries', 'crawl-controls', 'crawl-search', 'crawl-kind', 'crawl-summary', 'crawl-reset',
        'crawl-counter', 'crawl-no-matches', 'crawl-view'].map(id => [id, element(id)]));
    ui['crawl-controls'].hidden = ui['crawl-no-matches'].hidden = true;
    Object.entries(restored).forEach(([id, value]) => { ui['crawl-' + id].value = value; });
    const rows = (data ?? [
        ['a', 'plugins.i2p', 'KnownSeed', 'Zulu', 'HomepageResponseObserved', '200'],
        ['b', 'plugins.i2p', 'KnownSeed', '', 'HomepageAttemptFailed', ''],
        ['c', 'alpha.i2p', 'DiscoveredHost', 'Alpha', 'HomepageResponseObserved', '404'],
        ['d', 'delta.i2p', 'DiscoveredHost', '<img src=x> literal', 'RequestTimedOut', ''],
        ['e', 'zoo.i2p', 'KnownSeed', '東京 🧅', 'HomepageResponseObserved', '200']
    ]).map(([id, hostname, kind, title, summary, http]) => {
        const row = element('row-' + id);
        row.dataset = { entry: 'i2p-crawl-entry-' + id, hostname, kind, summary, http, search: hostname + ' ' + title };
        row.anchor = element('anchor-' + id);
        row.anchor.setAttribute('href', '#' + row.dataset.entry);
        return row;
    });
    const panels = rows.map(row => element(row.dataset.entry));
    const body = ui['crawl-entries'];
    body.children = [...rows];
    body.querySelectorAll = selector => { assert.equal(selector, 'tr[data-entry]'); return rows; };
    let broken = failSort;
    body.appendChild = row => {
        if (broken) throw new Error('Injected sort failure');
        body.children.splice(body.children.indexOf(row), 1); body.children.push(row);
    };
    const headers = ['kind', 'hostname', 'summary', 'http'].map(key => {
        const header = element('column-' + key), button = element('button-' + key);
        header.dataset.sort = key; button.disabled = true;
        header.querySelector = selector => { assert.equal(selector, 'button'); return button; };
        return header;
    });
    const document = { getElementById: id => ui[id], querySelectorAll: selector => { assert.equal(selector, 'th[data-sort]'); return headers; } };
    if (javascript) vm.runInNewContext(script, { document }); // No network, navigation or executable-data APIs are available.
    if (allEntries && !failSort) { ui['crawl-view'].value = 'all'; ui['crawl-view'].events.change(); }
    return { ui, rows, panels, headers,
        visible: () => body.children.filter(row => !row.hidden).map(row => row.dataset.entry.slice(16)),
        filter(key, value) { ui['crawl-' + key].value = value; ui['crawl-' + key].events[key === 'search' ? 'input' : 'change'](); },
        sort(key) { headers.find(header => header.dataset.sort === key).querySelector('button').click(); },
        reset() { ui['crawl-reset'].click(); },
        breakSort() { broken = true; }
    };
}

test('initial likely reachable view shows only observed homepage responses, ignoring restored filters', () => {
    const v = viewer({ restored: { view: 'all', search: 'stale', kind: 'DiscoveredHost', summary: 'RequestTimedOut' } });
    assert.deepEqual(v.visible(), ['c', 'a', 'e']); // Includes HTTP 404: this is a response observation, not a 200-only claim.
    assert.equal(v.ui['crawl-view'].value, 'likely');
    assert.equal(v.ui['crawl-controls'].hidden, false);
    for (const key of ['search', 'kind', 'summary']) assert.equal(v.ui['crawl-' + key].value, '');
    assert.equal(v.ui['crawl-counter'].textContent, '3 likely reachable / 5 public entries');
    assert.equal(v.ui['crawl-no-matches'].hidden, true);
});

test('hostname and title search is case-insensitive and preserves Unicode and literal markup text', () => {
    const v = viewer({ allEntries: true });
    v.filter('search', ' PLUGINS.I2P '); assert.deepEqual(v.visible(), ['a', 'b']);
    v.filter('search', 'zULu'); assert.deepEqual(v.visible(), ['a']);
    v.filter('search', '東京 🧅'); assert.deepEqual(v.visible(), ['e']);
    v.filter('search', '<IMG SRC=X>'); assert.deepEqual(v.visible(), ['d']);
    assert.equal(v.rows.find(r => r.dataset.entry.endsWith('-d')).dataset.search, 'delta.i2p <img src=x> literal');
});

test('View depends only on HomepageResponseObserved and All entries restores every other summary state', () => {
    const states = ['HomepageResponseObserved', 'HomepageAttemptFailed', 'RobotsDisallowed', 'ProxyFailureObserved',
        'RequestTimedOut', 'TransportFailure', 'RobotsRedirect', 'Ambiguous', 'HomepageResponseObserved'];
    const data = states.map((summary, i) => ['s' + i, 'host' + i + '.i2p', 'KnownSeed', '', summary, i === 0 ? '502' : i === 8 ? '404' : '']);
    const v = viewer({ data });
    assert.deepEqual(v.visible(), ['s0', 's8']);
    assert.equal(v.ui['crawl-counter'].textContent, '2 likely reachable / 9 public entries');
    v.filter('view', 'all'); assert.equal(v.visible().length, 9);
    assert.equal(v.ui['crawl-counter'].textContent, '9 matching / 9 public entries');
    v.filter('summary', 'RobotsDisallowed'); assert.deepEqual(v.visible(), ['s2']);
    v.filter('view', 'likely'); assert.deepEqual(v.visible(), []);
    assert.equal(v.ui['crawl-summary'].value, 'RobotsDisallowed', 'Summary filter must remain independent of View.');
    v.filter('view', 'all'); assert.deepEqual(v.visible(), ['s2']);
    v.reset(); assert.deepEqual(v.visible(), ['s0', 's8']);
});

test('View composes with search, Kind and Summary without silently broadening an empty default', () => {
    const v = viewer();
    v.filter('search', 'plugins'); assert.deepEqual(v.visible(), ['a']);
    v.filter('summary', 'HomepageAttemptFailed'); assert.deepEqual(v.visible(), []);
    assert.equal(v.ui['crawl-view'].value, 'likely');
    v.filter('view', 'all'); assert.deepEqual(v.visible(), ['b']);
    v.filter('kind', 'DiscoveredHost'); assert.deepEqual(v.visible(), []);
    v.filter('kind', 'KnownSeed'); assert.deepEqual(v.visible(), ['b']);
    v.reset(); assert.deepEqual(v.visible(), ['c', 'a', 'e']);
    const ambiguous = viewer({ data: [['x', 'unknown.i2p', 'DiscoveredHost', '', 'Ambiguous', '']] });
    assert.deepEqual(ambiguous.visible(), []);
    assert.equal(ambiguous.ui['crawl-controls'].hidden, false);
    ambiguous.filter('view', 'all'); assert.deepEqual(ambiguous.visible(), ['x']);
});

test('without JavaScript every static row remains available and View controls stay hidden', () => {
    const v = viewer({ javascript: false });
    assert.equal(v.visible().length, 5);
    assert.ok(v.rows.every(row => !row.hidden));
    assert.equal(v.ui['crawl-controls'].hidden, true);
    assert.ok(v.rows.every(row => Object.keys(row.anchor.events).length === 0));
});

test('Kind filter keeps alternate known destinations distinct and supports both kinds and All', () => {
    const v = viewer({ allEntries: true });
    v.filter('kind', 'KnownSeed'); assert.deepEqual(v.visible(), ['a', 'b', 'e']);
    v.filter('kind', 'DiscoveredHost'); assert.deepEqual(v.visible(), ['c', 'd']);
    v.filter('kind', ''); assert.equal(v.visible().length, 5);
});

test('summary and search filters compose, and reset clears all filters while retaining the selected sort', () => {
    const v = viewer({ allEntries: true });
    v.sort('kind');
    v.filter('summary', 'HomepageResponseObserved'); assert.deepEqual(v.visible(), ['a', 'e', 'c']);
    v.filter('kind', 'DiscoveredHost'); assert.deepEqual(v.visible(), ['c']);
    v.filter('search', 'absent'); assert.deepEqual(v.visible(), []);
    assert.equal(v.ui['crawl-no-matches'].hidden, false);
    v.reset(); assert.deepEqual(v.visible(), ['a', 'e', 'c']);
    assert.equal(v.ui['crawl-view'].value, 'likely');
    for (const key of ['search', 'kind', 'summary']) assert.equal(v.ui['crawl-' + key].value, '');
    assert.equal(v.ui['crawl-no-matches'].hidden, true);
    assert.equal(v.headers.find(h => h.dataset.sort === 'kind').getAttribute('aria-sort'), 'ascending');
});

test('hostname, kind and summary sorting toggle with accessible direction indicators', () => {
    const v = viewer({ allEntries: true });
    v.sort('hostname'); assert.deepEqual(v.visible(), ['e', 'a', 'b', 'd', 'c']);
    v.sort('kind'); assert.deepEqual(v.visible(), ['a', 'b', 'e', 'c', 'd']);
    v.sort('kind'); assert.deepEqual(v.visible(), ['c', 'd', 'a', 'b', 'e']);
    v.sort('summary'); assert.deepEqual(v.visible(), ['b', 'c', 'a', 'e', 'd']);
    v.sort('summary'); assert.deepEqual(v.visible(), ['d', 'c', 'a', 'e', 'b']);
    const active = v.headers.filter(h => ['ascending', 'descending'].includes(h.getAttribute('aria-sort')));
    assert.equal(active.length, 1); assert.equal(active[0].dataset.sort, 'summary');
});

test('homepage HTTP sort is numeric and missing observations stay last in either direction', () => {
    const v = viewer({ allEntries: true });
    v.sort('http'); assert.deepEqual(v.visible(), ['a', 'e', 'c', 'd', 'b']);
    v.sort('http'); assert.deepEqual(v.visible(), ['c', 'a', 'e', 'd', 'b']);
    v.filter('kind', 'KnownSeed'); assert.deepEqual(v.visible(), ['a', 'e', 'b']);
});

test('identity breaks ties deterministically without merging same-hostname entries', () => {
    const data = [['b', 'same.i2p', 'KnownSeed', '', 'Ambiguous', ''], ['a', 'same.i2p', 'KnownSeed', '', 'Ambiguous', '']];
    const first = viewer({ data, allEntries: true }), second = viewer({ data: [...data].reverse(), allEntries: true });
    for (const key of ['hostname', 'kind', 'summary', 'http']) {
        first.sort(key); second.sort(key); assert.deepEqual(first.visible(), ['a', 'b']); assert.deepEqual(second.visible(), ['a', 'b']);
    }
});

test('native detail targets remain available and JS never overrides CSS visibility when rows are filtered out', () => {
    const v = viewer();
    const links = v.rows.map(row => row.anchor.getAttribute('href'));
    v.filter('search', 'absent'); v.sort('kind');
    assert.deepEqual(v.visible(), []);
    assert.ok(v.panels.every(panel => !panel.hidden));
    assert.deepEqual(v.rows.map(row => row.anchor.getAttribute('href')), links);
    assert.ok(v.rows.every(row => Object.keys(row.anchor.events).length === 0));
});

test('detail visibility uses CSS target rules independently of JavaScript with no empty visible section heading', () => {
    const css = template.match(/<style>([\s\S]*?)<\/style>/)[1];
    assert.match(css.match(/\.crawl-detail\s*\{([^}]*)\}/)[1], /\bdisplay:\s*none\s*;/);
    assert.match(css.match(/\.crawl-detail:target\s*\{([^}]*)\}/)[1], /\bdisplay:\s*block\s*;/);
    assert.match(css.match(/\.crawl-details\s*\{([^}]*)\}/)[1], /\bmargin:\s*0\s*;/);
    assert.match(template, /<section class="crawl-details" aria-label="Complete public observations">\{\{details\}\}<\/section>/);
    assert.ok(!template.includes('id="details-title"'));
    assert.ok(!script.includes('crawl-detail'), 'Enhancement must not manage detail visibility.');
});

test('empty crawl remains usable and reports zero matches without manufacturing entries', () => {
    const v = viewer({ data: [] });
    assert.deepEqual(v.visible(), []);
    assert.equal(v.ui['crawl-counter'].textContent, '0 likely reachable / 0 public entries');
    v.reset(); v.sort('http'); assert.equal(v.panels.length, 0);
});

test('initial enhancement failure restores every static entry and disables inaccessible controls', () => {
    const v = viewer({ failSort: true });
    assert.equal(v.visible().length, 5);
    assert.equal(v.ui['crawl-controls'].hidden, true);
    assert.ok(v.headers.every(h => h.querySelector('button').disabled));
    assert.ok(v.panels.every(panel => !panel.hidden));
    assert.match(v.ui['crawl-counter'].textContent, /all 5 public entries remain visible/);
});

test('later enhancement failure removes partial filtering and leaves native detail links intact', () => {
    const v = viewer();
    v.filter('search', 'zulu'); assert.deepEqual(v.visible(), ['a']);
    v.breakSort(); v.sort('kind');
    assert.equal(v.visible().length, 5);
    assert.equal(v.ui['crawl-controls'].hidden, true);
    v.filter('search', 'absent'); assert.equal(v.visible().length, 5);
    assert.ok(v.panels.every(panel => !panel.hidden));
});

test('enhancement uses rendered attributes only with no automatic networking, executable JSON or History API', () => {
    for (const forbidden of ['innerHTML', 'insertAdjacentHTML', 'document.write', 'fetch(', 'XMLHttpRequest', 'WebSocket', 'sendBeacon',
        'eval(', 'JSON.parse', 'history.', 'location.', 'clipboard']) assert.ok(!script.includes(forbidden), forbidden);
    assert.match(template, /connect-src 'none'/);
    assert.match(template, /<link rel="icon" href="data:,">/);
    assert.ok(!/<(?:script|link|img)[^>]+(?:src|href)="https?:\/\//i.test(template));
});
