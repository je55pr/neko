const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const vm=require('node:vm');
const workspace=fs.readFileSync(path.join(__dirname,'..','src','ui','NekoWorkspace.js'),'utf8');


function isolatedLiveRenderer(){
 const source=workspace.split('function applyLive(view,update)')[1]
   .split('function receive(next)')[0];
 return vm.runInNewContext('function applyLive(view,update)'+source+';applyLive',{
   placeLiveActivity(){},
   applyActivities(){},
   updateAwaitingReply(){},
   showMarkdown(element,text){element.text=text;},
   followLatest(){}
 });
}

function sampleView(){
 return {
  live:{hidden:false},
  liveBody:{
    text:'NEKO UI ACK',
    replaceChildren(){this.text='';}
  },
  lastLive:'old-snapshot',
  currentLiveId:'old-assistant-id',
  lastNonEmptyLive:{id:'old-assistant-id',text:'NEKO UI ACK'},
  savedAssistantById:new Map([['old-assistant-id',{}]]),
  overview:{textContent:''},
  lastInfo:{},
  initialTail:false,
  followTail:false,
  pending:null
 };
}

test('a live reply is removed when saved history takes ownership',()=>{
 const applyLive=isolatedLiveRenderer();
 const view=sampleView();
 applyLive(view,null);
 assert.equal(view.live.hidden,true);
 assert.equal(view.liveBody.text,'');
 assert.equal(view.currentLiveId,'');
 assert.equal(view.lastLive,'');
 assert.equal(view.lastNonEmptyLive,null);
});

test('a later live reply renders normally after saved-history handoff',()=>{
 const applyLive=isolatedLiveRenderer();
 const view=sampleView();
 applyLive(view,null);
 applyLive(view,{
   messageId:'next-assistant-id',text:'A new answer',completed:false,
   error:'',activities:[]
 });
 assert.equal(view.live.hidden,false);
 assert.equal(view.liveBody.text,'A new answer');
 assert.equal(view.currentLiveId,'next-assistant-id');
});


function isolatedPriorLiveId(){
 const match=workspace.match(/function priorLiveId\(view\)\s*\{[^}]*\}/);
 assert.ok(match,'Prior-message reconciliation helper is missing');
 return vm.runInNewContext(match[0]+';priorLiveId');
}

test('completed unsaved reply survives a later send clearing live state',()=>{
 const applyLive=isolatedLiveRenderer();
 const view=sampleView();
 view.savedAssistantById.clear();
 applyLive(view,{
   messageId:'old-assistant-id',text:'NEKO UI ACK',completed:true,
   error:'',activities:[]
 });
 applyLive(view,null);
 assert.equal(view.live.hidden,false);
 assert.equal(view.liveBody.text,'NEKO UI ACK');
 assert.equal(view.currentLiveId,'old-assistant-id');
});

test('saved handoff retains preceding live ID for future reconciliation',()=>{
 const applyLive=isolatedLiveRenderer();
 const priorLiveId=isolatedPriorLiveId();
 const view=sampleView();
 applyLive(view,{
   messageId:'old-assistant-id',text:'NEKO UI ACK',completed:true,
   error:'',activities:[]
 });
 applyLive(view,null);
 assert.equal(view.live.hidden,true);
 assert.equal(view.currentLiveId,'');
 assert.equal(priorLiveId(view),'old-assistant-id');
 view.currentLiveId='new-assistant-id';
 assert.equal(priorLiveId(view),'new-assistant-id');
 assert.match(workspace,/view\.pending\.priorLiveId=priorLiveId\(view\)/);
});

test('a saved message ID alone cannot hide streamed public reply text',()=>{
 const render=workspace.split('function renderHistory(view,info)')[1]
   .split('function applyLive(view,update)')[0];
 assert.match(render,/if\(!item\.text\?\.trim\(\)\)continue/);
 assert.match(render,/savedAssistantById\.set\(item\.id,box\)/);
 assert.match(render,/if\(info\.live\?\.completed && view\.savedAssistantById\.has\(info\.live\.messageId\)\)/);
 assert.doesNotMatch(render,/if\(info\.live\?\.completed && view\.historyMessageIds\.has\(info\.live\.messageId\)\)/);
});

test('a blank terminal SSE frame keeps earlier same-message public text visible',()=>{
 const live=workspace.split('function applyLive(view,update)')[1]
   .split('function receive(next)')[0];
 assert.match(live,/view\.lastNonEmptyLive=\{id:update\.messageId,text:update\.text\}/);
 assert.match(live,/view\.lastNonEmptyLive\?\.id===update\.messageId/);
 assert.match(live,/view\.savedAssistantById\?\.has\(update\.messageId\)/);
 assert.match(live,/showMarkdown\(view\.liveBody,shownText\)/);
 assert.match(live,/view\.live\.hidden=hasSavedReply \|\| !shownText/);
 assert.doesNotMatch(live,/historyMessageIds\?\.has\(update\.messageId\)/);
});
