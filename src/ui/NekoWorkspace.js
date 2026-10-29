(() => {
  if(location.origin!=='https://neko.internal')return;
  let root,grid,toolbar,canvas,mode=false,lastIds='';
  let snapshot=null, shellSnapshot=null, pickerOpen=false, newChatOpen=false;
  let newChatAwaiting=false,hotDialogState=null;
  let views=new Map(),activeBoardId=null;
  const boardViews=new Map();
  const hotPanels=new Map(),hotCameras=new Map();
  // Reading attention belongs to the conversation, not to an individual
  // panel. The same chat may be pinned to several named boards.
  const chatAttention=new Map();

  // Bottom-following is a user preference, not a measurement that should be
  // revoked by the browser's automatic scroll adjustment during layout.
  const nearTail=scroll=>scroll.scrollHeight-scroll.scrollTop-scroll.clientHeight<=2;
  const userScrollIsActive=view=>view.pointerScrolling ||
    Date.now()<(view.userScrollIntentUntil||0);
  const rememberUserScroll=(view,ms=450)=>{
    view.userScrollIntentUntil=Date.now()+ms;
  };
  const nestedWheelScroller=(event,container)=>{
    for(let node=event.target;node&&node!==container;node=node.parentElement){
      if(node.scrollHeight<=node.clientHeight+1)continue;
      const overflow=window.getComputedStyle(node).overflowY;
      if(!/auto|scroll/.test(overflow))continue;
      if(event.deltaY<0 && node.scrollTop>0 ||
         event.deltaY>0 && node.scrollTop+node.clientHeight<node.scrollHeight-1)
        return true;
    }
    return false;
  };
  // Allow Markdown and layout to settle before scrolling. Keep queued
  // stream updates together, and let the user's scroll always take priority.
  function followLatest(view){
    if(!view.followTail||view.tailQueued)return;
    view.tailQueued=true;
    view.autoScrolling=true;
    requestAnimationFrame(()=>{
      if(view.followTail&&mode&&!root.hidden&&view.panel.isConnected)
        view.messages.scrollTop=view.messages.scrollHeight;
      requestAnimationFrame(()=>{
        if(view.followTail&&mode&&!root.hidden&&view.panel.isConnected)
          view.messages.scrollTop=view.messages.scrollHeight;
        view.tailQueued=false;
        view.autoScrolling=false;
      });
    });
  }
  const post=(action,data={})=>window.__nekoShell?.post?.(action,data);
  const el=(tag,cls='',text='')=>{
    const node=document.createElement(tag);node.className=cls;node.textContent=text;
    return node;
  };
  function mount(){
    const shadow=document.querySelector('#neko-shell-surface')?.shadowRoot;
    if(!shadow || root)return;
    const css=el('style');
    css.textContent=`
      #nekoWorkspace{flex:1;min-height:0;min-width:0;display:flex;flex-direction:column;background:#0c1116}
      #nekoWorkspace[hidden]{display:none}
      .nwToolbar{display:flex;gap:9px;align-items:center;padding:12px 20px;border-bottom:1px solid #293842;flex-wrap:wrap}
      .nwToolbar b{font-size:19px;margin-right:auto}
      .nwToolbar button,.nwToolbar select,.nwToolbar input{
        border:1px solid #38514e;border-radius:9px;background:#1b302e;
        color:#daefe7;padding:8px 11px;font-size:15px}
      .nwBoardSelect{min-width:130px;max-width:235px;font-weight:650}
      .nwBoardRename{min-width:130px;max-width:235px}
      .nwToolbar .nwBoardDanger{border-color:#775751;color:#f2c4b8}
      .nwToolbar .nwPrimary{background:#b0e6d9;color:#123126;font-weight:700}
      .nwOverlay{position:absolute;z-index:60;inset:0;background:#081015b8;
        display:flex;justify-content:center;align-items:flex-start;padding:clamp(24px,7vh,72px) 14px}
      .nwOverlay[hidden]{display:none}
      .nwDialog{background:#142026;color:#e5f4ef;border:1px solid #416259;
        border-radius:16px;box-shadow:0 20px 75px #000b;padding:22px;
        width:min(600px,100%);max-height:min(80vh,850px);
        display:flex;flex-direction:column;gap:12px}
      .nwDialogHeader{display:flex;align-items:center;gap:16px}
      .nwDialogHeader strong{font-size:19px;flex:1}
      .nwDialog button{background:#29453f;border:1px solid #42675c;
        color:#e7f5ef;border-radius:8px;padding:8px 12px;cursor:pointer}
      .nwDialog button:disabled{opacity:.5;cursor:default}
      .nwDialog button.nwPrimary{background:#b0e6d9;color:#153428}
      .nwDialog input,.nwDialog textarea{background:#0d191e;color:#f0f5f5;
        border:1px solid #496259;border-radius:9px;padding:12px;font:inherit}
      .nwPickerResults{overflow-y:auto;min-height:0;display:flex;
        flex-direction:column;gap:5px}
      .nwPickRow{display:flex;align-items:center;gap:9px;padding:9px;
        border-radius:9px;background:#1c2c30}
      .nwPickTitle{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;
        white-space:nowrap;font-size:15px}
      .nwDialogHint{font-size:13px;color:#a7bbb8}
      .nwNewMessage{min-height:140px;resize:vertical}
      .nwOverlayActions{display:flex;align-items:center;justify-content:flex-end;gap:10px}
      .nwViewport{flex:1;min-height:0;min-width:0;position:relative;overflow:hidden;
        cursor:grab;touch-action:none;
        background-color:#0c151a;
        background-image:radial-gradient(circle,#315149 1.1px,transparent 1.1px);
        background-size:var(--nw-grid-step,60px) var(--nw-grid-step,60px);
        background-position:var(--nw-grid-x,0px) var(--nw-grid-y,0px)}
      .nwViewport.nwInteracting{cursor:grabbing}
      .nwWorld{position:absolute;left:0;top:0;width:0;height:0;
        transform-origin:0 0;will-change:transform;cursor:default}
      /* The whole card carries its status, even at distant canvas zoom.
         Keep message content legible by tinting dark surfaces rather than text. */
      .nwPanel{
        --nwBody:#111b21;--nwHead:#1a2b2d;--nwComposerBg:#17252b;
        --nwDraftBg:#0f1b20;--nwEdge:#39534f;--nwGlow:#0000;
        position:absolute;box-sizing:border-box;
        border:2px solid var(--nwEdge);border-radius:16px;overflow:hidden;
        display:flex;flex-direction:column;background:var(--nwBody);cursor:default;
        box-shadow:0 15px 45px #0008;
        transition:background-color .3s ease,border-color .3s ease,
          box-shadow .3s ease}
      .nwPanelHead{display:flex;gap:6px;align-items:center;padding:10px;
        background:var(--nwHead);border-bottom:1px solid var(--nwEdge);
        min-height:54px;cursor:grab;user-select:none;
        transition:background-color .3s ease,border-color .3s ease}
      .nwPanelHead:active{cursor:grabbing}
      .nwResizeHandle{position:absolute;z-index:8;touch-action:none;user-select:none;
        background:transparent;box-sizing:border-box}
      .nwResizeHandle:hover{background:rgba(156,228,207,.23)}
      .nwResize-n{top:0;left:var(--nw-corner-hit,20px);
        right:var(--nw-corner-hit,20px);height:var(--nw-edge-hit,10px);cursor:ns-resize}
      .nwResize-s{bottom:0;left:var(--nw-corner-hit,20px);
        right:var(--nw-corner-hit,20px);height:var(--nw-edge-hit,10px);cursor:ns-resize}
      .nwResize-w{left:0;top:var(--nw-corner-hit,20px);
        bottom:var(--nw-corner-hit,20px);width:var(--nw-edge-hit,10px);cursor:ew-resize}
      .nwResize-e{right:0;top:var(--nw-corner-hit,20px);
        bottom:var(--nw-corner-hit,20px);width:var(--nw-edge-hit,10px);cursor:ew-resize}
      .nwResize-nw,.nwResize-ne,.nwResize-sw,.nwResize-se{
        width:var(--nw-corner-hit,20px);height:var(--nw-corner-hit,20px)}
      .nwResize-nw{top:0;left:0;cursor:nwse-resize}
      .nwResize-se{bottom:0;right:0;cursor:nwse-resize}
      .nwResize-ne{top:0;right:0;cursor:nesw-resize}
      .nwResize-sw{bottom:0;left:0;cursor:nesw-resize}
      .nwOverviewText{display:none;padding:24px 22px;color:#e4f1ed;
        background:var(--nwBody);
        font-size:var(--nw-overview-body,36px);line-height:1.4;overflow:hidden;overflow-wrap:anywhere;
        white-space:pre-wrap;flex:1}
      .nwOverview .nwPanel .nwMessages,.nwOverview .nwPanel .nwComposer{display:none}
      .nwOverview .nwOverviewText{display:block}
      .nwOverview .nwPanelHead{min-height:110px}
      .nwOverview .nwPanelTitle{font-size:var(--nw-overview-title,42px)}
      .nwOverview .nwPanelHead button{font-size:var(--nw-overview-icon,37px);padding:7px 12px}
      .nwOverview .nwPanelHead button[title^="Refresh"],
      .nwOverview .nwPanelHead button[title^="Remove"]{display:none}
      .nwPanelTitle{font-size:17px;font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;min-width:0}
      .nwDragGrip{font-size:18px;color:#81aa9c;line-height:1}
      .nwPanelHead button{font-size:17px;color:#c3ddd4;background:transparent;border:1px solid #38534d;border-radius:7px;padding:3px 7px;flex:none;cursor:pointer}
      .nwPanelHead button:hover{background:#2d4741}
      /* Quiet by default: state lives in the header, not an idle banner. */
      .nwStatus{font-size:13px;color:#f0c8a8;background:#382923;
        padding:10px 13px;overflow-wrap:anywhere;line-height:1.4}
      .nwStatus[hidden]{display:none}
      .nwStatus[data-mode="waiting"]{background:#1d3341;color:#bcdce9}
      .nwIndicator{position:relative;display:inline-flex;align-items:center;
        justify-content:center;flex:none;width:21px;height:21px;
        color:#9cbdba;cursor:help}
      .nwIndicator[hidden],.nwUnreadDot[hidden]{display:none}
      .nwIndicator::before{content:'';display:block}
      .nwIndicator[data-state="sending"]::before{
        content:'↑';font-size:22px;font-weight:800;color:#addfd3;
        animation:nwSendFloat 1.25s ease-in-out infinite}
      .nwIndicator[data-state="thinking"]::before,
      .nwIndicator[data-state="loading"]::before{
        width:12px;height:12px;border:2px solid #4a706a;
        border-top-color:#b0e6d9;border-radius:50%;
        animation:nwSpin 1.25s linear infinite}
      .nwIndicator[data-state="tools"]::before{
        content:'⚙';font-size:20px;line-height:1;color:#a5d8c9;
        animation:nwSpin 4s linear infinite}
      .nwIndicator[data-state="streaming"]::before{
        content:'';width:9px;height:9px;background:#9bdcca;border-radius:50%;
        animation:nwHeartbeat 1.7s ease-in-out infinite}
      .nwIndicator[data-state="error"]::before{
        content:'!';border:1.5px solid #e9b18b;color:#f6c49f;
        border-radius:50%;font-size:13px;font-weight:850;
        width:16px;height:16px;text-align:center;line-height:16px}
      .nwUnreadDot{height:9px;width:9px;flex:none;background:#9ce5d0;
        border-radius:50%;box-shadow:0 0 0 2px #253b38;cursor:help}
      /* Each state colours the full panel: header, reading surface, composer
         and draft field. Unread applies only while the chat is otherwise idle. */
      .nwPanel[data-nw-state="loading"]{
        --nwBody:#203242;--nwHead:#33546d;--nwComposerBg:#273e50;
        --nwDraftBg:#1c3445;--nwEdge:#7c9eb8;--nwGlow:#6899b344}
      .nwPanel[data-nw-state="sending"]{
        --nwBody:#193954;--nwHead:#2e5d80;--nwComposerBg:#23455f;
        --nwDraftBg:#183a53;--nwEdge:#81c3ef;--nwGlow:#61a7df64}
      .nwPanel[data-nw-state="thinking"]{
        --nwBody:#302b4b;--nwHead:#54477a;--nwComposerBg:#3b355b;
        --nwDraftBg:#2d2a46;--nwEdge:#b4a5f1;--nwGlow:#9b8bdf66}
      .nwPanel[data-nw-state="tools"]{
        --nwBody:#41301f;--nwHead:#71522e;--nwComposerBg:#503c26;
        --nwDraftBg:#3d3022;--nwEdge:#e3b679;--nwGlow:#d4a24d64}
      .nwPanel[data-nw-state="streaming"]{
        --nwBody:#154333;--nwHead:#296a52;--nwComposerBg:#1a503e;
        --nwDraftBg:#153d30;--nwEdge:#75e0b5;--nwGlow:#57c99b66}
      .nwPanel.nwHasUnread[data-nw-state="idle"]{
        --nwBody:#1b423c;--nwHead:#2b735f;--nwComposerBg:#245345;
        --nwDraftBg:#1b4237;--nwEdge:#87efc8;--nwGlow:#58cba050}
      .nwPanel[data-nw-state="error"]{
        --nwBody:#492a2a;--nwHead:#79413e;--nwComposerBg:#573432;
        --nwDraftBg:#422a28;--nwEdge:#f0a084;--nwGlow:#c7776266}
      .nwPanel[data-nw-state="sending"],
      .nwPanel[data-nw-state="thinking"],
      .nwPanel[data-nw-state="tools"],
      .nwPanel[data-nw-state="streaming"]{
        animation:nwPanelGlow 2.8s ease-in-out infinite}
      .nwPanel[data-nw-state="loading"],
      .nwPanel[data-nw-state="error"],
      .nwPanel.nwHasUnread[data-nw-state="idle"]{
        box-shadow:0 15px 45px #0008,0 0 24px var(--nwGlow)}
      /* A moving light track is readable at low zoom. It never intercepts
         dragging, scrolling, or the resize handles. */
      .nwPanel[data-nw-state="sending"]::after,
      .nwPanel[data-nw-state="thinking"]::after,
      .nwPanel[data-nw-state="tools"]::after,
      .nwPanel[data-nw-state="streaming"]::after{
        content:'';position:absolute;z-index:7;pointer-events:none;
        left:0;right:0;top:0;height:5px;
        background:linear-gradient(100deg,transparent 0%,
          var(--nwEdge) 35%,#edfff7 50%,var(--nwEdge) 65%,transparent 100%);
        background-size:220% 100%;
        animation:nwPanelFlow 2.6s linear infinite}
      @keyframes nwSpin{to{transform:rotate(360deg)}}
      @keyframes nwHeartbeat{50%{box-shadow:0 0 0 6px #9bdcca24;opacity:.72}}
      @keyframes nwSendFloat{50%{transform:translateY(-3px)}}
      @keyframes nwPanelFlow{
        from{background-position:220% 0}
        to{background-position:-220% 0}}
      @keyframes nwPanelGlow{
        0%,100%{box-shadow:0 15px 45px #0008,0 0 12px var(--nwGlow)}
        50%{box-shadow:0 15px 45px #0008,0 0 34px var(--nwGlow)}}
      @media(prefers-reduced-motion:reduce){
        .nwIndicator::before,.nwPanel,.nwPanel::after{
          animation:none!important;transition:none!important}
        .nwPanel::after{background:var(--nwEdge)!important}
      }
      .nwOverview .nwIndicator{width:var(--nw-overview-icon,36px);
        height:var(--nw-overview-icon,36px)}
      .nwOverview .nwIndicator::before{transform-origin:center}
      .nwOverview .nwIndicator[data-state="sending"]::before,
      .nwOverview .nwIndicator[data-state="tools"]::before{
        font-size:var(--nw-overview-icon,36px)}
      .nwOverview .nwIndicator[data-state="error"]::before{
        width:var(--nw-overview-icon,36px);height:var(--nw-overview-icon,36px);
        line-height:var(--nw-overview-icon,36px);
        font-size:calc(var(--nw-overview-icon,36px)*.75)}
      .nwOverview .nwIndicator[data-state="thinking"]::before,
      .nwOverview .nwIndicator[data-state="loading"]::before{
        width:calc(var(--nw-overview-icon,36px)*.7);
        height:calc(var(--nw-overview-icon,36px)*.7)}
      .nwOverview .nwIndicator[data-state="streaming"]::before{
        width:calc(var(--nw-overview-icon,36px)*.55);
        height:calc(var(--nw-overview-icon,36px)*.55)}
      .nwOverview .nwUnreadDot{height:var(--nw-overview-icon,36px);
        width:var(--nw-overview-icon,36px)}

      .nwMessages{flex:1;overflow-y:auto;min-height:0;overscroll-behavior:contain;
        background:var(--nwBody);transition:background-color .3s ease;
        padding:13px 15px;margin-right:var(--nw-edge-hit,9px);user-select:text;cursor:text}
      .nwOverviewText{cursor:text}
      .nwMessage{padding-bottom:17px;white-space:pre-wrap;overflow-wrap:anywhere;font-size:15px;line-height:1.6;color:#e1ebea}
      /* Saved, pending and live messages share a narrow opposing gutter. */
      .nwMessage.user{width:max-content;max-width:calc(100% - clamp(20px,7%,56px));
        margin-left:auto;text-align:right;color:#e5f5f0;
        background:#253b37;border:1px solid #42675c;border-radius:14px 14px 4px 14px;
        padding:10px 13px 12px}
      .nwMessage.assistant{margin-right:clamp(20px,7%,56px);text-align:left}
      .nwMessage .who{display:block;font-weight:750;color:#91cdbb;font-size:12px;margin-bottom:5px}
      .nwMessage.user .who{color:#8fa2aa}
      .nwPending{border:1px dashed #497f70;border-radius:9px;padding:8px 11px;
        background:#1b302e}
      .nwPending.nwPendingAccepted{border-style:solid;border-color:#42675c;
        background:#253b37}
      .nwPendingState{font-size:12px;color:#a1c8bc;margin-top:5px}
      .nwPendingState[hidden]{display:none}
      .nwPendingState.warn{color:#efbf9a}
      .nwPendingRestore{margin-top:8px;border:1px solid #65998a;
        background:#27473f;color:#edfff7;border-radius:7px;padding:6px 10px;
        cursor:pointer}
      .nwMessage .nekoMarkdown{white-space:normal}
      .nwActivity{margin:5px 0 13px;border:1px solid #355355;border-radius:9px;
        background:#18292e;overflow:hidden;min-width:0}
      .nwActivity.tool_call{border-color:#54715f}
      .nwActivity.thinking{border-color:#4c536c;background:#202733}
      /* Public progress is real assistant prose, never a collapsible card. */
      .nwProgress{border:0;background:transparent;padding:0;
        color:#e1ebea;line-height:1.6;overflow-wrap:anywhere}
      .nwProgressBody{font-size:15px;line-height:1.6}
      .nwProgressBody.nekoMarkdown{white-space:normal}
      .nwActivity summary{cursor:pointer;padding:9px 10px;font-size:14px;
        color:#bfe6d9;font-weight:650;line-height:1.4;overflow-wrap:anywhere}
      .nwActivity.thinking summary{color:#bfcbed}
      .nwToolGroup{border-color:#4a746a;background:#192f2c}
      .nwToolGroup > summary{color:#bcf2dd;font-weight:750}
      .nwToolGroupBody{padding:0 8px 9px;display:flex;flex-direction:column;gap:5px}
      .nwToolGroupBody > .nwActivity{margin:0;background:#13252a}
      .nwToolGroupBody > .nwActivity > summary{font-size:13px;padding:7px 10px}
      .nwActivity .nwActivityBody{padding:0 11px 11px;font-size:14px;
        color:#d6e6e4;white-space:normal;overflow-wrap:anywhere}
      .nwActivity .nwActivityBody pre{max-height:310px;overflow:auto;
        font-size:12px;white-space:pre-wrap;overflow-wrap:anywhere}
      /* Top-level and live tool groups align with assistant replies. */
      .nwMessages > .nwActivity,.nwMessages > .nwActivities{
        margin-right:24px;
        margin-right:clamp(20px,7%,56px)}
      .nwActivityHint{font-size:12px;color:#94a9ae;margin-left:7px}
      /* Streaming output is an assistant message, never a green user bubble. */
      .nwLive{background:transparent;border:0;border-radius:0;padding:0;
        text-align:left;color:#e1ebea}
      .nwLiveBody{white-space:pre-wrap;overflow-wrap:anywhere;
        font-size:15px;line-height:1.6}
      .nwLiveBody.nekoMarkdown{white-space:normal}
      .nwLive[hidden]{display:none}
      /* Explanatory UI status, not a model-authored reply. */
      .nwAwaitingReply{font-size:12px;line-height:1.5;
        color:#9bb8b1;margin:2px 18px 12px 0}
      .nwAwaitingReply[hidden]{display:none}
      .nwComposer{flex:none;display:flex;flex-direction:column;gap:6px;
        border-top:1px solid var(--nwEdge);padding:9px;
        background:var(--nwComposerBg);
        transition:background-color .3s ease,border-color .3s ease}
      .nwAttachments{display:flex;align-items:stretch;gap:7px;flex-wrap:wrap}
      .nwAttachments[hidden]{display:none}
      .nwAttachment{display:flex;align-items:center;gap:8px;
        max-width:100%;background:#0c1b21aa;border:1px solid var(--nwEdge);
        border-radius:9px;padding:5px 7px;color:#deeee8}
      .nwAttachmentPreview{width:46px;height:46px;object-fit:cover;
        border-radius:6px;background:#14242a}
      .nwAttachmentIcon{font-size:22px;color:#a8d2c2}
      .nwAttachmentMeta{min-width:0;max-width:140px;display:flex;flex-direction:column}
      .nwAttachmentName{font-size:12px;overflow:hidden;text-overflow:ellipsis;
        white-space:nowrap}
      .nwAttachmentSize{font-size:11px;color:#a3b8b2}
      .nwAttachments button{background:#22362f;color:#e6f2ec;
        border:1px solid #42665b;border-radius:7px;cursor:pointer}
      .nwAttachmentError{font-size:12px;color:#ffbfaf}
      .nwAttachmentError[hidden]{display:none}
      .nwDraft{width:100%;min-height:58px;max-height:160px;resize:vertical;
        background:var(--nwDraftBg);border:1px solid var(--nwEdge);
        border-radius:9px;color:#eef4f3;padding:9px;font-size:16px;
        line-height:1.4;cursor:text;
        transition:background-color .3s ease,border-color .3s ease}
      .nwControls{display:flex;justify-content:flex-end;gap:7px}
      .nwControls button{padding:7px 13px;border-radius:8px;border:1px solid #405c55;background:#25483e;color:#e7fbef;font-size:14px;cursor:pointer}
      .nwControls button.nwSend{background:#b0e6d9;color:#123126;font-weight:700}
      .nwControls button:disabled{opacity:.42}
      .nwEmpty{font-size:17px;color:#9cb0b5;margin:auto;padding:30px;text-align:center}
      .workspacePin{border:1px solid #405f54;border-top:0;background:#1c3731;color:#b8f5d8;min-height:44px;border-radius:0 0 14px 14px;font-size:16px}
      .workspacePin:hover{background:#2b4b40}
    `;
    shadow.append(css);
    root=el('section');root.id='nekoWorkspace';root.hidden=true;
    toolbar=el('div','nwToolbar');
    toolbar.append(el('b','','Chat workspace'));
    const boardSelect=el('select','nwBoardSelect');
    boardSelect.setAttribute('aria-label','Choose canvas board');
    boardSelect.title='Switch between your canvas boards';
    boardSelect.addEventListener('change',()=>{
      resetDelete();
      post('panel-board-switch',{id:boardSelect.value});
    });
    const createBoard=el('button','','+ Board');
    createBoard.title='Create an empty canvas board';
    createBoard.addEventListener('click',()=>{
      resetDelete();
      post('panel-board-create');
    });
    const renameBoard=el('button','','Rename');
    renameBoard.title='Rename this canvas board';
    const renameInput=el('input','nwBoardRename');
    renameInput.maxLength=60;renameInput.hidden=true;
    renameInput.setAttribute('aria-label','Board name');
    const finishRename=()=>{
      if(renameInput.hidden)return;
      renameInput.hidden=true;
      renameBoard.hidden=false;
      const name=renameInput.value.trim();
      const currentName=(snapshot?.boards||[])
        .find(board=>board.id===activeBoardId)?.name||'';
      if(name && name!==currentName)
        post('panel-board-rename',{id:activeBoardId,name});
    };
    renameBoard.addEventListener('click',()=>{
      // Edit the underlying board name, never a presentation label.
      renameInput.value=(snapshot?.boards||[])
        .find(board=>board.id===activeBoardId)?.name||'Workspace';
      renameBoard.hidden=true;renameInput.hidden=false;
      renameInput.focus();renameInput.select();
    });
    renameInput.addEventListener('keydown',event=>{
      if(event.key==='Enter'){event.preventDefault();finishRename();}
      if(event.key==='Escape'){
        event.preventDefault();
        renameInput.hidden=true;renameBoard.hidden=false;
      }
    });
    renameInput.addEventListener('blur',finishRename);
    const deleteBoard=el('button','nwBoardDanger','Delete');
    deleteBoard.title='Remove this board only, not its conversations';
    const resetDelete=()=>{deleteBoard.textContent='Delete';deleteBoard.dataset.confirm='';};
    deleteBoard.addEventListener('click',()=>{
      if(deleteBoard.dataset.confirm===activeBoardId){
        resetDelete();post('panel-board-delete',{id:activeBoardId});
      }else{
        deleteBoard.dataset.confirm=activeBoardId;
        deleteBoard.textContent='Confirm delete';
      }
    });
    const count=el('span','nwCount','0 chats');
    const browse=el('button','','+ Add chats');
    browse.addEventListener('click',()=>showPicker());
    const newChat=el('button','nwPrimary','+ New chat');
    newChat.addEventListener('click',()=>showNewChat());
    canvas=window.__nekoCanvas.create(post);
    const zoomOut=el('button','','−');zoomOut.title='Zoom out';
    zoomOut.addEventListener('click',()=>canvas.zoomBy(1/1.25));
    const zoomIn=el('button','','+');zoomIn.title='Zoom in';
    zoomIn.addEventListener('click',()=>canvas.zoomBy(1.25));
    const zoomLabel=el('span','nwZoomLabel','100%');
    zoomLabel.style.minWidth='54px';
    zoomLabel.style.textAlign='center';
    let wasOverview=false,previousZoom=null;
    canvas.setZoomListener(value=>{
      zoomLabel.textContent=value+'%';
      const zoomChanged=previousZoom!==value;
      previousZoom=value;
      const overview=value<55;
      if(zoomChanged && mode && !overview){
        for(const view of views.values())
          if(view.followTail)followLatest(view);
      }
      if(overview&&!wasOverview){
        for(const view of views.values())
          view.overviewScrollTop=view.messages.scrollTop;
      }
      if(!overview&&wasOverview && mode){
        requestAnimationFrame(()=>{
          for(const view of views.values()){
            if(view.followTail)followLatest(view);
            else if(Number.isFinite(view.overviewScrollTop))
              view.messages.scrollTop=view.overviewScrollTop;
          }
        });
      }
      wasOverview=overview;
    });
    const fit=el('button','','Fit all');
    fit.title='Zoom out to show all chats';
    fit.addEventListener('click',()=>canvas.fitAll());
    const arrange=el('button','','Arrange');
    arrange.title='Reset panel positions into a tidy layout (keeps sizes)';
    arrange.addEventListener('click',()=>canvas.arrange());
    const diagnostics=el('button','','Logs');
    diagnostics.title='Open Neko diagnostic events (no message content)';
    diagnostics.setAttribute('aria-label','Open diagnostic logs');
    diagnostics.addEventListener('click',()=>post('diagnostics-open'));
    toolbar.append(boardSelect,createBoard,renameBoard,renameInput,deleteBoard,
      count,browse,newChat,zoomOut,zoomLabel,zoomIn,fit,arrange,diagnostics);
    const hint=el('div','nwCanvasHint',
      'Drag a title bar to move • drag edges to resize • scroll on the background to zoom • double-click a title to focus');
    hint.style.cssText='font-size:12px;color:#9bb9af;padding:5px 20px;background:#122027';
    grid=canvas.world;root.append(toolbar,hint,canvas.viewport);
    shadow.querySelector('main')?.append(root);
    mountDialogs();
    root._count=count;
    root._boardSelect=boardSelect;
    root._boardDelete=deleteBoard;
    root._renameInput=renameInput;
    root._renameButton=renameBoard;
    root._resetDelete=resetDelete;
    root._boardListKey='';
  }

  let pickerDialog,pickerSearch,pickerResults,newDialog,newDraft,newStatus,newSubmit,newReset;
  function mountDialogs(){
    pickerDialog=el('div','nwOverlay');pickerDialog.hidden=true;
    pickerDialog.setAttribute('role','presentation');
    const pane=el('section','nwDialog');
    pane.setAttribute('role','dialog');pane.setAttribute('aria-modal','true');
    pane.setAttribute('aria-label','Add conversations');
    const header=el('div','nwDialogHeader');
    header.append(el('strong','','Add conversations'));
    const close=el('button','','Close');close.addEventListener('click',hidePicker);
    header.append(close);
    pickerSearch=el('input');
    pickerSearch.type='search';pickerSearch.placeholder='Search your conversations';
    pickerSearch.setAttribute('aria-label','Search conversations');
    pickerSearch.addEventListener('input',renderPicker);
    pickerResults=el('div','nwPickerResults');
    const hint=el('div','nwDialogHint',
      'Adding a chat here does not remove it from another board.');
    const refresh=el('button','','Refresh list');
    refresh.addEventListener('click',()=>post('library-refresh'));
    const actions=el('div','nwOverlayActions');actions.append(refresh);
    pane.append(header,pickerSearch,hint,pickerResults,actions);
    pickerDialog.append(pane);
    pickerDialog.addEventListener('click',event=>{
      if(event.target===pickerDialog)hidePicker();
    });
    newDialog=el('div','nwOverlay');newDialog.hidden=true;
    const newPane=el('section','nwDialog');
    newPane.setAttribute('role','dialog');newPane.setAttribute('aria-modal','true');
    newPane.setAttribute('aria-label','New conversation');
    const newHeader=el('div','nwDialogHeader');
    newHeader.append(el('strong','','New conversation'));
    const cancel=el('button','','Close');
    cancel.addEventListener('click',hideNewChat);
    newHeader.append(cancel);
    newPane.append(newHeader,
      el('div','nwDialogHint',
        'Send the first message to create a conversation on this board.'));
    newDraft=el('textarea','nwNewMessage');
    newDraft.placeholder='What would you like to work on?';
    newDraft.maxLength=24000;
    newDraft.setAttribute('aria-label','First message');
    newDraft.addEventListener('keydown',event=>{
      if(event.key==='Enter'&&!event.shiftKey){
        event.preventDefault();submitNewChat();
      }
    });
    newDraft.addEventListener('input',updateNewChatButton);
    newStatus=el('div','nwDialogHint');
    newStatus.setAttribute('role','status');
    newSubmit=el('button','nwPrimary','Create and send');
    newSubmit.addEventListener('click',submitNewChat);
    newReset=el('button','','Start a different chat');
    newReset.hidden=true;
    newReset.addEventListener('click',()=>{
      if(snapshot?.creatingChat || !snapshot?.newChatUncertain)return;
      newDraft.value='';
      newStatus.textContent='Previous uncertain delivery will not be retried.';
      newReset.hidden=true;
      post('panel-new-chat-reset');
      updateNewChatButton();
    });
    const buttons=el('div','nwOverlayActions');
    buttons.append(newReset,newSubmit);
    newPane.append(newDraft,newStatus,buttons);
    newDialog.append(newPane);
    const sh=root.getRootNode();
    (sh.getElementById('shell')||sh.querySelector('main'))
      .append(pickerDialog,newDialog);
    sh.addEventListener('keydown',event=>{
      if(event.key!=='Escape')return;
      if(!pickerDialog.hidden){event.preventDefault();hidePicker();}
      else if(!newDialog.hidden&&!newChatBusy()){
        event.preventDefault();hideNewChat();
      }
    });
  }
  function showPicker(){
    newDialog.hidden=true;newChatOpen=false;
    pickerOpen=true;pickerDialog.hidden=false;
    pickerSearch.value='';
    renderPicker();pickerSearch.focus();
  }
  function hidePicker(){pickerOpen=false;pickerDialog.hidden=true;}
  function renderPicker(){
    if(!pickerOpen)return;
    const matches=new Map();
    for(const item of shellSnapshot?.library?.chats||[])
      if(item?.id)matches.set(item.id,item.title||'Untitled chat');
    for(const item of shellSnapshot?.project?.chats||[])
      if(item?.id&&!matches.has(item.id))
        matches.set(item.id,item.title||'Untitled chat');
    const filter=pickerSearch.value.trim().toLocaleLowerCase();
    const pinned=new Set((snapshot?.panels||[]).map(x=>x.id));
    pickerResults.replaceChildren();
    let shown=0;
    for(const [id,title] of matches){
      if(filter&&!title.toLocaleLowerCase().includes(filter)&&
         !id.toLowerCase().includes(filter))continue;
      if(shown++>=150)break;
      const row=el('div','nwPickRow');
      const label=el('span','nwPickTitle',title);label.title=title;
      const add=el('button','',pinned.has(id)?'Added':'+ Add');
      add.disabled=pinned.has(id);
      add.setAttribute('aria-label','Add '+title);
      add.addEventListener('click',()=>{
        add.disabled=true;add.textContent='Adding…';
        post('panel-add',{id});
      });
      row.append(label,add);pickerResults.append(row);
    }
    if(!shown)pickerResults.append(el('div','nwDialogHint',
      shellSnapshot?.library?.loading?'Loading conversations…':
      'No matching conversations. Try Refresh list.'));
  }
  function newChatBusy(){return !!snapshot?.creatingChat||newChatAwaiting;}
  function updateNewChatButton(){
    newSubmit.disabled=newChatBusy()||!!snapshot?.newChatUncertain||
      !newDraft.value.trim();
  }
  function showNewChat(){
    hidePicker();newChatOpen=true;newDialog.hidden=false;
    newStatus.textContent=snapshot?.newChatUncertain
      ?snapshot.newChatError||'Previous delivery was uncertain.'
      :'';
    newReset.hidden=!snapshot?.newChatUncertain;
    updateNewChatButton();newDraft.focus();
  }
  function hideNewChat(){
    if(newChatBusy())return;
    newChatOpen=false;newDialog.hidden=true;
  }
  function submitNewChat(){
    const text=newDraft.value.trim();
    if(!text||newChatBusy()||newSubmit.disabled)return;
    newChatAwaiting=true;
    newStatus.textContent='Creating conversation…';
    newSubmit.disabled=true;
    post('panel-new-chat',{text,boardId:activeBoardId});
  }
  function updateCanvasDialogs(){
    if(hotDialogState){
      const saved=hotDialogState;hotDialogState=null;
      if(typeof saved.newDraft==='string')newDraft.value=saved.newDraft;
      newChatAwaiting=!!saved.newChatAwaiting;
      if(saved.newChatOpen){
        newChatOpen=true;newDialog.hidden=false;
      }
      if(saved.pickerOpen){
        pickerOpen=true;pickerDialog.hidden=false;
        pickerSearch.value=saved.pickerSearch||'';
      }
    }
    if(pickerOpen)renderPicker();
    // Resolve server completion even if mode changes hid the modal.
    if(newChatAwaiting && snapshot?.createdChatId){
      newChatAwaiting=false;newDialog.hidden=true;newChatOpen=false;
      newDraft.value='';newStatus.textContent='';
    }else if(newChatAwaiting && snapshot?.newChatError && !snapshot?.creatingChat){
      newChatAwaiting=false;newStatus.textContent=snapshot.newChatError;
    }
    if(!newDialog.hidden){
      if(newChatAwaiting && snapshot?.createdChatId){
        newChatAwaiting=false;
        newDialog.hidden=true;newChatOpen=false;
        newDraft.value='';newStatus.textContent='';
      }else if(snapshot?.newChatError && !snapshot?.creatingChat){
        newChatAwaiting=false;
        newStatus.textContent=snapshot.newChatError;
      }else if(snapshot?.creatingChat || newChatAwaiting){
        newStatus.textContent='Creating conversation…';
      }
      newReset.hidden=!snapshot?.newChatUncertain;
      newSubmit.disabled=newChatBusy()||!!snapshot?.newChatUncertain||
        !newDraft.value.trim();
    }
  }

  function attentionFor(id){
    if(!chatAttention.has(id))
      chatAttention.set(id,{
        primed:false,unread:false,lastReplyId:null,
        lastCompletionId:null,wasWorking:false,error:''
      });
    return chatAttention.get(id);
  }
  function boardChatIds(id){
    const board=(snapshot?.boards||[]).find(b=>b.id===id);
    // Backend supplies all boards' chat IDs from local layout preferences.
    // Cached DOM views are only a fallback for older in-process snapshots.
    return board?.chatIds || (id===activeBoardId
      ?[...views.keys()]:[...(boardViews.get(id)?.keys()||[])]);
  }
  function updateBoardAttention(){
    if(!root?._boardSelect)return;
    for(const option of root._boardSelect.options){
      const board=(snapshot?.boards||[]).find(x=>x.id===option.value);
      const name=board?.name||option.value;
      const chats=boardChatIds(option.value);
      const warning=chats.some(id=>!!attentionFor(id).error);
      const unread=chats.some(id=>attentionFor(id).unread);
      option.textContent=warning?'⚠ '+name:unread?'● '+name:name;
      option.title=warning?name+' needs attention':
        unread?name+' has an unread reply':name;
      option.style.color=warning?'#f3bf9d':'';
    }
  }
  function updateUnreadDecorations(){
    for(const panels of [...boardViews.values(),views]){
      for(const [id,view] of panels){
        const unread=attentionFor(id).unread;
        view.unreadDot.hidden=!unread;
        view.panel.classList.toggle('nwHasUnread',unread);
      }
    }
    updateBoardAttention();
  }
  function markChatRead(id){
    const state=attentionFor(id);
    if(!state.unread)return;
    state.unread=false;
    updateUnreadDecorations();
  }
  function observeStoredSignals(signals){
    for(const signal of signals||[]){
      if(!signal?.id)continue;
      const state=attentionFor(signal.id);
      state.error=signal.error||'';
      if(!state.primed){
        // An already-complete reply when Neko first loads is not unread.
        state.primed=true;
        state.lastCompletionId=signal.completed && signal.messageId
          ?signal.messageId:null;
        state.wasWorking=!!signal.working;
      }else if(signal.messageId)markNewCompletion(signal.id,signal);
      else if(signal.working)state.wasWorking=true;
    }
    updateUnreadDecorations();
  }
  function backgroundSignals(changes){
    for(const [id,signal] of Object.entries(changes||{})){
      attentionFor(id).error=signal?.error||'';
      markNewCompletion(id,signal);
    }
    updateUnreadDecorations();
  }
  function markNewCompletion(id,update){
    if(!update?.messageId)return;
    const state=attentionFor(id);
    if(!state.primed){
      state.primed=true;
      state.lastCompletionId=update.completed?update.messageId:null;
      state.wasWorking=!update.completed;
      return;
    }
    if(update.completed){
      if(state.lastCompletionId!==update.messageId){
        // Only a completion observed after initial hydration becomes unread.
        // Do not show a badge merely because older history was just loaded.
        state.unread=true;
        state.lastCompletionId=update.messageId;
      }
      state.wasWorking=false;
    }else state.wasWorking=true;
  }
  function observeAttention(view,info){
    const id=view.panel.dataset.id;
    const state=attentionFor(id);
    state.error=info.historyError||
      (info.live?.error==='server_complete_final_pending'
        ?'':info.live?.error||'');
    const latest=[...(info.messages||[])].reverse()
      .find(m=>m.role==='assistant' && m.kind==='message' && m.id);
    if(!state.primed){
      state.primed=true;
      state.lastReplyId=latest?.id||null;
      state.lastCompletionId=info.live?.completed
        ?info.live.messageId:null;
      state.wasWorking=!!info.generating||!!info.sending;
    }else{
      if(latest?.id && state.lastReplyId &&
         latest.id!==state.lastReplyId)
        state.unread=true;
      if(latest?.id)state.lastReplyId=latest.id;
      if(info.live)markNewCompletion(id,info.live);
      else if(info.generating)state.wasWorking=true;
    }
    updateUnreadDecorations();
  }
  // Only stable backend reason codes are sent to diagnostics. Never forward
  // chat text, titles, stream content or raw service error messages.
  function issueCode(info,update,view){
    if(info.localCacheError)return 'local_cache_error';
    if(info.historyError)return 'history_error';
    if(info.stopFeedback && info.stopFeedback!=='stop_checking')
      return 'stop_issue';
    if(update?.error && update.error!=='server_complete_final_pending')
      return 'stream_error';
    if(info.uncertain)return 'send_uncertain';
    if(!info.authorized&&!info.historyLoading)return 'catalog_unavailable';
    if(info.phase==='send_failed'||info.phase==='blocked'||
       info.status==='bridge_unavailable')return 'send_failed';
    if(view.pending && ['failed','uncertain'].includes(view.pending.stage))
      return 'pending_message_failed';
    return '';
  }
  function describeIssue(info,update,view){
    if(info.localCacheError)
      return 'Local transcript backup could not be read or saved. The chat may not survive another restart until server history loads.';
    if(info.historyError)return info.historyError;
    const stopProblems={
      stop_read_unavailable:
        'Neko cannot verify whether this response ended. Send remains locked against duplicates. Retry verification when the connection recovers.',
      stop_read_throttled:
        'Stop verification is rate-limited. Neko has not confirmed that the response ended. Retry Stop after the cooldown.',
      stop_still_generating:
        'Stop was requested, but the server still reports this conversation as generating. Retry Stop or check again shortly.',
      stop_unavailable:
        'Neko could not complete Stop, and the server has not confirmed completion. Retry Stop or Refresh.',
      stop_delivery_unconfirmed:
        'The server says this response has ended, but the original message has not been verified in saved history. Send remains locked against duplicates. Retry Verify after the history cooldown.',
      verify_still_generating:
        'The server still reports this conversation as generating. Verification cannot release Send until the response ends.'
    };
    if(Object.hasOwn(stopProblems,info.stopFeedback))return stopProblems[info.stopFeedback];
    if(update?.error && update.error!=='server_complete_final_pending')
      return 'Response stream failed. Refresh this conversation to investigate.';
    if(info.uncertain)return 'Send status is uncertain. Check conversation history before trying again.';
    if(!info.authorized&&!info.historyLoading)
      return 'Waiting for signed-in chat catalog.';
    const reason=info.status||'';
    const reasons={
      other_workspace_generation_active:
        'Another conversation is generating. The shared-composer fallback cannot send in parallel. Restore your message and try after it finishes.',
      other_observed_stream_active:
        'A different response is still streaming. This fallback send must wait. Restore your message and try when it finishes.',
      shared_composer_busy:
        'Another conversation is using the shared composer. Restore your message and try again when it is free.',
      conversation_generating:
        'This conversation is already generating a response. Wait for it to finish before sending again.',
      composer_backend_unavailable:
        'The authenticated chat backend is not ready. Restore your message and try when it reconnects.',
      composer_route_not_ready:
        'The chat composer did not become ready. Restore your message and try again later.',
      model_selection_unavailable:
        'The site model selection is unavailable. Nothing was sent. Retry after the backend reconnects.',
      thinking_model_unavailable:
        'No supported reasoning model and effort are currently available in the signed-in model catalog. Nothing was sent. Retry after the model list loads.',
      maximum_effort_requires_native_transport:
        'The maximum-effort sender is unavailable for this message or attachment. Nothing was sent. Retry after Neko reconnects; your message and attachments are retained.',
      history_preflight_unavailable:
        'Could not verify chat history before sending. Restore your message and retry when history is available.',
      preflight_rate_limited:
        'History and status reads are temporarily rate limited. Nothing was sent. Restore your message and retry when that limit clears.',
      parent_unavailable:
        'Neko could not verify the next message parent. Nothing was sent. Restore your message and retry after the history connection recovers.',
      conversation_status_unavailable:
        'Neko could not confirm whether this chat is generating. Nothing was sent. Restore your message and retry after the connection recovers.',
      previous_submission_uncertain:
        'A previous submission may have succeeded. Verify history before sending again.',
      bridge_unavailable:
        'The chat sending bridge is unavailable. Restore your message and try after reconnecting.'
    };
    if(info.phase==='send_failed'||info.phase==='blocked'||
       reason==='bridge_unavailable'){
      if(reason.startsWith('attachment_stage_resolve_input_') ||
         reason==='attachment_file_input_not_found' ||
         reason==='attachment_file_input_probe_failed')
        return 'Neko could not locate the website file input. Nothing was sent. The screenshot is still attached.';
      if(reason.startsWith('attachment_stage_set_input_'))
        return 'Neko could not attach the file to the signed-in composer. Nothing was sent. The screenshot is still attached.';
      if(reason==='attachment_attachment_preview_timeout')
        return 'The upload was not confirmed. Nothing was sent. The screenshot is still attached.';
      if(reason.startsWith('attachment_'))
        return 'Attachment preparation failed ('+reason.replace(/^attachment_/,'')+
          '). Nothing was sent. Restore the caption and keep the file selected.';
      return (Object.hasOwn(reasons,reason)?reasons[reason]:null) ||
        'Message was not sent. Restore it to the composer and try again when the connection is ready.';
    }
    if(view.pending && ['failed','uncertain'].includes(view.pending.stage))
      return 'Last send needs attention. See the outgoing message below.';
    return '';
  }
  function showChatState(view,info,liveUpdate=null){
    const update=liveUpdate||info.live;
    const working=!(liveUpdate?.completed) && (!!info.generating ||
      !!(update && !update.completed && !update.error &&
        (update.text||update.activities?.length)));
    const activity=update?.activities?.at(-1);
    const activeTool=activity &&
      (activity.kind==='tool_call'||activity.kind==='tool_result') &&
      (activity.status==='in_progress'||activity.status==='running');
    const issue=describeIssue(info,update,view);
    const code=issueCode(info,update,view);
    const reason=typeof info.status==='string' &&
      /^[a-z_]{3,70}$/.test(info.status)?info.status:'';
    const diagnosticKey=code+':'+reason;
    if(code && diagnosticKey!==view.lastDiagnosticKey)
      post('ui-diagnostic',{code:'warning_visible',id:view.panel.dataset.id,
        detail:reason||code});
    view.lastDiagnosticKey=code?diagnosticKey:'';
    const state=issue?'error':
      (liveUpdate?.completed?false:(info.sending||info.stopPending))?'sending':
      working?(activeTool?'tools':
        !update?.text?'thinking':'streaming'):
      info.historyLoading?'loading':'idle';
    const descriptions={
      idle:'Ready',sending:'Sending message',thinking:'Neko is thinking',
      tools:'Using tools',streaming:'Neko is replying',
      loading:'Loading conversation',error:'Needs attention'
    };
    view.indicator.dataset.state=state;
    view.indicator.hidden=state==='idle';
    const preflightWaiting=!issue && !!info.sending &&
      (info.readRetryAfterSeconds||0)>0;
    const waitMessage=preflightWaiting
      ?'Waiting for read cooldown ('+info.readRetryAfterSeconds+
       's). Nothing has been submitted yet.':'';
    const stopWaiting=!issue && info.stopFeedback==='stop_checking';
    const descriptor=issue||waitMessage||(stopWaiting?'Stop requested. Verifying server completion.':'')||descriptions[state];
    view.indicator.title=descriptor;
    view.indicator.setAttribute('aria-label',descriptor);
    view.panel.dataset.nwState=state;
    view.status.hidden=!issue&&!preflightWaiting&&!stopWaiting;
    view.status.dataset.mode=(preflightWaiting||stopWaiting)?'waiting':'error';
    const banner=issue||waitMessage||(stopWaiting?'Stop requested. Verifying server completion.':'');
    if(view.status.textContent!==banner)view.status.textContent=banner;
    view.unreadDot.hidden=!attentionFor(view.panel.dataset.id).unread;
  }

  function addPanel(id){
    const panel=el('article','nwPanel');panel.dataset.id=id;
    const head=el('div','nwPanelHead');
    const title=el('span','nwPanelTitle','Conversation');title.title=id;
    const grip=el('span','nwDragGrip','⠿');grip.setAttribute('aria-hidden','true');
    head.append(grip,title);
    const indicator=el('span','nwIndicator');
    indicator.hidden=true;
    indicator.setAttribute('role','img');
    const unreadDot=el('span','nwUnreadDot');
    unreadDot.hidden=true;
    unreadDot.title='New reply: open this chat to mark it read';
    unreadDot.setAttribute('role','img');
    unreadDot.setAttribute('aria-label','Unread reply');
    head.append(indicator,unreadDot);
    const control=(symbol,label,action,extra={})=>{
      const button=el('button','',symbol);button.title=label;
      button.setAttribute('aria-label',label);
      button.addEventListener('click',()=>post(action,{id,...extra}));
      head.append(button);return button;
    };
    const focusButton=el('button','','⤢');
    focusButton.title='Zoom in to read this chat';
    focusButton.setAttribute('aria-label','Zoom in to this chat');
    focusButton.addEventListener('click',()=>{
      canvas.focus(id);
      markChatRead(id);
    });
    head.append(focusButton);
    const renameChat=el('button','','✎');
    renameChat.title='Rename this conversation';
    renameChat.setAttribute('aria-label','Rename this conversation');
    renameChat.addEventListener('click',()=>{
      const current=views.get(id)?.lastInfo?.title||title.textContent||'';
      const proposed=window.prompt('Rename conversation',current);
      if(proposed===null)return;
      const next=proposed.trim();
      if(!next||next.length>180||next===current)return;
      post('rename-chat',{id,title:next});
    });
    head.append(renameChat);
    control('↻','Refresh conversation history','panel-refresh-history');
    control('×','Remove from workspace (does not stop generation)','panel-remove');
    const status=el('div','nwStatus');
    status.hidden=true;
    status.setAttribute('role','alert');
    const overview=el('div','nwOverviewText','Reading latest activity…');
    const messages=el('div','nwMessages');messages.setAttribute('role','log');
    const activityHost=el('div','nwActivities');
    const localEcho=el('div','nwMessage user nwPending');
    localEcho.hidden=true;
    const echoContent=el('div','');
    const echoState=el('div','nwPendingState');
    const restore=el('button','nwPendingRestore','Restore to composer');
    restore.hidden=true;
    localEcho.append(el('span','who','You'),echoContent,echoState,restore);
    restore.addEventListener('click',()=>{
      post('ui-diagnostic',{code:'control_pressed',id,detail:'restore'});
      const view=views.get(id);
      if(!view?.pending)return;
      if(view.draft.value) {
        view.draft.value+=(view.draft.value.endsWith('\n')?'':'\n')+view.pending.text;
      } else view.draft.value=view.pending.text;
      post('panel-draft',{id,text:view.draft.value});
      view.draft.focus();
      view.pending=null;
      view.suppressedDraft=null;
      localEcho.remove();
      view.send.disabled=!!view.busy||
        (!view.draft.value.trim()&&!view.attached?.length);
    });
    const live=el('div','nwMessage assistant nwLive');live.hidden=true;
    const liveBody=el('div','nwLiveBody');
    live.append(el('span','who','Neko'),liveBody);
    const awaitingReply=el('div','nwAwaitingReply');
    awaitingReply.hidden=true;
    awaitingReply.setAttribute('role','status');
    messages.append(localEcho,activityHost,awaitingReply,live);
    const composer=el('div','nwComposer');
    const attachmentList=el('div','nwAttachments');attachmentList.hidden=true;
    const attachmentError=el('div','nwAttachmentError');attachmentError.hidden=true;
    attachmentError.setAttribute('role','status');
    const draft=el('textarea','nwDraft');draft.placeholder='Message this conversation…';
    draft.maxLength=24000;draft.rows=2;
    const controls=el('div','nwControls');
    const stop=el('button','','Stop');stop.hidden=true;
    stop.addEventListener('click',()=>post(
      views.get(id)?.verifyOnly?'panel-verify-send':'panel-stop',{id}));
    const send=el('button','nwSend','Send');
    // Capture only which control was attempted and whether it was disabled.
    // No draft text, keyboard contents, URLs or clipboard contents are logged.
    const controlAttempt=button=>{
      const name=button===send?'send':
        views.get(id)?.verifyOnly?'verify':'stop';
      post('ui-diagnostic',{code:'control_pressed',id,
        detail:name+'_'+(button.disabled?'disabled':'enabled')});
    };
    controls.addEventListener('pointerdown',event=>{
      if(event.target===send||event.target===stop)
        controlAttempt(event.target);
    },true);
    controls.addEventListener('keydown',event=>{
      if((event.key==='Enter'||event.key===' ') &&
         (event.target===send||event.target===stop))
        controlAttempt(event.target);
    },true);
    const submit=()=>{
      if(send.disabled||(!draft.value.trim()&&!views.get(id)?.attached?.length))return;
      markChatRead(id);
      const view=views.get(id);
      const text=draft.value;
      if(view){
        view.pending=window.__nekoOutgoing.create(id,text,view.lastHistory||[],
          (view.attached||[]).map(item=>item.name));
        // A previous answer may still be visible only as a live bubble.
        // This outgoing user message belongs after that older answer.
        view.pending.priorLiveId=view.currentLiveId||'';
        view.suppressedDraft=text;
        draft.value='';
        drawPending(view);
        view.followTail=true;
        followLatest(view);
      }
      send.disabled=true;
      post('panel-send',{id,text});
    };
    send.addEventListener('click',submit);
    const updateSendAvailability=()=>{
      const current=views.get(id);
      send.disabled=(!draft.value.trim()&&!current?.attached?.length) ||
        !!current?.busy || !!current?.pending;
    };
    draft.addEventListener('input',()=>{
      updateSendAvailability();
      post('panel-draft',{id,text:draft.value});
    });
    // WebView2 can restore/edit a textarea without firing an input event
    // after a hot reload. Re-evaluate on focus and keyboard activity too.
    draft.addEventListener('focus',updateSendAvailability);
    draft.addEventListener('keyup',updateSendAvailability);
    draft.addEventListener('change',updateSendAvailability);
    draft.addEventListener('keydown',event=>{
      if(event.key==='Enter'&&!event.shiftKey){
        post('ui-diagnostic',{code:'control_pressed',id,
          detail:'send_'+(send.disabled?'disabled':'enabled')});
        event.preventDefault();submit();
      }
    });
    draft.addEventListener('paste',()=>{
      // Keep the browser's native text paste. WPF separately stages copied
      // images or Explorer files, but ignores a text-only clipboard.
      // No preventDefault() here, or Ctrl+V text would stop working.
      post('panel-attachment-paste',{id});
    });
    controls.append(stop,send);
    composer.append(attachmentList,attachmentError,draft,controls);
    panel.append(head,status,overview,messages,composer);
    const view={panel,head,title,status,indicator,unreadDot,
      overview,messages,localEcho,
      echoContent,echoState,restore,activityHost,activityViews:new Map(),
      live,liveBody,awaitingReply,draft,send,stop,
      attachmentList,attachmentError,attached:[],
      rev:-1,busy:false,lastLive:'',
      historyGroups:new Map(),liveGroups:new Map(),
      followTail:true,initialTail:true,tailQueued:false,autoScrolling:false,
      pointerScrolling:false,userScrollIntentUntil:0,
      userScrollAway:false,
      lastScrollHeight:messages.scrollHeight,
      lastClientHeight:messages.clientHeight,
      pending:null,suppressedDraft:null,lastHistory:[]};
    messages.addEventListener('scroll',()=>{
      const reflowed=view.lastScrollHeight!==messages.scrollHeight ||
        view.lastClientHeight!==messages.clientHeight;
      view.lastScrollHeight=messages.scrollHeight;
      view.lastClientHeight=messages.clientHeight;
      if(view.autoScrolling)return;
      // A resize that follows a wheel event is still a resize. Do not
      // interpret the browser's geometry-induced scroll as user intent.
      if(reflowed && view.followTail && !view.pointerScrolling){
        followLatest(view);return;
      }
      if(userScrollIsActive(view)){
        // Scrollbar dragging, wheel, touch and keyboard navigation are the
        // ONLY ways a native scroll event can change the follow preference.
        view.followTail=!view.userScrollAway && nearTail(messages);
        if(view.followTail){markChatRead(id);followLatest(view);}
      }else if(view.followTail){
        // Resizing, zooming, reflow and history hydration can scroll a
        // container without any user input. Restore its tail anchor.
        followLatest(view);
      }
    });
    messages.addEventListener('pointerdown',event=>{
      markChatRead(id);
      // The scrollbar itself belongs to this element, not to its children.
      // Clicking prose or a tool disclosure is NOT an intent to scroll.
      const edge=messages.getBoundingClientRect().right-event.clientX;
      if(event.button===1 || event.target===messages && edge>=0 && edge<=22){
        view.pointerScrolling=true;
        view.userScrollAway=false;
        rememberUserScroll(view,700);
      }
    });
    const finishScrollbarDrag=()=>{
      if(!view.pointerScrolling)return;
      view.pointerScrolling=false;
      rememberUserScroll(view,450);
    };
    messages.addEventListener('pointerup',finishScrollbarDrag);
    messages.addEventListener('pointercancel',finishScrollbarDrag);
    window.addEventListener('pointerup',finishScrollbarDrag);
    window.addEventListener('pointercancel',finishScrollbarDrag);
    messages.addEventListener('touchmove',()=>rememberUserScroll(view,850),
      {passive:true});
    panel.addEventListener('dblclick',()=>markChatRead(id));
    messages.addEventListener('keydown',event=>{
      markChatRead(id);
      if(event.target.closest('input,textarea,[contenteditable=true]'))return;
      if(['ArrowUp','PageUp','Home'].includes(event.key)){
        rememberUserScroll(view);
        view.followTail=false;
        view.userScrollAway=true;
      }else if(['ArrowDown','PageDown','End',' '].includes(event.key)){
        view.userScrollAway=false;
        rememberUserScroll(view);
      }
    });
    messages.addEventListener('wheel',event=>{
      if(event.ctrlKey||event.altKey||nestedWheelScroller(event,messages))
        return;
      rememberUserScroll(view);
      if(event.deltaY<0 && messages.scrollHeight>messages.clientHeight){
        view.followTail=false;
        view.userScrollAway=true;
      }else if(event.deltaY>0){
        view.userScrollAway=false;
        if(nearTail(messages)){
          view.followTail=true;
          followLatest(view);
        }
      }
    },{passive:true});
    // Track changes that do not generate scroll events: panel resizing,
    // zoom/reflow, streamed Markdown, expanding tool groups and images.
    const resizeObserver=typeof ResizeObserver==='function'?
      new ResizeObserver(()=>followLatest(view)):null;
    for(const target of [panel,messages,activityHost,liveBody,localEcho])
      resizeObserver?.observe(target);
    view.resizeObserver=resizeObserver;
    const mutationObserver=typeof MutationObserver==='function'?
      new MutationObserver(()=>followLatest(view)):null;
    mutationObserver?.observe(messages,{subtree:true,childList:true,
      characterData:true,attributes:true,attributeFilter:['hidden']});
    view.mutationObserver=mutationObserver;
    messages.addEventListener('load',()=>followLatest(view),true);
    view.dispose=()=>{
      resizeObserver?.disconnect();
      mutationObserver?.disconnect();
      window.removeEventListener('pointerup',finishScrollbarDrag);
      window.removeEventListener('pointercancel',finishScrollbarDrag);
    };
    const boardHotPanels=hotPanels.get(activeBoardId);
    const recovered=boardHotPanels&&Object.hasOwn(boardHotPanels,id)
      ?boardHotPanels[id]:null;
    if(recovered){
      delete boardHotPanels[id];
      if(!Object.keys(boardHotPanels).length)hotPanels.delete(activeBoardId);
    }
    if(recovered){
      if(typeof recovered.draft==='string'){
        draft.value=recovered.draft;
        view.restoredDraft=recovered.draft;
        // Synchronise only the local session buffer, not the network.
        post('panel-draft',{id,text:recovered.draft});
      }
      view.followTail=recovered.followTail!==false;
      view.restoredScrollTop=Number.isFinite(recovered.scroll)
        ?recovered.scroll:null;
      if(recovered.pending && typeof recovered.pending.text==='string'){
        view.pending={...recovered.pending,
          seenIds:new Set(recovered.pending.seenIds||[])};
        view.suppressedDraft=recovered.suppressedDraft??view.pending.text;
      }
    }
    views.set(id,view);
    canvas.bindPanel(view);
    return view;
  }
  function renderPanelAttachments(view,info){
    const items=Array.isArray(info.attachments)?info.attachments:[];
    const signature=items.map(x=>x.key).join('|')+
      '|'+!!info.attachmentSending+'|'+(info.attachmentError||'');
    view.attached=items;
    if(view.attachmentSignature===signature)return;
    view.attachmentSignature=signature;
    view.attachmentList.replaceChildren();
    view.attachmentList.hidden=items.length===0;
    for(const item of items){
      const chip=el('div','nwAttachment');
      if(typeof item.image==='string' && item.image.startsWith('data:image/')){
        const preview=el('img','nwAttachmentPreview');
        preview.src=item.image;preview.alt='Screenshot preview';
        chip.append(preview);
      }else chip.append(el('span','nwAttachmentIcon','📎'));
      const details=el('div','nwAttachmentMeta');
      const name=el('span','nwAttachmentName',item.name||'Attachment');
      name.title=item.name||'Attachment';
      const size=el('span','nwAttachmentSize',
        Math.round((item.size||0)/1024)+' KB');
      details.append(name,size);chip.append(details);
      const remove=el('button','','×');
      remove.title='Remove '+(item.name||'attachment');
      remove.setAttribute('aria-label',remove.title);
      remove.disabled=!!info.attachmentSending||!!view.pending;
      remove.addEventListener('click',()=>{
        post('panel-attachment-remove',{id:view.panel.dataset.id,key:item.key});
      });
      chip.append(remove);
      view.attachmentList.append(chip);
    }
    view.attachmentError.textContent=info.attachmentError||'';
    view.attachmentError.hidden=!info.attachmentError;
  }

  function placeLiveActivity(view,update){
    // Saved final answers can appear before their live SSE activity groups
    // when the journal commits the final message. Anchor each live activity
    // sequence immediately BEFORE the final message it belongs to, never
    // blindly at the end of all saved messages. Once a newer SSE begins,
    // relocate the same host to the current live tail.
    const associatedFinal=view.savedAssistantById?.get(update?.messageId);
    const anchor=associatedFinal||view.live;
    if(anchor?.parentNode===view.messages &&
       view.activityHost.nextSibling!==anchor)
      view.messages.insertBefore(view.activityHost,anchor);
  }
  function drawPending(view){
    if(!view.pending){
      view.localEcho.hidden=true;
      view.localEcho.remove();
      return;
    }
    view.localEcho.hidden=false;
    const pending=view.pending;
    const pendingDisplayKey=pending.text+'|'+(pending.attachmentNames||[]).join('|');
    if(view.echoContent.dataset.text!==pendingDisplayKey){
      view.echoContent.dataset.text=pendingDisplayKey;
      const caption=pending.text||'';
      const attachmentNames=(pending.attachmentNames||[]).map(name=>'📎 '+name);
      showMarkdown(view.echoContent,
        [caption,...attachmentNames].filter(Boolean).join('\n'));
    }
    // Saved-history hydration is background bookkeeping. Once the send is
    // accepted, keep the user message visible without a lingering status.
    const deliveryLabel=window.__nekoOutgoing.labels[pending.stage]||'';
    view.echoState.textContent=deliveryLabel;
    view.echoState.hidden=!deliveryLabel;
    view.localEcho.classList.toggle('nwPendingAccepted',
      pending.stage==='syncing');
    view.echoState.classList.toggle('warn',
      pending.stage==='uncertain'||pending.stage==='failed');
    view.restore.hidden=pending.stage!=='failed';
    // The current assistant bubble can be an older, completed live reply
    // that has not yet reached saved history. Never put a failed or newly
    // queued message above it. Once a NEW response starts, the user bubble
    // belongs before that new live reply instead.
    const sameOldLive=!!pending.priorLiveId &&
      pending.priorLiveId===view.currentLiveId && !view.live.hidden;
    const priorFinalIsSaved=!!view.savedAssistantById?.has(
      pending.priorLiveId);
    if(pending.stage==='failed' || sameOldLive)
      view.messages.append(view.localEcho);
    else if(priorFinalIsSaved &&
            pending.priorLiveId===view.currentLiveId)
      view.messages.insertBefore(view.localEcho,view.live);
    else view.messages.insertBefore(view.localEcho,view.activityHost);
  }
  function showMarkdown(element,text){
    if(window.__nekoMarkdown?.ready)window.__nekoMarkdown.render(element,text||'');
    else element.textContent=text||'';
  }
  function activityNode(activity,prior=null){
    const kind=activity.kind||'tool_activity';
    if(kind==='progress'){
      // Public commentary is ordinary assistant text, not an optional
      // disclosure. Render identically in live SSE and saved history.
      const existing=prior?.details?.classList?.contains('nwProgress')
        ?prior.details:null;
      const block=existing||el('div','nwMessage assistant nwProgress');
      const body=existing?prior.body:el('div','nwProgressBody');
      if(!existing)block.append(el('span','who','Neko'),body);
      const text=activity.text||'';
      if(body.dataset.original!==text){
        body.dataset.original=text;
        showMarkdown(body,text);
      }
      return {details:block,summary:null,body};
    }
    const details=prior?.details||el('details','nwActivity '+kind);
    details.className='nwActivity '+kind;
    const summary=prior?.summary||el('summary');
    const body=prior?.body||el('div','nwActivityBody');
    if(!prior)details.append(summary,body);
    const label=activity.title||(
      kind==='thinking'?'Thinking':kind==='tool_call'?'Tool invocation':
      kind==='tool_result'?'Tool output':'Activity');
    summary.textContent=(kind==='thinking'?'◈ ':kind==='tool_call'?'⌘ ':'▣ ')+label;
    if(activity.status==='in_progress'){
      summary.append(el('span','nwActivityHint',' · running'));
    }
    const text=activity.text||(
      kind==='thinking'?'Thinking details not available.':
      'No text output was provided.');
    if(body.dataset.original!==text){
      body.dataset.original=text;
      body.replaceChildren();
      // Tool inputs are often JSON. Keep their formatting and never execute it.
      if(kind!=='thinking' && /^[\s]*[\{\[]/.test(text)){
        const code=el('pre','',text);
        body.append(code);
      }else showMarkdown(body,text);
    }
    return {details,summary,body};
  }
  function applyActivities(view,activities){
    const keep=new Set();
    const children=[];
    const toolGroups=window.__nekoToolGroups;
    const incoming=(activities||[]).slice(-48).filter(item=>
      !view.historyMessageIds?.has(item.id));
    for(const block of toolGroups.sequences(incoming)){
      const entries=block.type==='tools'?block.items:[block.item];
      const nodes=entries.map(item=>{
        keep.add(item.id);
        const node=activityNode(item,view.activityViews.get(item.id));
        view.activityViews.set(item.id,node);
        return node.details;
      });
      if(block.type==='tools'){
        children.push(toolGroups.disclosure(document,entries,
          (_,index)=>nodes[index],{
            cache:view.liveGroups,wrapperClass:'nwActivity nwToolGroup',
            contentClass:'nwToolGroupBody'
          }));
      }else children.push(nodes[0]);
    }
    view.activityHost.replaceChildren(...children);
    for(const [id,node] of view.activityViews)
      if(!keep.has(id)){node.details.remove();view.activityViews.delete(id);}
    for(const key of view.liveGroups.keys())
      if(!keep.has(key))view.liveGroups.delete(key);
  }
  function updateAwaitingReply(view,info,update){
    // Distinguish tool activity from a public final reply. This is a
    // neutral UI status, never model-authored assistant text.
    const history=view.lastHistory||[];
    const userIndex=history.findLastIndex(m=>m.role==='user');
    const currentTurn=history.slice(userIndex+1);
    const isTool=item=>['tool_call','tool_result','tool_activity'].includes(item.kind);
    const toolsSeen=currentTurn.some(isTool) ||
      (update?.activities||[]).some(isTool);
    const savedAnswer=currentTurn.some(m=>m.role==='assistant' &&
      m.kind==='message' && m.text?.trim() &&
      (!m.channel||m.channel==='final'));
    const liveAnswer=!!(update?.text?.trim() ||
      (view.lastNonEmptyLive?.id===update?.messageId &&
       view.lastNonEmptyLive?.text?.trim()));
    view.awaitingReply.hidden=!toolsSeen || !!savedAnswer || liveAnswer;
    if(!view.awaitingReply.hidden)
      view.awaitingReply.textContent=info?.generating?
        'Tools are running. No final assistant reply has arrived yet.':
        'Tool activity is saved, but no final assistant reply is available yet.';
  }
  function renderHistory(view,info){
    if(view.rev===info.historyRevision)return;
    view.rev=info.historyRevision;
    const messages=view.messages;
    const history=info.messages||[];
    view.lastHistory=history;
    if(view.pending){
      const outcome=window.__nekoOutgoing.reconcile(view.pending,history,info);
      if(outcome.event==='confirmed')view.pending=null;
    }
    if(view.suppressedDraft!==null && !view.pending && info.draft==='')
      view.suppressedDraft=null;
    const hasHistory=history.length>0;
    const savedScroll=messages.scrollTop;
    const fragments=document.createDocumentFragment();
    const toolGroups=window.__nekoToolGroups;
    const savedAssistantById=new Map();
    // Server history has already been ordered authoritatively. Never infer
    // the position of an unsaved user bubble from an assistant message ID:
    // after hot reload the saved baseline may be missing older replies.
    // Keep this local-only bubble at the tail until its server ID is confirmed.
    for(const block of toolGroups.sequences(history)){
      if(block.type==='tools'){
        fragments.append(toolGroups.disclosure(document,block.items,
          item=>activityNode(item).details,{
            cache:view.historyGroups,wrapperClass:'nwActivity nwToolGroup',
            contentClass:'nwToolGroupBody'
          }));
        continue;
      }
      const item=block.item;
      const kind=item.kind||'message';
      // The accepted local user entry is already visible as the pending
      // outgoing bubble. Avoid showing it twice until the response finishes.
      if(kind==='local_user' && view.pending?.text===item.text)continue;
      if(kind==='thinking'||kind==='media'||kind==='progress'){
        // Progress is a plain, always-visible Neko message; only private
        // thinking/media and tool details remain disclosure controls.
        fragments.append(activityNode(item).details);
        continue;
      }
      if(!item.text?.trim())continue;
      const box=el('div','nwMessage '+(item.role==='user'?'user':'assistant'));
      const who=el('span','who',item.role==='user'?'You':'Neko');
      const content=el('div','');
      showMarkdown(content,item.text);
      box.append(who,content);fragments.append(box);
      if(item.role==='assistant' && kind==='message' && item.id)
        savedAssistantById.set(item.id,box);
    }
    messages.replaceChildren(fragments,view.activityHost,view.awaitingReply,view.live);
    view.savedAssistantById=savedAssistantById;
    placeLiveActivity(view,info.live);
    drawPending(view);
    view.historyMessageIds=new Set(history.map(m=>m.id));
    // An ID appearing in history is NOT proof that the saved final reply is
    // rendered. A pending/empty server record can share the live message ID
    // while carrying no visible text. Never hide streaming output for that.
    if(info.live?.completed && view.savedAssistantById.has(info.live.messageId))
      view.live.hidden=true;
    applyActivities(view,info.live?.activities||[]);
    updateAwaitingReply(view,info,info.live);
    if(view.initialTail&&hasHistory)view.initialTail=false;
    if(!view.followTail)messages.scrollTop=savedScroll;
    else if(hasHistory)followLatest(view);
  }
  function applyLive(view,update){
    if(!update)return;
    view.currentLiveId=update.messageId||'';
    placeLiveActivity(view,update);
    if(update.text?.trim())
      view.overview.textContent=update.text.replace(/[#*`>_\[\]]/g,'').slice(-300);
    else if(update.activities?.length && !update.completed){
      const last=update.activities.at(-1);
      view.overview.textContent=(last.title||'Working')+
        (last.status==='in_progress'?' · running':'');
    }
    applyActivities(view,update.activities||[]);
    // A terminal SSE snapshot sometimes arrives with empty text after a
    // nonempty snapshot for the SAME public message. Keep the previous
    // displayed text until a visible saved final replaces it. This is only
    // a visual continuity fallback, never evidence of server completion.
    if(update.messageId && update.text?.trim())
      view.lastNonEmptyLive={id:update.messageId,text:update.text};
    const hasSavedReply=!!(update.completed &&
      view.savedAssistantById?.has(update.messageId));
    const priorText=!update.text?.trim() && !update.error &&
      view.lastNonEmptyLive?.id===update.messageId &&
      !hasSavedReply ?view.lastNonEmptyLive.text:'';
    // Server-verified Stop completion is not an assistant answer. Keep
    // any existing public text, but never print its internal reason code.
    const visibleError=update.error==='server_complete_final_pending'
      ?'':update.error;
    const shownText=update.text||priorText||visibleError||'';
    const key=update.messageId+'|'+shownText+'|'+update.completed+'|'+update.error+
      '|'+hasSavedReply;
    updateAwaitingReply(view,view.lastInfo||{},update);
    if(view.lastLive===key)return;
    view.lastLive=key;
    showMarkdown(view.liveBody,shownText);
    view.live.hidden=hasSavedReply || !shownText;
    if(view.pending)drawPending(view);
    if(view.initialTail&&(update.text||update.activities?.length))
      view.initialTail=false;
    if(view.followTail&&(update.text||update.error||update.activities?.length))
      followLatest(view);
    // Stop/Verify visibility belongs to the authoritative session snapshot,
    // not an SSE frame. A completed/partial frame can arrive before local
    // generating state is reconciled; hiding Stop here deadlocks the UI
    // while Send remains disabled.
    // The next panel snapshot updates Stop and Send together.
  }
  function receive(next){
    snapshot=next.panelWorkspace||{};
    mount();if(!root)return;
    shellSnapshot=next;
    const nextBoardId=snapshot.activeBoardId||'legacy';
    const ids=(snapshot.panels||[]).map(p=>p.id);
    const hotCanvas=hotCameras.get(nextBoardId);
    if(hotCanvas){
      if(hotCanvas.camera?.zoom)
        snapshot.camera={...hotCanvas.camera,saved:true};
      for(const panel of snapshot.panels||[])
        if(hotCanvas.rects?.[panel.id])
          panel.rect=hotCanvas.rects[panel.id];
    }
    if(activeBoardId!==nextBoardId){
      if(activeBoardId!==null){
        for(const view of views.values())
          view.savedScrollTop=view.messages.scrollTop;
        boardViews.set(activeBoardId,views);
      }
      views=boardViews.get(nextBoardId)||new Map();
      activeBoardId=nextBoardId;
      lastIds='';
      canvas.switchBoard(nextBoardId);
      grid.replaceChildren();
      root._resetDelete();
      root._renameInput.hidden=true;
      root._renameButton.hidden=false;
    }
    const boards=snapshot.boards?.length?snapshot.boards:[
      {id:nextBoardId,name:'Workspace 1',count:ids.length}
    ];
    for(const cachedId of boardViews.keys())
      if(!boards.some(board=>board.id===cachedId)){
        for(const view of boardViews.get(cachedId).values())
          view.dispose?.();
        boardViews.delete(cachedId);
      }
    const listKey=boards.map(b=>b.id+'|'+b.name).join('~');
    if(listKey!==root._boardListKey){
      const previous=root._boardSelect.value;
      root._boardSelect.replaceChildren(...boards.map(board=>{
        const option=el('option','',board.name);
        option.value=board.id;return option;
      }));
      root._boardListKey=listKey;
    }
    root._boardSelect.value=nextBoardId;
    observeStoredSignals(snapshot.signals);
    root._boardDelete.disabled=boards.length<=1;
    root._boardSelect.disabled=boards.length<=1;
    const wasVisible=mode;
    mode=!!snapshot.visible;
    if(mode&&!wasVisible){
      for(const view of views.values()){
        view.followTail=true;
        view.initialTail=true;
      }
    }
    root.hidden=!mode;
    const currentName=boards.find(b=>b.id===nextBoardId)?.name||'Workspace';
    const sh=root.getRootNode();
    for(const id of ['refresh','newTop','websiteTop'])
      sh.getElementById(id).hidden=mode;
    if(mode){
      sh.getElementById('projectScroll').hidden=true;
      sh.getElementById('messageScroll').hidden=true;
      sh.querySelector('.composeArea').hidden=true;
      sh.getElementById('title').textContent=currentName;
      sh.getElementById('optionsTop').hidden=true;
      sh.getElementById('home').hidden=true;
    }
    if(!mode){
      // Do not discard a pending new-chat confirmation while the view is hidden.
      if(newChatAwaiting && snapshot?.createdChatId){
        newChatAwaiting=false;newDraft.value='';newStatus.textContent='';
      }else if(newChatAwaiting && snapshot?.newChatError && !snapshot?.creatingChat){
        newChatAwaiting=false;newStatus.textContent=snapshot.newChatError;
      }
      pickerOpen=false;newChatOpen=false;
      if(pickerDialog)pickerDialog.hidden=true;
      if(newDialog)newDialog.hidden=true;
      return;
    }
    root._count.textContent=ids.length+' / '+(snapshot.capacity||'?')+' chats';
    updateCanvasDialogs();
    updateBoardAttention();
    const key=ids.join('|');
    const justAdded=lastIds && key!==lastIds
      ?ids.find(id=>!lastIds.split('|').includes(id)):null;
    if(key!==lastIds){
      // Detaching/reinserting existing panel nodes resets WebView scrollTop.
      // Preserve each panel's reading position independently.
      const scrollPositions=new Map([...views].map(([id,view])=>
        [id,{top:Number.isFinite(view.savedScrollTop)
             ?view.savedScrollTop:view.messages.scrollTop,
             follow:view.followTail}]));
      const focused=sh.activeElement;
      const focusedPanel=[...views.values()].find(view=>
        view.panel.contains(focused));
      for(const id of [...views.keys()]){
        if(!ids.includes(id)){
          views.get(id).dispose?.();
          views.get(id).panel.remove();
          views.delete(id);
        }
      }
      if(!ids.length){
        grid.replaceChildren(el('div','nwEmpty','Use Add chats to bring an existing conversation onto this board, or New chat to start one.'));
      }else{
        const ordered=ids.map(id=>views.get(id)||addPanel(id)).map(x=>x.panel);
        grid.replaceChildren(...ordered);
        for(const [id,position] of scrollPositions){
          const view=views.get(id);
          if(!view)continue;
          if(position.follow)continue;
          view.autoScrolling=true;
          view.messages.scrollTop=position.top;
        }
        if(focusedPanel?.panel.isConnected&&focused?.isConnected)
          focused.focus({preventScroll:true});
        requestAnimationFrame(()=>{
          for(const [id,position] of scrollPositions){
            const view=views.get(id);
            if(!view)continue;
            if(position.follow)followLatest(view);
            else view.messages.scrollTop=position.top;
          }
          requestAnimationFrame(()=>{
            for(const [id,position] of scrollPositions){
              const view=views.get(id);
              if(view){
                view.savedScrollTop=null;
                if(!position.follow)view.autoScrolling=false;
              }
            }
          });
        });
      }
      lastIds=key;
    }
    canvas.sync(snapshot.panels||[],snapshot.camera);
    if(justAdded)canvas.focus(justAdded);
    hotCameras.delete(nextBoardId);
    for(const info of snapshot.panels||[]){
      const view=views.get(info.id);if(!view)continue;
      view.title.textContent=info.title||'Conversation';
      view.title.title=info.title||info.id;
      view.lastInfo=info;
      renderHistory(view,info);
      if(view.restoredScrollTop!==null &&
         Number.isFinite(view.restoredScrollTop)){
        const position=view.restoredScrollTop;
        // A saved numeric scrollTop is obsolete after panel reflow. If the
        // user was following the tail at reload, honour the sticky intent.
        if(view.followTail)followLatest(view);
        else{
          view.autoScrolling=true;
          view.messages.scrollTop=position;
          requestAnimationFrame(()=>{
            if(view.messages.isConnected && !view.followTail)
              view.messages.scrollTop=position;
            view.autoScrolling=false;
          });
        }
        view.restoredScrollTop=null;
      }
      if(view.pending){
        const outcome=window.__nekoOutgoing.reconcile(
          view.pending,info.messages||[],info);
        if(outcome.event==='confirmed')view.pending=null;
        drawPending(view);
      }
      const busy=!!info.sending || !!info.generating || !!info.uncertain || !info.authorized;
      view.busy=busy;
      view.verifyOnly=!!info.uncertain&&!info.generating&&!info.sending;
      view.stop.hidden=![info.sending,info.generating,info.uncertain].some(Boolean);
      view.stop.disabled=!!info.stopPending||info.stopFeedback==='stop_checking';
      view.stop.textContent=view.stop.disabled?'Checking…':
        view.verifyOnly?'Verify':'Stop';
      // The backend deliberately retains submitted text until acknowledgement.
      // Do not mirror that old draft into an already cleared composer.
      if(view.restoredDraft!==undefined &&
         view.restoredDraft===(info.draft||''))
        view.restoredDraft=undefined;
      if(!view.pending && view.suppressedDraft===null &&
         view.restoredDraft===undefined &&
         sh.activeElement!==view.draft &&
         view.draft.value!==(info.draft||'')){
        view.draft.value=info.draft||'';
      }
      renderPanelAttachments(view,info);
      view.send.disabled=busy||!!view.pending||
        (!view.draft.value.trim()&&!view.attached?.length);
      const recent=[...(info.messages||[])].reverse().find(m=>
        m.role==='assistant' && m.kind==='message' && m.text?.trim());
      const activity=info.live?.activities?.at(-1);
      const raw=info.live?.text ||
        (!info.generating && info.historyFinalMissing
          ?'The latest final answer is missing from saved history. Neko will recheck once; use Refresh if it remains incomplete.'
          :info.generating && activity?.title?
          activity.title+' · '+(activity.status||'working'):
          recent?.text || '');
      view.overview.textContent=raw.replace(/[#*`>_\[\]]/g,'').slice(-300);
      observeAttention(view,info);
      showChatState(view,info);
      applyLive(view,info.live);
      if(view.initialTail&&((info.messages||[]).length>0||
          info.live?.text||info.live?.activities?.length)){
        view.initialTail=false;
        if(view.followTail)followLatest(view);
      }
      if(info.authorized&&!info.newChatAwaitingHistory&&
         !info.historyRevision&&!info.historyLoading&&!view.requestedHistory){
        view.requestedHistory=true;
        // A frontend-only reload must never launch eight new history reads.
        // Any missing history can be retried explicitly with Refresh.
        if(!next.frontendHotReload)
          post('panel-refresh-history',{id:info.id});
      }
    }
  }
  function live(changes){
    for(const [id,update] of Object.entries(changes||{})){
      const view=views.get(id);
      if(view){
        markNewCompletion(id,update);
        applyLive(view,update);
        // Live SSE patches do not increment historyRevision. A terminal
        // reply can arrive and render before the next saved-history GET,
        // especially during a shared history cooldown. Reconcile the local
        // user bubble against this specific NEW completed response, without
        // waiting for another complete workspace snapshot.
        const prior=view.lastInfo||{};
        if(view.pending && view.pending.stage==='syncing' &&
           prior.status==='sse_accepted' && !prior.uncertain &&
           update.completed===true && !update.error &&
           update.messageId && update.messageId!==view.pending.priorLiveId){
          const outcome=window.__nekoOutgoing.reconcile(
            view.pending,view.lastHistory||[],
            {...prior,phase:'completed',sending:false,generating:false,
             live:update});
          if(outcome.event==='confirmed'){
            view.pending=null;
            view.suppressedDraft=null;
            view.busy=false;
            drawPending(view);
            view.send.disabled=!view.draft.value.trim()&&!view.attached?.length;
          }
        }
        showChatState(view,view.lastInfo||{},update);
        updateUnreadDecorations();
      }
    }
  }
  window.__nekoWorkspace={receive,live,backgroundSignals,
    exportHotState:()=>{
      const boards=new Map(boardViews);
      if(activeBoardId)boards.set(activeBoardId,views);
      const saved=Object.create(null);
      for(const [boardId,panels] of boards){
        saved[boardId]=Object.create(null);
        for(const [id,view] of panels)
          saved[boardId][id]={
            draft:view.draft.value,
            scroll:view.messages.scrollTop,
            followTail:view.followTail,
            suppressedDraft:view.suppressedDraft,
            pending:view.pending?{...view.pending,
              seenIds:[...view.pending.seenIds]}:null
          };
      }
      return {panels:saved,boardId:activeBoardId,
        canvas:canvas?.inspect()||null,
        attention:[...chatAttention],
        dialogs:{
          pickerOpen,pickerSearch:pickerSearch?.value||'',
          newChatOpen,newDraft:newDraft?.value||'',
          newChatAwaiting
        }};
    },
    restoreHotState:data=>{
      if(!data || typeof data!=='object')return;
      if(data.dialogs && typeof data.dialogs==='object')
        hotDialogState=data.dialogs;
      for(const [id,state] of data.attention||[])
        if(typeof id==='string' && state && typeof state==='object')
          chatAttention.set(id,{
            primed:!!state.primed,unread:!!state.unread,
            lastReplyId:state.lastReplyId||null,
            lastCompletionId:state.lastCompletionId||null,
            wasWorking:!!state.wasWorking,error:state.error||''
          });
      for(const [board,panels] of Object.entries(data.panels||{}))
        if(panels&&typeof panels==='object')
          hotPanels.set(board,panels);
      if(data.boardId && data.canvas)
        hotCameras.set(data.boardId,data.canvas);
    },
    inspect:()=>({
    visible:mode,count:views.size,boardId:activeBoardId,
    boards:snapshot?.boards,ids:[...views.keys()],
    canvas:canvas?.inspect(),historyItems:[...views.values()]
      .map(v=>({id:v.panel.dataset.id,messages:v.messages.querySelectorAll('.nwMessage').length}))
  })};
  if(document.readyState==='loading')
    document.addEventListener('DOMContentLoaded',mount,{once:true});
  else mount();
})();