'use strict';
// The local record is Git-excluded. CI receives its exact JSON as a dispatch
// input, saves it under the commit filename, and validates it again before
// granting the publishing job access. No shell evaluation of record contents.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const cp = require('node:child_process');
const root = path.resolve(__dirname, '..');
const templatePath = path.join(__dirname, 'release-checklist-template.json');
const read = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const templateHash = t => crypto.createHash('sha256').update(JSON.stringify(t)).digest('hex');
function sha(value) {
  if (!/^[a-f0-9]{40}$/.test(value || '')) throw Error('Use the full 40-character lowercase commit ID.');
  return value;
}
function validate(record, expectedCommit, template, filename) {
  sha(expectedCommit);
  if (record.schemaVersion !== 1 || record.commit !== expectedCommit) throw Error('Checklist commit does not match the release commit.');
  if (path.basename(filename) !== expectedCommit + '.json') throw Error('Checklist filename must be the exact commit ID followed by .json.');
  if (record.templateSha256 !== templateHash(template)) throw Error('Checklist is stale: use the latest template and review every item.');
  if (typeof record.reviewedBy !== 'string' || !record.reviewedBy.trim()) throw Error('Record who performed the checks.');
  if (!Number.isFinite(Date.parse(record.reviewedAt)) || Date.parse(record.reviewedAt) > Date.now() + 300000) throw Error('Record a valid review time.');
  if (typeof record.uxChanged !== 'boolean') throw Error('Explicitly record whether the UX changed.');
  if (!Array.isArray(record.checks) || record.checks.length !== template.items.length) throw Error('Checklist items do not match the current template.');
  const seen = new Set();
  for (const check of record.checks) {
    if (!template.items.some(i => i.id === check.id) || seen.has(check.id)) throw Error('Unknown or duplicate checklist item.');
    seen.add(check.id);
    if (check.description !== template.items.find(i => i.id === check.id).label) throw Error('Requirement wording differs from the template: ' + check.id);
    if (check.checked !== true) throw Error('Unchecked release requirement: ' + check.id);
    if (typeof check.evidence !== 'string' || check.evidence.trim().length < 20) throw Error('Add specific findings and evidence for ' + check.id + '.');
  }
  return record;
}
function create(commit, template) {
  return { schemaVersion: 1, commit: sha(commit), templateSha256: templateHash(template),
    reviewedBy: '', reviewedAt: '', uxChanged: null,
    checks: template.items.map(i => ({ id: i.id, description: i.label, checked: false, evidence: '' })) };
}
function git(...args) { return cp.execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim(); }
async function main(args) {
  const [command, file, expectedCommit] = args;
  const template = read(templatePath);
  if (command === 'create') {
    const commit = git('rev-parse', 'HEAD');
    if (git('status', '--porcelain', '--untracked-files=normal')) throw Error('Commit the candidate changes before preparing its checklist.');
    const target = path.join(root, 'release-checks', commit + '.json');
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, JSON.stringify(create(commit, template), null, 2) + '\n', { flag: 'wx' });
    console.log(target); return;
  }
  if (command === 'validate') {
    validate(read(file), sha(expectedCommit), template, file);
    console.log('All current release requirements are checked for ' + expectedCommit); return;
  }
  if (command === 'receive') {
    const commit = sha(process.env.RELEASE_SHA);
    const target = path.join(root, 'release-checks', commit + '.json');
    if (!process.env.RELEASE_CHECKLIST) throw Error('A completed commit-specific checklist is required.');
    const record = JSON.parse(process.env.RELEASE_CHECKLIST);
    validate(record, commit, template, target);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, JSON.stringify(record, null, 2) + '\n', { flag: 'wx' });
    console.log(target); return;
  }
  if (command === 'latest-template') {
    // Read the current main template immediately before publishing. A run
    // prepared from an earlier template cannot publish after main changes it.
    const repo = process.env.GITHUB_REPOSITORY;
    if (!/^[\w.-]+\/[\w.-]+$/.test(repo || '')) throw Error('Missing GitHub repository.');
    const response = await fetch(`https://api.github.com/repos/${repo}/contents/scripts/release-checklist-template.json?ref=main`,
      { signal: AbortSignal.timeout(15000), headers: { Authorization: 'Bearer ' + process.env.GH_TOKEN, Accept: 'application/vnd.github.raw+json' } });
    if (!response.ok) throw Error('Cannot read the latest main checklist template: HTTP ' + response.status);
    const latest = await response.json();
    if (templateHash(latest) !== templateHash(template)) throw Error('Main has a different checklist template. Prepare a new candidate and checklist.');
    console.log('Template matches current main.'); return;
  }
  if (command === 'dispatch') {
    const record = read(file), commit = git('rev-parse', 'HEAD');
    validate(record, commit, template, file);
    if (git('status', '--porcelain', '--untracked-files=normal')) throw Error('The working tree differs from the checked commit.');
    const remoteMain = git('ls-remote', 'origin', 'refs/heads/main').split(/\s/)[0];
    if (commit !== remoteMain) throw Error('The checked commit must be current origin/main.');
    const publish = args.includes('--publish'), tagIndex = args.indexOf('--tag');
    const tag = tagIndex < 0 ? '' : args[tagIndex + 1];
    if (publish && !/^v\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$/.test(tag || '')) throw Error('Publishing requires --tag vX.Y.Z.');
    cp.execFileSync('gh', ['workflow', 'run', 'release-checks.yml', '--ref', 'main', '--json'],
      { cwd: root, input: JSON.stringify({ publish: String(publish), tag, checklist: JSON.stringify(record) }), stdio: ['pipe', 'inherit', 'inherit'] });
    return;
  }
  throw Error('Usage: node scripts/release-checklist.cjs create | validate <file> <commit> | dispatch <file> [--publish --tag vX.Y.Z]');
}
module.exports = { create, validate, templateHash };
if (require.main === module) main(process.argv.slice(2)).catch(e => { console.error(e.message); process.exitCode = 1; });
