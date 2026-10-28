const {test}=require('node:test');
const assert=require('node:assert/strict');
const path=require('node:path');

const root=path.join(__dirname,'..');
const markdownit=require(path.join(root,'src','ui','vendor','markdown-it.min.js'));
const modulePath=path.join(root,'src','ui','NekoMarkdown.js');

function renderer(){
  const sent=[];
  const window={markdownit,__nekoShell:{post:(action,data)=>sent.push({action,data})}};
  const document={
    createElement:()=>({cloneNode:()=>({})}),
    getElementById:()=>null
  };
  globalThis.window=window;
  globalThis.document=document;
  globalThis.location={origin:'https://neko.internal'};
  delete require.cache[require.resolve(modulePath)];
  require(modulePath);
  assert.equal(window.__nekoMarkdown?.ready,true);
  function render(text){
    const el={
      classList:{add(){}},
      _html:'',
      get innerHTML(){return this._html;},
      set innerHTML(value){this._html=value;},
      anchors:[],
      querySelectorAll(){
        return this.anchors;
      }
    };
    // Lightweight anchor stand-ins to verify the real outgoing host event.
    Object.defineProperty(el,'innerHTML',{
      get(){return this._html;},
      set(html){
        this._html=html;
        this.anchors=[...html.matchAll(/<a href="([^"]+)"[^>]*>/g)].map(match=>{
          const handlers={};
          return {
            getAttribute:()=>match[1],
            addEventListener:(kind,cb)=>{handlers[kind]=cb;},
            click(){
              let prevented=false;
              handlers.click?.({preventDefault(){prevented=true;}});
              return prevented;
            }
          };
        });
      }
    });
    window.__nekoMarkdown.render(el,text);
    return el;
  }
  return {render,sent};
}

test('unformatted conversation URLs become real clickable anchors',()=>{
  const {render,sent}=renderer();
  const item=render('See https://example.org/c/example-conversation for context.');
  assert.match(item.innerHTML,
    /href="https:\/\/example\.org\/c\/example-conversation"/);
  assert.equal(item.anchors.length,1);
  assert.equal(item.anchors[0].click(),true);
  assert.equal(sent.length,1);
  assert.equal(sent[0].action,'open-external-url');
  assert.equal(sent[0].data.url,
    'https://example.org/c/example-conversation');
});

test('bare domains, Markdown links, and trailing punctuation work',()=>{
  const {render}=renderer();
  const item=render('Visit www.example.org, [Docs](https://example.org/help) and https://example.org/path.');
  assert.equal(item.anchors.length,3);
  assert.match(item.innerHTML,/href="http:\/\/www\.example\.org"/);
  assert.match(item.innerHTML,/href="https:\/\/example\.org\/help"/);
  assert.match(item.innerHTML,/href="https:\/\/example\.org\/path"/);
  assert.doesNotMatch(item.innerHTML,/href="https:\/\/example\.org\/path\."/);
});

test('URL-like text in code and dangerous protocols stay unlinked',()=>{
  const {render,sent}=renderer();
  const item=render([
    '`https://example.org/c/code-only`',
    '```text',
    'https://example.org/c/fenced-only',
    '```',
    '[unsafe](javascript:alert(1))',
    '<script>alert(1)</script>',
    'https://example.org/c/visible'
  ].join('\n'));
  assert.equal(item.anchors.length,1);
  assert.match(item.innerHTML,/&lt;script&gt;/);
  assert.doesNotMatch(item.innerHTML,/<script\b/i);
  assert.doesNotMatch(item.innerHTML,/href="javascript:/);
  assert.equal(sent.length,0);
});

test('rendering text does not cause a navigation without a click',()=>{
  const {render,sent}=renderer();
  render('https://example.org/c/no-automatic-navigation');
  assert.equal(sent.length,0);
  const img=render('![tracker](https://example.org/pixel.png)');
  assert.doesNotMatch(img.innerHTML,/<img\b/i);
  assert.match(img.innerHTML,/nekoMediaPlaceholder/);
  const task=render('- [x] done\n- [ ] open');
  assert.match(task.innerHTML,/<input type="checkbox"[^>]*disabled checked/);
  assert.match(task.innerHTML,/<input type="checkbox"[^>]*disabled(?! checked)/);
});
