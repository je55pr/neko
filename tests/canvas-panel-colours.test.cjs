const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const css=fs.readFileSync(path.join(__dirname,'..','src','ui','NekoWorkspace.js'),'utf8');

const states=['loading','sending','thinking','tools','streaming','error'];
const props=['--nwBody','--nwHead','--nwComposerBg','--nwDraftBg','--nwEdge','--nwGlow'];

function vars(selector){
  const start=css.indexOf(selector+'{');
  assert(start>=0,'Missing state rule '+selector);
  const chunk=css.slice(start+selector.length+1,css.indexOf('}',start));
  const result=new Map();
  for(const match of chunk.matchAll(/(--nw\w+):\s*(#[0-9a-f]{6,8})/ig))
    result.set(match[1],match[2]);
  return result;
}
function foreground(selector){
  const start=css.indexOf(selector+'{');
  assert(start>=0,'Missing foreground selector '+selector);
  const chunk=css.slice(start+selector.length+1,css.indexOf('}',start));
  const match=chunk.match(/(?:^|;)\s*color:\s*(#[0-9a-f]{6,8})/i);
  assert(match,'Missing foreground color for '+selector);
  return match[1];
}
function luminance(hex){
  const color=hex.slice(1,7).match(/.{2}/g).map(x=>parseInt(x,16)/255);
  const rgb=color.map(v=>v<=0.04045?v/12.92:Math.pow((v+0.055)/1.055,2.4));
  return .2126*rgb[0]+.7152*rgb[1]+.0722*rgb[2];
}
function contrast(a,b){
  const x=luminance(a),y=luminance(b);
  return (Math.max(x,y)+.05)/(Math.min(x,y)+.05);
}

test('all active states tint the entire card distinctly',()=>{
  const idle=vars('.nwPanel');
  assert.equal(idle.get("--nwBody"),'#111b21');
  const names=[...states,'unread'];
  const backgrounds=new Set();
  const borders=new Set();
  for(const state of names){
    const sel=state==='unread'
      ?'.nwPanel.nwHasUnread[data-nw-state="idle"]'
      :'.nwPanel[data-nw-state="'+state+'"]';
    const style=vars(sel);
    for(const property of props)
      assert(style.get(property),state+' missing '+property);
    for(const surface of ['--nwBody','--nwHead','--nwComposerBg','--nwDraftBg'])
      assert.notEqual(style.get(surface),idle.get(surface),
        state+' '+surface+' must not use the idle colour');
    assert(!backgrounds.has(style.get("--nwBody")),state+' duplicate body palette');
    assert(!borders.has(style.get("--nwEdge")),state+' duplicate border palette');
    backgrounds.add(style.get("--nwBody"));borders.add(style.get("--nwEdge"));
    // Message body and header title remain light on deeply tinted surfaces.
    assert(contrast(foreground('.nwMessage'),style.get("--nwBody"))>=7,
      state+' message text contrast too low');
    assert(contrast(foreground('.nwMessage'),style.get("--nwHead"))>=4.5,
      state+' header contrast too low');
    assert(contrast(foreground('.nwDraft'),style.get("--nwDraftBg"))>=7,
      state+' draft text contrast too low');
  }
});
test('state variables are used throughout header, messages, overview and composer',()=>{
  for(const fragment of [
    'background:var(--nwBody);cursor:default',
    'background:var(--nwHead);border-bottom:1px solid var(--nwEdge)',
    'background:var(--nwBody);transition:background-color',
    'background:var(--nwComposerBg);',
    'background:var(--nwDraftBg);border:1px solid var(--nwEdge)',
    'background:var(--nwBody);'
  ])assert(css.includes(fragment),'Missing status surface: '+fragment);
  assert.match(css,/border:2px solid var\(--nwEdge\)/);
});
test('active work has moving full-card glow and pointer-safe top progress stripe',()=>{
  for(const state of ['sending','thinking','tools','streaming'])
    assert(css.includes('.nwPanel[data-nw-state="'+state+'"]'));
  assert.match(css,/animation:nwPanelGlow 2\.8s ease-in-out infinite/);
  assert.match(css,/animation:nwPanelFlow 2\.6s linear infinite/);
  assert.match(css,/content:'';position:absolute;z-index:7;pointer-events:none/);
  assert.match(css,/\.nwResizeHandle\{position:absolute;z-index:8/);
  assert.match(css,/@media\(prefers-reduced-motion:reduce\)/);
  assert.match(css,/animation:none!important;transition:none!important/);
});
test('unread highlight only applies when idle and error wins over unread',()=>{
  assert.match(css,/\.nwPanel\.nwHasUnread\[data-nw-state="idle"\]/);
  assert.match(css,/\.nwPanel\[data-nw-state="error"\]/);
  assert.match(css,/view\.panel\.dataset\.nwState=state/);
  assert.match(css,/view\.panel\.classList\.toggle\('nwHasUnread',unread\)/);
});
