const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const ui=fs.readFileSync(path.join(__dirname,'..','src','ui','NekoWorkspace.js'),'utf8');

test('at-bottom detection uses a narrow tolerance and explicit user-scroll window',()=>{
 const code=ui.split('const nearTail=')[1].split('const nestedWheelScroller=')[0];
 assert.match(code,/scrollHeight\s*-\s*.*scrollTop/);
 assert.match(code,/Date\.now\(\)\s*<\s*\(view\.userScrollIntentUntil/);
 assert.match(code,/pointerScrolling/);
 assert.match(code,/clientHeight/);
});

test('browser layout scrolls cannot clear followTail',()=>{
 const handler=ui.split("messages.addEventListener('scroll',()=>{")[1]
  .split("messages.addEventListener('pointerdown'")[0];
 assert.match(handler,/const reflowed=view\.lastScrollHeight!==messages\.scrollHeight/);
 assert.match(handler,/if\(reflowed && view\.followTail && !view\.pointerScrolling\)/);
 assert.match(handler,/if\(userScrollIsActive\(view\)\)/);
 assert.match(handler,/view\.followTail=!view\.userScrollAway && nearTail\(messages\)/);
 assert.match(handler,/else if\(view\.followTail\)/);
});

test('only intentional navigation can release sticky following',()=>{
 const end='const boardHotPanels=hotPanels.get(activeBoardId);';
 assert(ui.includes(end),'Expected panel-navigation handler end marker');
 const block=ui.split("messages.addEventListener('pointerdown',event=>{")[1]
  .split(end)[0];
 assert.match(block,/view\.pointerScrolling=true/);
 assert.match(block,/messages\.addEventListener\('touchmove'/);
 assert.match(block,/\['ArrowUp','PageUp','Home'\]/);
 assert.match(block,/view\.userScrollAway=true/);
 assert.match(block,/nestedWheelScroller\(event,messages\)/);
 assert.match(block,/if\(event\.deltaY<0/);
 assert.match(block,/view\.userScrollAway=false/);
});

test('panel resize, message reflow, workspace zoom and restore preserve the follow latch',()=>{
 assert.match(ui,/new ResizeObserver\(\(\)=>followLatest\(view\)\)/);
 assert.match(ui,/new MutationObserver\(\(\)=>followLatest\(view\)\)/);
 assert.match(ui,/messages\.addEventListener\('load',\(\)=>followLatest\(view\),true\)/);
 assert.match(ui,/if\(zoomChanged && mode && !overview\)/);
 assert.match(ui,/if\(view\.followTail\)followLatest\(view\);\s*else\{/);
 assert.match(ui,/view\.followTail=recovered\.followTail!==false/);
 assert.match(ui,/view\.dispose=\(\)=>/);
 assert.match(ui,/view\.dispose\?\.\(\)/);
});
