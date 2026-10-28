const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const workspace=fs.readFileSync(path.join(__dirname,'..','src','ui','NekoWorkspace.js'),'utf8');

test('completed live activity is anchored immediately before its saved final message',()=>{
 const anchor=workspace.split('function placeLiveActivity(view,update)')[1]
   .split('function drawPending(view)')[0];
 assert.match(anchor,/view\.savedAssistantById\?\.get\(update\?\.messageId\)/);
 assert.match(anchor,/const anchor=associatedFinal\|\|view\.live/);
 assert.match(anchor,/view\.messages\.insertBefore\(view\.activityHost,anchor\)/);
 const history=workspace.split('function renderHistory(view,info)')[1]
   .split('function applyLive(view,update)')[0];
 assert.match(history,/const savedAssistantById=new Map\(\)/);
 assert.match(history,/if\(item\.role==='assistant' && kind==='message' && item\.id\)/);
 assert.match(history,/savedAssistantById\.set\(item\.id,box\)/);
 assert.match(history,/view\.savedAssistantById=savedAssistantById/);
 assert.match(history,/placeLiveActivity\(view,info\.live\)/);
});

test('new SSE message repositions activities after the earlier completed reply',()=>{
 const live=workspace.split('function applyLive(view,update)')[1]
   .split('function receive(next)')[0];
 assert.match(live,/view\.currentLiveId=update\.messageId\|\|''/);
 assert.match(live,/placeLiveActivity\(view,update\)/);
 assert.match(live,/applyActivities\(view,update\.activities\|\|\[\]\)/);
 const pending=workspace.split('function drawPending(view)')[1]
   .split('function showMarkdown(element,text)')[0];
 assert.match(pending,/priorFinalIsSaved=!!view\.savedAssistantById\?\.has\(/);
 assert.match(pending,/pending\.priorLiveId===view\.currentLiveId/);
 assert.match(pending,/view\.messages\.insertBefore\(view\.localEcho,view\.live\)/);
 assert.match(pending,/view\.messages\.insertBefore\(view\.localEcho,view\.activityHost\)/);
});

test('tool/progress deduplication and on-wire sends are unchanged',()=>{
 const activities=workspace.split('function applyActivities(view,activities)')[1]
   .split('function renderHistory(view,info)')[0];
 assert.match(activities,/!view\.historyMessageIds\?\.has\(item\.id\)/);
 assert.match(activities,/view\.activityHost\.replaceChildren\(\.\.\.children\)/);
 const anchor=workspace.split('function placeLiveActivity(view,update)')[1]
   .split('function drawPending(view)')[0];
 assert.doesNotMatch(anchor,/post\(|fetch\(|SendAsync|SubmitPrepared/);
});
