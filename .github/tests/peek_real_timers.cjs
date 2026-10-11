// Observe the unchanged upstream suite; never replace timers, clocks, or callbacks.
const assert = require('node:assert/strict');
const {createHook} = require('node:async_hooks');
const {writeFileSync} = require('node:fs');
const timers = require('node:timers');

const names = ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval',
  'setImmediate', 'clearImmediate', 'Date', 'Promise'];
const original = new Map(names.map(name => [name, globalThis[name]]));
const originalNow = Date.now;
const prefix = 'Matrix Node.js StreamReader Handle delayed read ';
const timedTests = [];
const resources = new Map();
let current;
let testsChecked = 0;

function assertNative() {
  for (const [name, value] of original) {
    assert.equal(globalThis[name], value, `${name} was replaced; real timers are required`);
  }
  assert.equal(Date.now, originalNow, 'Date.now was replaced; real time is required');
  assert.equal(setTimeout, timers.setTimeout, 'setTimeout must be the Node timer');
}

const hook = createHook({
  init(id, type, trigger, resource) {
    if (current && type === 'Timeout' && resource._idleTimeout === 500 &&
        /[/\\]test[/\\]util\.[jt]s:/.test(new Error().stack)) {
      current.scheduled++;
      resources.set(id, current);
    }
  },
  before(id) {
    const owner = resources.get(id);
    if (owner) owner.fired++;
  },
  destroy(id) {
    resources.delete(id);
  },
});

exports.mochaHooks = {
  beforeAll() {
    assertNative();
    hook.enable();
  },
  beforeEach() {
    assertNative();
    const title = this.currentTest.fullTitle();
    current = title.startsWith(prefix) ? {title, scheduled: 0, fired: 0} : undefined;
  },
  afterEach() {
    assertNative();
    testsChecked++;
    if (current) {
      assert.ok(current.scheduled > 0, 'Upstream delayed read must schedule a real 500 ms timer');
      if (current.title === prefix + 'handle delay') {
        assert.ok(current.fired > 0, 'Upstream delayed read must await the real timer callback');
      }
      timedTests.push(current);
    }
    current = undefined;
  },
  afterAll() {
    hook.disable();
    assertNative();
    writeFileSync(process.env.DOTCOV_TIMER_AUDIT,
      JSON.stringify({nativeTimers: true, testsChecked, timedTests}, null, 2));
  },
};
