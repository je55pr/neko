(() => {
  if(location.origin!=='https://neko.internal')return;
  const clamp=(n,min,max)=>Math.max(min,Math.min(max,n));
  const finite=(n,fallback)=>
    (typeof n==='number'||(typeof n==='string'&&n.trim()!=='')) &&
    Number.isFinite(Number(n))?Number(n):fallback;
  const bounds=r=>({
    x:clamp(finite(r?.x,48),-100000,100000),
    y:clamp(finite(r?.y,48),-100000,100000),
    w:clamp(finite(r?.w,550),360,2200),
    h:clamp(finite(r?.h,730),420,2400)
  });
  const resizeEdges=['n','s','e','w','ne','nw','se','sw'];
  // The opposite edge remains fixed. When a minimum/maximum size is reached,
  // stop changing the dragged edge instead of shifting the whole panel.
  function resizeRect(start,edge,dx,dy) {
    const r={...start};
    if(edge.includes('w')) {
      const right=start.x+start.w;
      r.x=clamp(start.x+dx,
        Math.max(-100000,right-2200),
        Math.min(100000,right-360));
      r.w=right-r.x;
    } else if(edge.includes('e'))r.w=clamp(start.w+dx,360,2200);
    if(edge.includes('n')) {
      const bottom=start.y+start.h;
      r.y=clamp(start.y+dy,
        Math.max(-100000,bottom-2400),
        Math.min(100000,bottom-420));
      r.h=bottom-r.y;
    } else if(edge.includes('s'))r.h=clamp(start.h+dy,420,2400);
    return r;
  }
  function create(post) {
    let boardId='legacy';
    const emit=(action,data)=>post(action,{...data,boardId});
    const viewport=document.createElement('div');
    viewport.className='nwViewport';
    viewport.tabIndex=0;
    viewport.setAttribute('aria-label','Two dimensional conversation workspace');
    const world=document.createElement('div');
    world.className='nwWorld';
    viewport.append(world);
    const rects=new Map();
    let camera={x:0,y:0,zoom:1};
    let initialised=false, gesture=null, viewTimer=null;
    let onZoom=()=>{};
    function draw() {
      world.style.transform=`translate3d(${camera.x}px,${camera.y}px,0) scale(${camera.zoom})`;
      viewport.classList.toggle('nwOverview',camera.zoom<.55);
      // Compensate label sizes at overview zoom: eight distant chat panels
      // should still show readable titles/status, not microscopic lettering.
      const physical=(px,ceiling)=>Math.min(ceiling,px/camera.zoom)+'px';
      viewport.style.setProperty('--nw-overview-title',physical(14,125));
      viewport.style.setProperty('--nw-overview-status',physical(12,105));
      viewport.style.setProperty('--nw-overview-body',physical(13,115));
      viewport.style.setProperty('--nw-overview-icon',physical(13,90));
      // Keep borders easy to grab under zoom without swallowing the interior.
      viewport.style.setProperty('--nw-edge-hit',
        Math.min(80,Math.max(8,9/camera.zoom))+'px');
      viewport.style.setProperty('--nw-corner-hit',
        Math.min(120,Math.max(18,19/camera.zoom))+'px');
      viewport.style.setProperty('--nw-grid-step',Math.max(16,Math.round(60*camera.zoom))+'px');
      viewport.style.setProperty('--nw-grid-x',camera.x+'px');
      viewport.style.setProperty('--nw-grid-y',camera.y+'px');
      onZoom(Math.round(camera.zoom*100));
    }
    function saveView() {
      clearTimeout(viewTimer);
      emit('panel-canvas-camera',{x:camera.x,y:camera.y,zoom:camera.zoom});
    }
    function laterSave() {
      clearTimeout(viewTimer);
      viewTimer=setTimeout(saveView,280);
    }
    function viewportSize(){
      return {w:viewport.clientWidth||1200,h:viewport.clientHeight||780};
    }
    function setCamera(x,y,zoom,persist=true){
      camera={x:clamp(finite(x,0),-100000,100000),
        y:clamp(finite(y,0),-100000,100000),
        zoom:clamp(finite(zoom,1),.08,2.5)};
      draw();
      if(persist)laterSave();
    }
    function zoomAt(x,y,targetZoom){
      const z=clamp(targetZoom,.08,2.5);
      const wx=(x-camera.x)/camera.zoom,wy=(y-camera.y)/camera.zoom;
      setCamera(x-wx*z,y-wy*z,z);
    }
    function zoomBy(factor){
      const size=viewportSize();
      zoomAt(size.w/2,size.h/2,camera.zoom*factor);
    }
    function fitAll(){
      const values=[...rects.values()];
      if(!values.length)return;
      const left=Math.min(...values.map(r=>r.x));
      const top=Math.min(...values.map(r=>r.y));
      const right=Math.max(...values.map(r=>r.x+r.w));
      const bottom=Math.max(...values.map(r=>r.y+r.h));
      const size=viewportSize();
      const scale=clamp(Math.min((size.w-90)/(right-left),
        (size.h-80)/(bottom-top),1.3),.08,2.5);
      setCamera((size.w-(left+right)*scale)/2,
        (size.h-(top+bottom)*scale)/2,scale);
    }
    function focus(id){
      const r=rects.get(id);if(!r)return;
      const size=viewportSize();
      const scale=clamp(Math.min((size.w-72)/r.w,
        (size.h-55)/r.h,1.7),.08,2.5);
      setCamera(size.w/2-(r.x+r.w/2)*scale,
        size.h/2-(r.y+r.h/2)*scale,scale);
    }
    function arrange(){
      const placements=[];
      let x=48,y=48,rowHeight=0,column=0;
      for(const [id,r] of rects) {
        if(column===3){x=48;y+=rowHeight+42;rowHeight=0;column=0;}
        r.x=x;r.y=y;
        x+=r.w+42;
        rowHeight=Math.max(rowHeight,r.h);
        column++;
        paint(id);
        placements.push({id,...r});
      }
      if(placements.length)emit('panel-canvas-layout',{rects:placements});
      fitAll();
    }
    function paint(id) {
      const r=rects.get(id);
      const node=[...world.children].find(x=>x.dataset.id===id);
      if(!r||!node)return;
      node.style.left=r.x+'px';node.style.top=r.y+'px';
      node.style.width=r.w+'px';node.style.height=r.h+'px';
    }
    function sync(panelInfo,cameraInfo){
      const ids=new Set(panelInfo.map(x=>x.id));
      for(const id of rects.keys())if(!ids.has(id))rects.delete(id);
      if(gesture?.type==='panel' && !rects.has(gesture.id))cancelGesture();
      for(const item of panelInfo){
        if(!rects.has(item.id))rects.set(item.id,bounds(item.rect));
        paint(item.id);
      }
      if(!initialised) {
        initialised=true;
        if(cameraInfo?.saved) {
          setCamera(cameraInfo.x,cameraInfo.y,cameraInfo.zoom,false);
        }else{
          requestAnimationFrame(()=>{if(viewport.isConnected)fitAll();});
        }
      }
      draw();
    }
    function cancelGesture(){
      const g=gesture;
      gesture=null;
      if(g?.target?.releasePointerCapture){
        try{g.target.releasePointerCapture(g.pointerId);}catch{}
      }
      viewport.classList.remove('nwInteracting');
    }
    function finishGesture(event){
      if(!gesture ||
         (gesture.pointerId!==undefined &&
          gesture.pointerId!==event.pointerId))return;
      const g=gesture;
      if(g.type==='panel' && g.moved){
        const r=rects.get(g.id);
        if(r)emit('panel-canvas-rect',{id:g.id,...r});
      }
      if(g.type==='pan' && g.moved)saveView();
      cancelGesture();
    }
    function bindPanel(view) {
      const {panel,head}=view;
      if(panel.dataset.nwCanvasBound)return;
      panel.dataset.nwCanvasBound='true';
      head.draggable=false;
      head.title='Drag chat to move it; double-click to zoom in';
      head.setAttribute('aria-label','Drag this chat around the board; double-click to focus');
      const handleStart=(event,action,edge='')=>{
        if(event.button!==0||gesture)return;
        if(action==='move' && event.target.closest(
          'button, textarea, input, select, a, summary'))return;
        event.preventDefault();
        const r=rects.get(panel.dataset.id);
        if(!r)return;
        gesture={type:'panel',id:panel.dataset.id,pointerId:event.pointerId,
          sx:event.clientX,sy:event.clientY,start:{...r},
          action,edge,target:event.currentTarget,moved:false};
        viewport.classList.add('nwInteracting');
        if(event.currentTarget.setPointerCapture)
          event.currentTarget.setPointerCapture(event.pointerId);
      };
      const move=event=>{
        const g=gesture;
        if(!g||g.type!=='panel'||g.id!==panel.dataset.id ||
           g.pointerId!==event.pointerId)return;
        const r=rects.get(g.id);
        if(!r){cancelGesture();return;}
        const dx=(event.clientX-g.sx)/camera.zoom;
        const dy=(event.clientY-g.sy)/camera.zoom;
        if(g.action==='resize'){
          Object.assign(r,resizeRect(g.start,g.edge,dx,dy));
        }else{
          r.x=clamp(g.start.x+dx,-100000,100000);
          r.y=clamp(g.start.y+dy,-100000,100000);
        }
        if(r.x!==g.start.x||r.y!==g.start.y||
           r.w!==g.start.w||r.h!==g.start.h)g.moved=true;
        paint(g.id);
      };
      head.addEventListener('pointerdown',event=>handleStart(event,'move'));
      head.addEventListener('pointermove',move);
      head.addEventListener('pointerup',finishGesture);
      head.addEventListener('pointercancel',finishGesture);
      head.addEventListener('dblclick',event=>{
        if(!event.target.closest('button'))focus(panel.dataset.id);
      });
      panel.addEventListener('dblclick',event=>{
        // The overview shows a large summary instead of the composer.
        // Double-click anywhere on that summary to enter reading mode.
        if(viewport.classList.contains('nwOverview') &&
           !event.target.closest('button'))
          focus(panel.dataset.id);
      });
      // All four sides and all four corners resize. Only the header moves
      // the panel; resize hit targets never trigger a move or text selection.
      const edges={
        n:'top edge',s:'bottom edge',e:'right edge',w:'left edge',
        ne:'top-right corner',nw:'top-left corner',
        se:'bottom-right corner',sw:'bottom-left corner'
      };
      for(const edge of resizeEdges){
        const handle=document.createElement('div');
        handle.className='nwResizeHandle nwResize-'+edge;
        handle.dataset.edge=edge;
        handle.title='Drag '+edges[edge]+' to resize';
        handle.setAttribute('role','separator');
        handle.setAttribute('aria-label','Resize from '+edges[edge]);
        panel.append(handle);
        handle.addEventListener('pointerdown',event=>
          handleStart(event,'resize',edge));
        handle.addEventListener('pointermove',move);
        handle.addEventListener('pointerup',finishGesture);
        handle.addEventListener('pointercancel',finishGesture);
      }
      // Ctrl+arrows nudge position when keyboard focus is on the header.
      head.addEventListener('keydown',event=>{
        if(!event.ctrlKey||!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key))return;
        event.preventDefault();
        const r=rects.get(panel.dataset.id);if(!r)return;
        const delta=event.shiftKey?100:20;
        if(event.key==='ArrowLeft')r.x=clamp(r.x-delta,-100000,100000);
        if(event.key==='ArrowRight')r.x=clamp(r.x+delta,-100000,100000);
        if(event.key==='ArrowUp')r.y=clamp(r.y-delta,-100000,100000);
        if(event.key==='ArrowDown')r.y=clamp(r.y+delta,-100000,100000);
        paint(panel.dataset.id);
        emit('panel-canvas-rect',{id:panel.dataset.id,...r});
      });
    }
    viewport.addEventListener('pointerdown',event=>{
      if(gesture)return;
      if(event.button!==1 && (event.button!==0||event.target!==viewport))return;
      event.preventDefault();
      gesture={type:'pan',pointerId:event.pointerId,
        sx:event.clientX,sy:event.clientY,
        start:{...camera},target:viewport,moved:false};
      viewport.classList.add('nwInteracting');
      if(viewport.setPointerCapture)viewport.setPointerCapture(event.pointerId);
    });
    viewport.addEventListener('pointermove',event=>{
      if(gesture?.type!=='pan')return;
      const dx=event.clientX-gesture.sx,dy=event.clientY-gesture.sy;
      if(Math.abs(dx)+Math.abs(dy)>1)gesture.moved=true;
      setCamera(gesture.start.x+dx,gesture.start.y+dy,camera.zoom,false);
    });
    viewport.addEventListener('pointerup',event=>{
      if(gesture?.type==='pan')finishGesture(event);
    });
    viewport.addEventListener('pointercancel',event=>{
      if(gesture?.type==='pan')finishGesture(event);
    });
    viewport.addEventListener('wheel',event=>{
      const insideContent=event.target.closest('.nwMessages, .nwComposer');
      if(insideContent&&!event.ctrlKey&&!event.altKey)return;
      event.preventDefault();
      const rect=viewport.getBoundingClientRect();
      const factor=Math.exp(-event.deltaY*.0015);
      zoomAt(event.clientX-rect.left,event.clientY-rect.top,camera.zoom*factor);
    },{passive:false});
    function switchBoard(nextBoardId){
      if(nextBoardId===boardId)return;
      // Persist the old camera with its old board ID before changing context.
      if(viewTimer){saveView();viewTimer=null;}
      cancelGesture();
      boardId=nextBoardId;
      rects.clear();
      initialised=false;
      camera={x:0,y:0,zoom:1};
      draw();
    }
    return {viewport,world,bindPanel,sync,focus,fitAll,zoomBy,arrange,switchBoard,
      setZoomListener(fn){onZoom=fn;draw();},
      inspect(){return {camera:{...camera},rects:Object.fromEntries(
        [...rects].map(([id,r])=>[id,{...r}]))};}};
  }
  window.__nekoCanvas={create,resizeRect};
})();
