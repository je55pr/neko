const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');

const read=name=>fs.readFileSync(path.join(__dirname,'..','src','ui',name),'utf8');
const single=read('NekoSurface.js');
const workspace=read('NekoWorkspace.js');

test('single chat uses modest opposing gutters, not separate columns',()=>{
  assert.match(single,/\.message\.user,\.message\.assistant\s*\{\s*width:calc\(100% - clamp\(20px,7%,56px\)\)\}/);
  assert.match(single,/\.message\.user\{align-items:flex-end;align-self:flex-end\}/);
  assert.match(single,/\.message\.assistant\{align-self:flex-start\}/);
  assert.match(single,/bubble\.className='message user pending'/);
  assert.match(single,/message\.className='message '\+\(item\.role==='user'\?'user':'assistant'\)/);
  // Top-level thinking/tool groups follow Neko's right gutter. Individual
  // calls inside an expanded group still fill that group's available width.
  assert.match(single,/\.message\.activity\{width:calc\(100% - clamp\(20px,7%,56px\)\);align-self:flex-start;/);
  assert.match(single,/\.message\.activity \.toolGroupContent > \.message\.activity\{\s*width:100%;margin:0/);
  assert.match(single,/\.message\.user \.content\{background:#253039;border:1px solid #34424c/);
});

test('workspace saved, pending and streamed messages share role gutters',()=>{
  assert.match(workspace,/\.nwMessage\.user\{width:max-content;max-width:calc\(100% - clamp\(20px,7%,56px\)\);/);
  assert.match(workspace,/margin-left:auto;text-align:right;color:#e5f5f0;/);
  assert.match(workspace,/background:#253b37;border:1px solid #42675c;border-radius:14px 14px 4px 14px;/);
  assert.match(workspace,/\.nwMessage\.assistant\{margin-right:clamp\(20px,7%,56px\);text-align:left\}/);
  assert.match(workspace,/\.nwMessages > \.nwActivity,\.nwMessages > \.nwActivities\{\s*margin-right:24px;/);
  assert.match(workspace,/margin-right:clamp\(20px,7%,56px\)\}\s*\.nwActivity/);
  assert.match(workspace,/const localEcho=el\('div','nwMessage user nwPending'\)/);
  assert.match(workspace,/const live=el\('div','nwMessage assistant nwLive'\)/);
  assert.match(workspace,/const box=el\('div','nwMessage '\+\(item\.role==='user'\?'user':'assistant'\)\)/);
  assert.doesNotMatch(workspace,/grid-template-columns:\s*repeat\(2/);
});
