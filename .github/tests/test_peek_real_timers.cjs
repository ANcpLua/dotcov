// Negative controls run in disposable processes and never contribute coverage.
const assert = require('node:assert/strict');
const {spawnSync} = require('node:child_process');
const {mkdtempSync, mkdirSync, writeFileSync, rmSync} = require('node:fs');
const {tmpdir} = require('node:os');
const path = require('node:path');
const {test} = require('node:test');

const project = path.resolve(process.env.PEEK_PROJECT);
const hook = path.join(__dirname, 'peek_real_timers.cjs');
const title = 'Matrix Node.js StreamReader Handle delayed read handle delay';
const cases = [
  ['replaced clock', "Date.now = () => 0; it('example', () => {});", 'Date.now was replaced'],
  ['missing timer', `it(${JSON.stringify(title)}, async () => {});`, 'must schedule a real 500 ms timer'],
  ['unawaited timer', `const schedule = require('./test/util.js');
    it(${JSON.stringify(title)}, async () => { schedule(); });`, 'must await the real timer callback'],
];

for (const [name, source, message] of cases) {
  test(`rejects ${name}`, () => {
    const dir = mkdtempSync(path.join(tmpdir(), 'dotcov-timer-control-'));
    try {
      mkdirSync(path.join(dir, 'test'));
      writeFileSync(path.join(dir, 'test', 'util.js'),
        'module.exports = () => setTimeout(() => {}, 500);');
      writeFileSync(path.join(dir, 'fixture.cjs'), source);
      const result = spawnSync(process.execPath,
        [path.join(project, 'node_modules/mocha/bin/mocha.js'), '--no-config', '--no-package',
          '--require', hook, 'fixture.cjs'], {
          cwd: dir, encoding: 'utf8', timeout: 10000,
          env: {...process.env, DOTCOV_TIMER_AUDIT: path.join(dir, 'audit.json')},
        });
      assert.ifError(result.error);
      assert.ok(result.status > 0, result.stdout + result.stderr);
      assert.ok((result.stdout + result.stderr).includes(message), result.stdout + result.stderr);
    } finally {
      rmSync(dir, {recursive: true, force: true});
    }
  });
}
