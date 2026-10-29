const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const source=name=>fs.readFileSync(path.join(__dirname,'..','src','ui',name),'utf8');
const workspace=source('NekoWorkspace.js');
const outgoing=source('NekoOutgoing.js');
test('progress is ordinary permanently visible Neko text, not a details/summary expander',()=>{
 const code=workspace.split('function activityNode(activity,prior=null)')[1]
   .split('function applyActivities(view,activities)')[0];
 assert.match(code,/if\(kind==='progress'\)/);
 assert.match(code,/el\('div','nwMessage assistant nwProgress'\)/);
 assert.match(code,/block\.append\(el\('span','who','Neko'\),body\)/);
 assert.match(code,/showMarkdown\(body,text\)/);
 assert.match(code,/return \{details:block,summary:null,body\}/);
 assert.match(code,/el\('details','nwActivity '\+kind\)/);
 const history=workspace.split('function renderHistory(view,info)')[1]
   .split('function applyLive(view,update)')[0];
 assert.match(history,/kind==='thinking'\|\|kind==='media'\|\|kind==='progress'/);
 assert.match(history,/fragments\.append\(activityNode\(item\)\.details\)/);
});
test('accepted sends never show an endlessly stuck updating history label',()=>{
 assert.match(outgoing,/sending:'Sending…',syncing:''/);
 assert.doesNotMatch(outgoing,/updating history/i);
 assert.match(workspace,/view\.echoState\.hidden=!deliveryLabel/);
 assert.match(workspace,/view\.localEcho\.classList\.toggle\('nwPendingAccepted',/);
 assert.match(workspace,/\.nwPending\.nwPendingAccepted\{border-style:solid/);
 assert.match(workspace,/\.nwPendingState\[hidden\]\{display:none\}/);
 assert.match(outgoing,/uncertain:'Delivery unconfirmed/);
 assert.match(outgoing,/failed:'Not sent/);
});
