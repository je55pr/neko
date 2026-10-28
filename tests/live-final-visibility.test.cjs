const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const workspace=fs.readFileSync(path.join(__dirname,'..','src','ui','NekoWorkspace.js'),'utf8');

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
