const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs');
const {create,validate}=require('./release-checklist.cjs');
const template=JSON.parse(fs.readFileSync(__dirname+'/release-checklist-template.json','utf8'));
const commit='a'.repeat(40),file=commit+'.json';
function completed(){const r=create(commit,template);r.reviewedBy='Agent';r.reviewedAt=new Date().toISOString();r.uxChanged=true;for(const c of r.checks){c.checked=true;c.evidence='Specific test run and reviewed documentation evidence.';}return r;}
test('an exact complete checklist passes',()=>assert.equal(validate(completed(),commit,template,file).commit,commit));
for(const [name,mutate]of [
 ['empty generated checklist',r=>Object.assign(r,create(commit,template))],
 ['wrong commit',r=>r.commit='b'.repeat(40)],['stale template',r=>r.templateSha256='0'.repeat(64)],
 ['unchecked box',r=>r.checks[1].checked=false],['string true',r=>r.checks[0].checked='true'],
 ['missing item',r=>r.checks.pop()],['duplicate replacing required item',r=>r.checks[1]=r.checks[0]],
 ['unknown item',r=>r.checks[0].id='unknown'],['no evidence',r=>r.checks[0].evidence='done'],
 ['changed requirement wording',r=>r.checks[0].description='No tests required'],
 ['no reviewer',r=>r.reviewedBy=''],['undecided UX changes',r=>r.uxChanged=null],
 ['invalid date',r=>r.reviewedAt='yesterday'],['future review',r=>r.reviewedAt='2099-01-01T00:00:00Z']
])test('reject '+name,()=>{const r=completed();mutate(r);assert.throws(()=>validate(r,commit,template,file));});
test('reject a record under the wrong filename',()=>assert.throws(()=>validate(completed(),commit,template,'other.json')));
test('new template requirement invalidates an earlier checklist',()=>assert.throws(()=>validate(completed(),commit,{...template,items:[...template.items,{id:'new',label:'New requirement'}]},file)));

// Exercise the actual CLI entry point used in CI, with a disposable copy of
// the script so no test writes to the real release-checks directory.
test('CI receive and final validate preserve the record and reject bad inputs',()=>{
 const cp=require('node:child_process'),path=require('node:path'),os=require('node:os');
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'backspace-release-gate-'));
 fs.mkdirSync(path.join(root,'scripts'));
 for(const name of ['release-checklist.cjs','release-checklist-template.json'])fs.copyFileSync(path.join(__dirname,name),path.join(root,'scripts',name));
 const script=path.join(root,'scripts','release-checklist.cjs');
 const run=(args,json)=>cp.spawnSync(process.execPath,[script,...args],{encoding:'utf8',env:{...process.env,RELEASE_SHA:commit,RELEASE_CHECKLIST:json??''}});
 assert.notEqual(run(['receive']).status,0,'missing record must fail');
 assert.notEqual(run(['receive'],'{bad JSON').status,0,'malformed record must fail');
 assert.notEqual(run(['receive'],JSON.stringify(create(commit,template))).status,0,'unchecked record must fail');
 assert.equal(fs.existsSync(path.join(root,'release-checks')),false,'invalid data must not be written');
 const r=completed(),received=run(['receive'],JSON.stringify(r));
 assert.equal(received.status,0,received.stderr);
 const target=path.join(root,'release-checks',file);
 assert.deepEqual(JSON.parse(fs.readFileSync(target,'utf8')),r);
 assert.equal(run(['validate',target,commit]).status,0);
 assert.notEqual(run(['validate',target,'b'.repeat(40)]).status,0);
 assert.notEqual(run(['validate',target+'.missing',commit]).status,0);
 assert.notEqual(run(['receive'],JSON.stringify(r)).status,0,'a second receive cannot overwrite a record');
});
