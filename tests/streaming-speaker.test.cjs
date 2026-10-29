const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');

const workspace=fs.readFileSync(path.join(__dirname,'..','src','ui','NekoWorkspace.js'),'utf8');

test('streaming replies have an explicit Neko assistant role and separate body',()=>{
  assert.match(workspace, /const live=el\('div','nwMessage assistant nwLive'\)/);
  assert.match(workspace,/live\.append\(el\('span','who','Neko'\),liveBody\)/);
  assert.match(workspace,/showMarkdown\(view\.liveBody,shownText\)/);
  assert.doesNotMatch(workspace,/showMarkdown\(view\.live,update\.text/);
});

test('streaming assistant cannot inherit the old green chat-box styling',()=>{
  assert.match(workspace,/\.nwLive\{background:transparent;border:0;border-radius:0/);
  assert.doesNotMatch(workspace,/\.nwLive\{[^}]*background:#19332f/);
  assert.match(workspace,/\.nwMessage\.user\{/);
  assert.match(workspace,/const box=el\('div','nwMessage '\+\(item\.role==='user'\?'user':'assistant'\)\)/);
});

test('outgoing user preview and authoritative assistant reply stay distinct',()=>{
  assert.match(workspace,/const localEcho=el\('div','nwMessage user nwPending'\)/);
  assert.match(workspace,/const live=el\('div','nwMessage assistant nwLive'\)/);
  assert.match(workspace,/messages\.replaceChildren\(fragments,view\.activityHost,view\.awaitingReply,view\.live\)/);
  assert.match(workspace,/update\.completed &&\s*view\.savedAssistantById\?\.has\(update\.messageId\)/);
  assert.match(workspace,/item\.role==='user'\?'You':'Neko'/);
});
