const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');

const surface=fs.readFileSync(
  path.join(__dirname,'..','src','ui','NekoSurface.js'),'utf8');

function section(start,end){
  const a=surface.indexOf(start);
  assert(a>=0,'Missing UI entry point: '+start);
  const b=surface.indexOf(end,a+start.length);
  assert(b>a,'Missing UI boundary: '+end);
  return surface.slice(a,b);
}

test('resource failures are captured at the window boundary',()=>{
  const handler=section("window.addEventListener('error'",
    "window.addEventListener('unhandledrejection'");
  assert(handler.trimEnd().endsWith('},true);'));
  assert(handler.includes("code:isResource?'script_load_failed'"));
});

test('received snapshots refresh library and selection-dependent keys',()=>{
  const library=section('function renderLibrary()','function render');
  assert(library.includes('!!state?.project?.visible'));
  const receive=section('function receive(next)','if (document.documentElement)');
  assert(receive.includes('renderLibrary();'));
});

test('activity disclosures retain state during streamed UI rebuilds',()=>{
  const render=section('function renderSingleActivity(item)',
    'function receive(next)');
  assert(render.includes('singleActivityOpen.has(item.id)'));
  assert(render.includes("detail.addEventListener('toggle'"));
  const receive=section('function receive(next)','if (document.documentElement)');
  assert(receive.includes('singleActivityOpen.clear()'));
});
