const {test}=require('node:test');
const assert=require('node:assert/strict');
const path=require('node:path');
const window={};
globalThis.window=window;
globalThis.location={origin:'https://neko.internal'};
require(path.join(__dirname,'..','src','ui','NekoOutgoing.js'));
const outgoing=window.__nekoOutgoing;
const old=[{id:'old-1',role:'user',text:'Repeat text'}];

test('a new outgoing bubble appears before any backend status',()=>{
 const p=outgoing.create('chat','Repeat text',old);
 assert.equal(p.stage,'sending');
 assert.equal(p.text,'Repeat text');
 assert.equal(outgoing.reconcile(p,old,{sending:false,status:'idle'}).event,'pending');
 assert.equal(p.stage,'sending');
 assert.equal(outgoing.reconcile(p,old,{sending:true}).event,'pending');
 assert.equal(p.sawSending,true);
});

test('an earlier identical text never falsely confirms a new submission',()=>{
 const p=outgoing.create('chat','Repeat text',old);
 assert.equal(outgoing.reconcile(p,old,{generating:true}).event,'pending');
 assert.equal(outgoing.reconcile(p,old,{status:'sse_accepted'}).event,'pending');
 assert.equal(p.stage,'syncing');
 assert.equal(outgoing.reconcile(p,[...old,{
    id:'new-user',role:'user',text:'Repeat text'
 }],{generating:true}).event,'confirmed');
});

test('normalised line endings from authoritative history reconcile',()=>{
 const p=outgoing.create('chat',' First\r\nSecond ',[]);
 const actual=[{id:'server',role:'user',text:'First\nSecond'}];
 assert.equal(outgoing.reconcile(p,actual,{}).event,'confirmed');
});

test('an uncertain delivery never becomes a success merely because output streams',()=>{
 const p=outgoing.create('chat','text',[]);
 outgoing.reconcile(p,[],{sending:true});
 assert.equal(outgoing.reconcile(p,[],{uncertain:true,generating:true}).event,
   'uncertain');
 assert.equal(p.stage,'uncertain');
 assert.equal(outgoing.reconcile(p,[],{generating:true}).event,'pending');
 assert.equal(p.stage,'uncertain');
});

test('definitive rejection offers recovery without auto retry',()=>{
 const p=outgoing.create('chat','text',[]);
 outgoing.reconcile(p,[],{sending:true});
 assert.equal(outgoing.reconcile(p,[],{sending:false,phase:'send_failed',
    status:'bridge_unavailable'}).event,'failed');
 assert.equal(p.stage,'failed');
 assert.equal(outgoing.labels.failed,'Not sent · restore your message');
});

test('a saved new user message overrides stale uncertainty',()=>{
 const p=outgoing.create('chat','text',[]);
 outgoing.reconcile(p,[],{uncertain:true});
 const event=outgoing.reconcile(p,[{
   id:'new-id',role:'user',text:'text'
 }],{uncertain:true});
 assert.equal(event.event,'confirmed');
});

test('accepted terminal SSE unlocks the outgoing bubble while GET history is throttled',()=>{
 const p=outgoing.create('chat','second turn',[
  {id:'old-user',role:'user',text:'first turn'}]);
 p.priorLiveId='old-response';
 const old={phase:'completed',status:'sse_accepted',sending:false,
  generating:false,uncertain:false,
  live:{messageId:'old-response',completed:true,error:''}};
 assert.equal(outgoing.reconcile(p,[],old).event,'pending',
  'an older terminal response must not confirm the new send');
 const busy={...old,phase:'streaming',generating:true,
  live:{messageId:'new-response',completed:false,error:''}};
 assert.equal(outgoing.reconcile(p,[],busy).event,'pending',
  'a stream still generating is not a completed turn');
 const now={...old,
  live:{messageId:'new-response',completed:true,error:''}};
 assert.equal(outgoing.reconcile(p,[],now).event,'confirmed',
  'HTTP 200/SSE completion confirms the accepted turn without new GET history');
 const uncertain={...now,uncertain:true};
 const p2=outgoing.create('chat','other prompt',[]);
 p2.priorLiveId='old-response';
 assert.equal(outgoing.reconcile(p2,[],uncertain).event,'uncertain',
  'uncertain submissions stay blocked');
});

test('different attachments cannot confirm an attachment-only submission',()=>{
 const p=outgoing.create('chat','',[],['drawing.png']);
 const history=[{id:'different-upload',role:'user',text:'[Image attachment]'}];
 assert.equal(outgoing.reconcile(p,history,{}).event,'pending');
 assert.equal(outgoing.reconcile(p,history,{
   phase:'completed',status:'history_confirmed',
   sending:false,uncertain:false
 }).event,'pending');
});

test('an attachment caption embedded in unrelated text cannot confirm it',()=>{
 const p=outgoing.create('chat','Describe this',[],['picture.png']);
 const history=[{id:'unrelated',role:'user',
   text:'Please do not Describe this earlier picture [Image attachment]'}];
 assert.equal(outgoing.reconcile(p,history,{}).event,'pending');
});

test('attachment submission is confirmed only by a correlated server user ID',()=>{
 const p=outgoing.create('chat','Describe this',[],['picture.png']);
 p.serverUserMessageId='specific-user-id';
 const different=[{id:'other-user-id',role:'user',
  text:'Describe this [Image attachment]'}];
 assert.equal(outgoing.reconcile(p,different,{}).event,'pending');
 assert.equal(outgoing.reconcile(p,[...different,
  {id:'specific-user-id',role:'user',text:'[File attachment]'}],{}).event,
  'confirmed');
});

test('an older completed stop response does not confirm a new submission',()=>{
 const p=outgoing.create('chat','new prompt',[]);
 p.priorLiveId='previous-response';
 const previous={
   phase:'completed',status:'sse_accepted',
   sending:false,generating:false,uncertain:false,
   live:{messageId:'previous-response',completed:true,
    error:'server_complete_final_pending'}
 };
 assert.equal(outgoing.reconcile(p,[],previous).event,'pending');
 assert.equal(p.stage,'syncing');
 assert.equal(outgoing.reconcile(p,[],previous).event,'pending');
 const current={...previous,
  live:{messageId:'current-response',completed:true,
   error:'server_complete_final_pending'}};
 assert.equal(outgoing.reconcile(p,[],current).event,'confirmed');
});

test('missing current-turn identity cannot confirm final-pending completion',()=>{
 const p=outgoing.create('chat','first ever turn',[]);
 p.stage='syncing';
 const state={
   phase:'completed',status:'sse_accepted',
   sending:false,generating:false,uncertain:false,
   live:{messageId:'unattributed-response',completed:true,
    error:'server_complete_final_pending'}
 };
 assert.equal(outgoing.reconcile(p,[],state).event,'pending');
});

test('send_failed with no prior sending snapshot and no status fails definitively',()=>{
 const p=outgoing.create('chat','unsubmitted prompt',[]);
 const result=outgoing.reconcile(p,[],{
   phase:'send_failed',sending:false,status:''
 });
 assert.equal(result.event,'failed');
 assert.equal(p.stage,'failed');
});
