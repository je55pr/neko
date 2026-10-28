const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');

class MockNode {
  constructor(tag) {
    this.tagName=tag.toUpperCase();
    this.className='';this.dataset={};this.children=[];
    this.style={setProperty(){}};
    this.handlers={};this.isConnected=true;
    this.capturedPointers=new Set();
    this.clientWidth=1450;this.clientHeight=825;
    const classes=new Set();
    this.classList={
      toggle:(key,enabled)=>enabled?classes.add(key):classes.delete(key),
      add:key=>classes.add(key),remove:key=>classes.delete(key),
      contains:key=>classes.has(key)
    };
  }
  append(...children){
    for(const child of children){child.parent=this;this.children.push(child);}
  }
  setAttribute(name,value){this[name]=value;}
  closest(selector){
    const tags=selector.split(',').map(part=>part.trim().toUpperCase());
    for(let node=this;node;node=node.parent)
      if(tags.includes(node.tagName))return node;
    return null;
  }
  addEventListener(name,fn){(this.handlers[name]??=[]).push(fn);}
  setPointerCapture(id){this.capturedPointers.add(id);}
  releasePointerCapture(id){this.capturedPointers.delete(id);}
  getBoundingClientRect(){return {left:0,top:0,width:1450,height:825};}
  emit(name,x=0,y=0,target=this,extras={}){
    const event={type:name,button:0,pointerId:1,
      clientX:x,clientY:y,deltaY:100,
      target,currentTarget:this,preventDefault(){},
      ...extras};
    for(let node=this;node;node=node.parent){
      event.currentTarget=node;
      for(const fn of node.handlers[name]||[])fn(event);
    }
  }
}
function makeCanvas() {
  const post=[],timers=new Map();
  let nextTimer=1;
  const context={
    location:{origin:'https://neko.internal'},
    window:{},document:{createElement:tag=>new MockNode(tag)},
    requestAnimationFrame:fn=>fn(),
    setTimeout:fn=>{const id=nextTimer++;timers.set(id,fn);return id;},
    clearTimeout:id=>timers.delete(id),Math,Number,Map
  };
  vm.runInNewContext(fs.readFileSync(
    path.join(__dirname,'..','src','ui','NekoCanvas.js'),'utf8'),context);
  const canvas=context.window.__nekoCanvas.create((action,data)=>
    post.push({action,data}));
  const flushTimers=()=>{
    const callbacks=[...timers.values()];
    timers.clear();
    for(const callback of callbacks)callback();
  };
  return {canvas,post,timers,flushTimers,
    resizeRect:context.window.__nekoCanvas.resizeRect};
}
const items=Array.from({length:8},(_,i)=>{
  const id='chat-'+i;
  return {id,rect:{x:48+(i%3)*592,y:48+Math.floor(i/3)*772,
    w:550,h:730}};
});
function attach(canvas,ids=items){
  for(const item of ids){
    const panel=new MockNode('article');panel.dataset.id=item.id;
    const head=new MockNode('header');
    panel.append(head);
    canvas.world.append(panel);
    canvas.bindPanel({panel,head});
  }
}

test('fit all, zoom to chat, move and resize are world-space operations',()=>{
  const {canvas,post}=makeCanvas();
  attach(canvas);
  canvas.sync(items,{saved:false});
  let current=canvas.inspect();
  assert(current.camera.zoom<.55);
  assert(canvas.viewport.classList.contains('nwOverview'));
  canvas.focus('chat-0');
  current=canvas.inspect();
  assert(current.camera.zoom>.9);
  const zoom=current.camera.zoom;
  // The bound header is supplied to the helper. Rebind a fresh panel to
  // exercise pointer events with a predictable synthetic target.
  const unique={id:'movable',rect:{x:90,y:110,w:550,h:730}};
  const panel=new MockNode('article');panel.dataset.id=unique.id;
  const head=new MockNode('header');
  panel.append(head);
  canvas.world.append(panel);
  canvas.bindPanel({panel,head});
  canvas.sync([...items,unique],{saved:true,...current.camera});
  const cameraBeforeDrag={...canvas.inspect().camera};
  head.emit('pointerdown',100,100);
  head.emit('pointermove',200,175);
  head.emit('pointerup',200,175);
  const moved=canvas.inspect().rects.movable;
  assert(Math.abs(moved.x-90-100/zoom)<.01);
  assert(Math.abs(moved.y-110-75/zoom)<.01);
  assert.equal(post.at(-1).action,'panel-canvas-rect');
  assert.equal(post.at(-1).data.id,'movable');
  assert.equal(canvas.inspect().camera.x,cameraBeforeDrag.x);
  assert.equal(canvas.inspect().camera.y,cameraBeforeDrag.y);
  assert.equal(canvas.inspect().camera.zoom,cameraBeforeDrag.zoom);
  const sizeHandle=panel.children.find(x=>x.dataset.edge==='se');
  assert(sizeHandle,'bottom-right corner handle is present');
  sizeHandle.emit('pointerdown',100,200);
  sizeHandle.emit('pointermove',190,290);
  sizeHandle.emit('pointerup',190,290);
  const resized=canvas.inspect().rects.movable;
  assert(resized.w>600);
  assert(resized.h>780);
  assert.equal(canvas.inspect().camera.x,cameraBeforeDrag.x);
  assert.equal(canvas.inspect().camera.y,cameraBeforeDrag.y);
  assert.equal(canvas.inspect().camera.zoom,cameraBeforeDrag.zoom);
  assert.equal(panel.children.filter(x=>x.className.startsWith('nwResizeHandle')).length,8);
});

test('camera restore and background panning do not change chat geometry',()=>{
  const {canvas}=makeCanvas();
  attach(canvas,items.slice(0,2));
  canvas.sync(items.slice(0,2),{
    saved:true,x:-300,y:200,zoom:1.3
  });
  const before=canvas.inspect();
  assert.equal(before.camera.zoom,1.3);
  canvas.viewport.emit('pointerdown',400,300);
  canvas.viewport.emit('pointermove',550,350);
  canvas.viewport.emit('pointerup',550,350);
  const after=canvas.inspect();
  assert.equal(after.camera.x,-150);
  assert.equal(after.camera.y,250);
  assert.equal(after.rects['chat-1'].x,before.rects['chat-1'].x);
  canvas.zoomBy(1.25);
  assert(canvas.inspect().camera.zoom>1.3);
});

test('an isolated restored layout keeps customised dimensions',()=>{
  const {canvas}=makeCanvas();
  const original=[{id:'one',rect:{x:-180,y:390,w:900,h:1150}}];
  const panel=new MockNode('article');panel.dataset.id='one';
  canvas.world.append(panel);
  const head=new MockNode('header');panel.append(head);
  canvas.bindPanel({panel,head});
  canvas.sync(original,{saved:true,x:-80,y:155,zoom:.64});
  const output=canvas.inspect();
  assert.equal(output.rects.one.h,1150);
  assert.equal(output.rects.one.w,900);
  assert.equal(output.camera.zoom,.64);
  assert.equal(panel.style.width,'900px');
  assert.equal(panel.style.height,'1150px');
});

test('tidy arrange writes one batch and keeps custom chat sizes',()=>{
  const {canvas,post}=makeCanvas();
  const custom=[
    {id:'A',rect:{x:-120,y:950,w:820,h:900}},
    {id:'B',rect:{x:1800,y:120,w:700,h:1150}}
  ];
  attach(canvas,custom);
  canvas.sync(custom,{saved:true,x:0,y:0,zoom:1});
  canvas.arrange();
  const layout=post.filter(x=>x.action==='panel-canvas-layout');
  assert.equal(layout.length,1);
  assert.equal(layout[0].data.rects.length,2);
  assert.equal(layout[0].data.rects[0].w,820);
  assert.equal(layout[0].data.rects[1].h,1150);
  assert.equal(canvas.inspect().rects.A.x,48);
  assert.equal(canvas.inspect().rects.B.x,910);
});

test('all four sides and corners resize only the requested edges',()=>{
  const {resizeRect}=makeCanvas();
  const start={x:100,y:200,w:600,h:800};
  const examples={
    n:{x:100,y:290,w:600,h:710},
    s:{x:100,y:200,w:600,h:890},
    e:{x:100,y:200,w:675,h:800},
    w:{x:175,y:200,w:525,h:800},
    ne:{x:100,y:290,w:675,h:710},
    nw:{x:175,y:290,w:525,h:710},
    se:{x:100,y:200,w:675,h:890},
    sw:{x:175,y:200,w:525,h:890}
  };
  for(const [edge,expected] of Object.entries(examples)){
    const actual=resizeRect(start,edge,75,90);
    for(const key of ['x','y','w','h'])
      assert.equal(actual[key],expected[key],edge+' should update '+key);
  }
});

test('top and left edges respect minimum and maximum size without drifting',()=>{
  const {resizeRect}=makeCanvas();
  const start={x:100,y:200,w:600,h:800};
  const westMin=resizeRect(start,'w',1000,0);
  assert.equal(westMin.w,360);
  assert.equal(westMin.x+westMin.w,700);
  const westMax=resizeRect(start,'w',-4000,0);
  assert.equal(westMax.w,2200);
  assert.equal(westMax.x+westMax.w,700);
  const northMin=resizeRect(start,'n',0,2000);
  assert.equal(northMin.h,420);
  assert.equal(northMin.y+northMin.h,1000);
  const northMax=resizeRect(start,'n',0,-4000);
  assert.equal(northMax.h,2400);
  assert.equal(northMax.y+northMax.h,1000);
  assert.equal(resizeRect(start,'e',-4000,0).w,360);
  assert.equal(resizeRect(start,'s',0,-4000).h,420);
});

test('left edge drag at zoom keeps right edge fixed and does not drag the chat',()=>{
  const {canvas,post}=makeCanvas();
  const id='test-edge', rect={x:100,y:200,w:600,h:800};
  const panel=new MockNode('article');panel.dataset.id=id;
  const head=new MockNode('header');
  panel.append(head);
  canvas.world.append(panel);
  canvas.bindPanel({panel,head});
  canvas.sync([{id,rect}],{saved:true,x:0,y:0,zoom:1.25});
  assert.equal(panel.children.filter(node=>node.dataset.edge).length,8);
  const left=panel.children.find(node=>node.dataset.edge==='w');
  left.emit('pointerdown',125,125);
  left.emit('pointermove',250,125);
  // An unrelated pointer should not accidentally finish the drag.
  left.emit('pointerup',250,125,left,{pointerId:2});
  left.emit('pointerup',250,125);
  let changed=canvas.inspect().rects[id];
  assert.equal(changed.x,200);
  assert.equal(changed.w,500);
  assert.equal(changed.x+changed.w,700);
  assert.equal(changed.y,200);
  assert.equal(changed.h,800);
  assert.equal(post.filter(item=>item.action==='panel-canvas-rect').length,1);

  // Dragging an area other than the header or borders doesn't move the panel.
  panel.emit('pointerdown',400,400);
  panel.emit('pointermove',500,500);
  panel.emit('pointerup',500,500);
  changed=canvas.inspect().rects[id];
  assert.equal(changed.x,200);
  assert.equal(changed.y,200);

  const top=panel.children.find(node=>node.dataset.edge==='n');
  top.emit('pointerdown',100,100);
  top.emit('pointermove',100,225);
  top.emit('pointerup',100,225);
  changed=canvas.inspect().rects[id];
  assert.equal(changed.y,300);
  assert.equal(changed.h,700);
  assert.equal(changed.y+changed.h,1000);
  assert.equal(post.filter(item=>item.action==='panel-canvas-rect').length,2);

  head.emit('pointerdown',200,200);
  head.emit('pointermove',325,325);
  head.emit('pointerup',325,325);
  changed=canvas.inspect().rects[id];
  assert.equal(changed.x,300);
  assert.equal(changed.y,400);
  assert.equal(changed.w,500);
  assert.equal(changed.h,700);
});

test('invalid persisted camera and panel fields use defaults',()=>{
  const {canvas}=makeCanvas();
  const data=[{id:'invalid',rect:{x:null,y:'',w:[],h:null}}];
  attach(canvas,data);
  canvas.sync(data,{saved:true,x:null,y:'',zoom:null});
  const state=canvas.inspect();
  assert.equal(state.rects.invalid.x,48);
  assert.equal(state.rects.invalid.y,48);
  assert.equal(state.rects.invalid.w,550);
  assert.equal(state.rects.invalid.h,730);
  assert.equal(state.camera.x,0);
  assert.equal(state.camera.y,0);
  assert.equal(state.camera.zoom,1);
});

test('tidy uses row maximum height and each preceding panel width',()=>{
  const {canvas}=makeCanvas();
  const data=[
    {id:'one',rect:{w:900,h:1000}},
    {id:'two',rect:{w:600,h:1250}},
    {id:'three',rect:{w:650,h:700}},
    {id:'four',rect:{w:450,h:500}}
  ];
  attach(canvas,data);
  canvas.sync(data,{saved:true,x:0,y:0,zoom:1});
  canvas.arrange();
  const state=canvas.inspect().rects;
  assert.equal(state.two.x,48+900+42);
  assert.equal(state.three.x,state.two.x+600+42);
  assert.equal(state.four.x,48);
  assert.equal(state.four.y,48+1250+42);
});

test('removing an actively dragged panel cleans up without invalid events',()=>{
  const {canvas,post}=makeCanvas();
  const panel=new MockNode('article');panel.dataset.id='dragged';
  const head=new MockNode('header');panel.append(head);
  canvas.world.append(panel);
  canvas.bindPanel({panel,head});
  canvas.sync([{id:'dragged',rect:{x:10,y:20,w:550,h:730}}],
    {saved:true,x:0,y:0,zoom:1});
  head.emit('pointerdown',100,100);
  head.emit('pointermove',200,200);
  assert(canvas.viewport.classList.contains('nwInteracting'));
  assert(head.capturedPointers.has(1));
  canvas.sync([],{saved:true,x:0,y:0,zoom:1});
  head.emit('pointermove',210,210);
  head.emit('pointerup',210,210);
  assert.equal(head.capturedPointers.size,0);
  assert.equal(canvas.viewport.classList.contains('nwInteracting'),false);
  assert.equal(post.filter(x=>x.action==='panel-canvas-rect').length,0);
});

test('keyboard movement stays within allowable panel bounds',()=>{
  const {canvas,post}=makeCanvas();
  const panel=new MockNode('article');panel.dataset.id='edge';
  const head=new MockNode('header');panel.append(head);
  canvas.world.append(panel);
  canvas.bindPanel({panel,head});
  canvas.sync([{id:'edge',rect:{x:99995,y:-99995,w:550,h:730}}],
    {saved:true,x:0,y:0,zoom:1});
  head.emit('keydown',0,0,head,{ctrlKey:true,shiftKey:true,key:'ArrowRight'});
  head.emit('keydown',0,0,head,{ctrlKey:true,shiftKey:true,key:'ArrowUp'});
  const state=canvas.inspect().rects.edge;
  assert.equal(state.x,100000);
  assert.equal(state.y,-100000);
  const rectEvents=post.filter(x=>x.action==='panel-canvas-rect');
  assert(rectEvents.every(x=>Math.abs(x.data.x)<=100000 &&
    Math.abs(x.data.y)<=100000));
});

test('board switching saves old camera before new board and cleans gestures',()=>{
  const {canvas,post,timers,flushTimers}=makeCanvas();
  const data=[{id:'old',rect:{x:50,y:50,w:550,h:730}}];
  attach(canvas,data);
  canvas.sync(data,{saved:true,x:10,y:20,zoom:1});
  canvas.zoomBy(1.1);
  assert(timers.size>0);
  canvas.viewport.emit('pointerdown',400,400);
  canvas.viewport.emit('pointermove',430,410);
  assert(canvas.viewport.capturedPointers.has(1));
  canvas.switchBoard('b');
  assert.equal(canvas.viewport.capturedPointers.size,0);
  assert.equal(canvas.viewport.classList.contains('nwInteracting'),false);
  assert.equal(Object.keys(canvas.inspect().rects).length,0);
  const oldSave=post.filter(x=>x.action==='panel-canvas-camera');
  assert.equal(oldSave.length,1);
  assert.equal(oldSave[0].data.boardId,'legacy');
  canvas.sync([{id:'next',rect:{x:0,y:0,w:550,h:730}}],
    {saved:false});
  flushTimers();
  const saved=post.filter(x=>x.action==='panel-canvas-camera');
  assert.equal(saved.at(-1).data.boardId,'b');
});
