(() => {
  // This script is only allowed to run in the dedicated local frontend.
  if (location.origin !== 'https://neko.internal') return;
  const channel = 'neko-ui-bridge-v1';
  const token = __NEKO_EMBED_TOKEN__;
  if (window.__nekoShell?.version === 1) return;
  let host, shadow, state = null, selected = null, lastVersion = -1;
  let submitting = false;
  const singleOutgoing=new Map();
  const singleSuppressedDraft=new Map();
  const singleToolGroupCache=new Map();
  const singleActivityOpen=new Set();
  let singleFollowTail=true;
  let singleNeedsInitialTail=true;
  let singleTailQueued=false;
  let singleAutoScrolling=false;
  let hotSingleScroll=null;
  // Scroll after HTML/Markdown layout finishes, not during DOM replacement.
  function followSingleLatest(scroll){
    if(!singleFollowTail||singleTailQueued)return;
    singleTailQueued=true;
    singleAutoScrolling=true;
    requestAnimationFrame(()=>{
      if(singleFollowTail&&!scroll.hidden)
        scroll.scrollTop=scroll.scrollHeight;
      requestAnimationFrame(()=>{
        if(singleFollowTail&&!scroll.hidden)
          scroll.scrollTop=scroll.scrollHeight;
        singleTailQueued=false;
        singleAutoScrolling=false;
      });
    });
  }
  let renameDialog = null;
  let deleteDialog = null;
  let activeOptions = null;
  let renameOutcomeRevision = -1;
  const $ = id => shadow?.getElementById(id);
  const isSavedChatTitle=id=>typeof id==='string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id);
  const send = (action, data = {}) => {
    try { window.chrome?.webview?.postMessage({channel, token, action, ...data}); }
    catch (_) {}
  };
  // Frontend faults go to a local, bounded diagnostic file via the existing
  // trusted bridge. Never record error messages or stacks: they may include
  // snippets of user content, secrets, or URLs.
  const sourceFile=value=>{
    const file=String(value||'').split(/[?#]/)[0].split('/').pop()||'';
    return /^(NekoSurface|NekoWorkspace|NekoMarkdown|NekoCanvas|NekoOutgoing|NekoToolGroups)\.js$/.test(file)
      ?file:'';
  };
  window.addEventListener('error',event=>{
    const isResource=event.target && event.target!==window &&
      event.target.tagName==='SCRIPT';
    send('ui-diagnostic',{
      code:isResource?'script_load_failed':'javascript_exception',
      detail:isResource?'script_resource':
        (event.error?.name||'Error'),
      source:sourceFile(isResource?event.target.src:event.filename),
      line:Number.isInteger(event.lineno)?event.lineno:0,
      column:Number.isInteger(event.colno)?event.colno:0
    });
  },true);
  window.addEventListener('unhandledrejection',event=>{
    send('ui-diagnostic',{
      code:'unhandled_rejection',
      detail:event.reason?.name||typeof event.reason
    });
  });
  function mount() {
    if (!document.documentElement || host?.isConnected) return;
    host = document.createElement('div');
    host.id = 'neko-shell-surface';
    host.style.cssText = 'all:initial;font-family:"Segoe UI",Arial,Helvetica,sans-serif;font-size:19.5px;line-height:1.5;position:fixed;inset:0;z-index:2147483646;pointer-events:none;';
    document.documentElement.appendChild(host);
    shadow = host.attachShadow({mode:'open'});
    shadow.innerHTML = `
      <style>
        :host{all:initial;position:fixed;inset:0;z-index:2147483646;font-family:"Segoe UI",Arial,Helvetica,sans-serif;color:#e5e9ef;font-size:19.5px;line-height:1.5}
        *,*:before,*:after{box-sizing:border-box}
        button,input,textarea,select{font:inherit}
        button{cursor:pointer;color:inherit}
        button:disabled{cursor:not-allowed;opacity:.4}
        button:focus-visible,select:focus-visible,textarea:focus-visible,input:focus-visible{outline:2px solid #94d2c7;outline-offset:3px}
        [hidden]{display:none!important}
        #shell{position:fixed;inset:0;display:grid;grid-template-columns:minmax(0,1fr);grid-template-rows:minmax(0,1fr);background:#0c1116;pointer-events:auto;overflow:hidden;font-family:"Segoe UI",Arial,Helvetica,sans-serif;font-size:19.5px}
        aside{background:#11171d;border-right:1px solid #27313a;min-width:0;min-height:0;overflow-y:auto;scrollbar-width:thin;display:flex;flex-direction:column;padding:26px 13px 13px}
        .brand{display:flex;align-items:center;gap:11px;padding:0 13px 25px;font-size:28.5px;font-weight:680;letter-spacing:-.55px}
        .mark{height:42px;width:42px;border-radius:10px;background:#b0e6d9;color:#102a28;display:grid;place-items:center;font-size:27px;font-weight:800}
        .brand .sub{font-size:15px;font-weight:560;letter-spacing:1px;color:#778891;margin-left:1px}
        .new{display:flex;align-items:center;justify-content:center;gap:9px;width:100%;height:56px;border:1px solid #a9dccc;background:#b0e6d9;color:#153028;border-radius:10px;font-weight:680;transition:filter .15s}
        .new:hover{filter:brightness(1.08)}
        .section{color:#72818c;font-size:15px;font-weight:730;letter-spacing:1.35px;margin:29px 12px 12px}
        .projectSection{display:flex;align-items:center;justify-content:space-between;margin:26px 6px 8px 12px}
        .projectSection .section{margin:0}
        .projectSync{border:1px solid transparent;background:transparent;color:#9db4be;border-radius:8px;font-size:24px;width:38px;height:38px;display:grid;place-items:center}
        .projectSync:hover:not(:disabled){color:#e8f1f3;background:#22323b;border-color:#40545e}
        #chats{overflow-y:auto;overflow-x:hidden;flex:0 1 auto;max-height:160px;min-height:0;scrollbar-color:#344149 transparent;scrollbar-width:thin}
        .chat{display:flex;align-items:center;gap:4px;margin:2px 0;border-radius:9px;color:#aab7bf;min-height:52px}
        .chat:hover{background:#19232a;color:#f4f7f8}
        .chat.active{background:#25342f;color:#d8f5e9}
        .chat button.open{border:0;background:transparent;text-align:left;min-width:0;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;padding:12px 9px 12px 14px;font-size:18.9px}
        .chat button.close{background:transparent;border:0;color:#718088;width:36px;height:36px;border-radius:6px;flex:0 0 36px;opacity:0}
        .chat:hover button.close,.chat.active button.close{opacity:1}
        .chat button.close:hover{background:#38444a;color:#fff}
        footer{border-top:1px solid #28323b;padding:12px 2px 0}
        .low{width:100%;display:flex;align-items:center;gap:10px;text-align:left;border:0;background:transparent;color:#a2b2bc;border-radius:8px;padding:10px;font-size:18px}
        .low:hover{background:#202c34;color:#fff}
        .low svg{width:23px;height:23px;stroke-width:1.7;flex:0 0 23px}
        .workspace{width:100%;background:#1b252c;color:#cbd7dc;border:1px solid #303e47;border-radius:8px;padding:8px;font-size:18px;margin:5px 0}
        #openLink{display:flex;gap:6px;padding:7px 2px}
        #linkValue{width:100%;min-width:0;color:#fff;background:#0b1116;border:1px solid #40505b;padding:9px;border-radius:8px}
        #linkGo{background:#b0e6d9;color:#19352e;border:0;border-radius:8px;padding:0 10px}
        main{display:flex;flex-direction:column;min-width:0;min-height:0;overflow:hidden}
        .top{min-height:86px;border-bottom:1px solid #222b32;display:flex;align-items:center;gap:15px;padding:0 31px;justify-content:space-between}
        .titlewrap{min-width:0;display:flex;align-items:baseline;gap:10px}
        .label{font-size:16.5px;color:#687b85;white-space:nowrap}
        #title{font-size:19.5px;font-weight:660;white-space:nowrap;text-overflow:ellipsis;overflow:hidden;color:#e8edf0}
        .topright{display:flex;align-items:center;gap:10px}
        #connection{color:#879a9e;font-size:16.5px;white-space:nowrap}
        #connection:before{content:'';width:6px;height:6px;border-radius:50%;background:#7bd4b5;display:inline-block;margin-right:8px;vertical-align:middle}
        .iconbutton{border:1px solid #27343c;background:#182027;border-radius:9px;padding:10px 13px;font-size:16.5px;color:#bdcbd3;white-space:nowrap}
        .iconbutton:hover{background:#26323a;color:#fff}
        #messageScroll{overflow-y:auto;flex:1;min-height:0;scrollbar-color:#33414a transparent;scrollbar-width:thin}
        #messages{width:min(100%,900px);margin:0 auto;padding:39px 34px 55px;display:flex;flex-direction:column;gap:35px}
        .message{width:100%;display:flex;flex-direction:column;gap:9px;min-width:0}
        /* A small opposing gutter makes speaker roles clear while scrolling. */
        .message.user,.message.assistant{
          width:calc(100% - clamp(20px,7%,56px))}
        .message.user{align-items:flex-end;align-self:flex-end}
        .message.assistant{align-self:flex-start}
        .message .speaker{font-size:16.5px;font-weight:680;color:#9fcabf;letter-spacing:.05px}
        .message.user .speaker{color:#899aa4}
        .message .content{font-size:21px;line-height:1.85;white-space:pre-wrap;overflow-wrap:anywhere;color:#dfe5e9;max-width:100%;user-select:text}
        .message.user .content{background:#253039;border:1px solid #34424c;padding:12px 18px;border-radius:17px 17px 4px 17px;max-width:88%;color:#f0f3f4}
        .message.pending .content{border-style:dashed;border-color:#679e91}
        .message.pending .pendingStatus{font-size:14px;color:#97c7b9}
        .message.pending .pendingStatus.warn{color:#efb98f}
        .message.pending button{padding:8px 12px;border:1px solid #6b9f8e;
          background:#263f38;border-radius:8px;color:#e6fff1;cursor:pointer}
        .message:not(.user) .content{padding:0 4px}
        .message .content.nekoMarkdown{white-space:normal}
        .message.activity{width:calc(100% - clamp(20px,7%,56px));align-self:flex-start;
          border:1px solid #3c5458;border-radius:11px;background:#18292e;padding:0;gap:0}
        .message.activity details{width:100%;min-width:0}
        .message.activity summary{padding:12px 16px;cursor:pointer;color:#bee5d6;
          font-size:16px;font-weight:700;overflow-wrap:anywhere}
        .message.activity .activityContent{padding:0 16px 16px;font-size:15px;
          white-space:normal;color:#c7dad6}
        .message.activity.thinking{background:#202733;border-color:#4c536c}
        .message.activity.tool_group{display:block;background:#192f2c;border-color:#4a746a}
        .message.activity.tool_group > summary{color:#bcf2dd}
        .message.activity .toolGroupContent{display:flex;flex-direction:column;
          padding:0 9px 10px;gap:6px}
        .message.activity .toolGroupContent > .message.activity{
          width:100%;margin:0;background:#13252a}
        .message.activity .toolGroupContent summary{font-size:14px;padding:8px 12px}
        .empty{display:flex;flex-direction:column;justify-content:center;align-items:center;gap:11px;margin:auto;text-align:center;padding:30px}
        .empty .orb{width:54px;height:54px;border:1px solid #47695f;border-radius:18px;color:#b2e7d9;display:grid;place-items:center;font-size:42px;background:#1a2a29;margin-bottom:11px}
        .empty h1{font-size:39px;letter-spacing:-.8px;font-weight:640;color:#f0f4f4;margin:0}
        .empty p{color:#84969f;font-size:19.5px;margin:0;max-width:390px}
        .historyPending{width:min(100%,900px);margin:0 auto;padding:50px 34px;display:flex;flex-direction:column;gap:24px}
        .historyPending .loadingLabel{font-size:19.5px;font-weight:640;color:#c3d4d9}
        .historyPending .loadingHint{font-size:16.5px;color:#8a9fa9}
        .loadingBubble{height:34px;border-radius:10px;background:linear-gradient(90deg,#18252c,#263740,#18252c);background-size:200% 100%;animation:loadingPulse 1.8s ease-in-out infinite}
        .loadingBubble:nth-child(3){width:86%}
        .loadingBubble:nth-child(4){width:72%}
        .loadingBubble:nth-child(5){width:60%}
        @keyframes loadingPulse{from{background-position:100% 0}to{background-position:-100% 0}}
        @media(prefers-reduced-motion:reduce){.loadingBubble{animation:none}}
        .historyBlank{padding:40px 34px;text-align:center;color:#a9bcc5;font-size:19px}
        .historyBlank .historyHint{font-size:16.5px;color:#8297a1;margin-top:12px}
        .composeArea{padding:11px 33px 20px;background:linear-gradient(0deg,#0c1116 80%,transparent);flex-shrink:0}
        .composer{width:min(100%,820px);margin:auto}
        .composebox{background:#1b252c;border:1px solid #35444e;border-radius:17px;padding:13px 14px 10px;box-shadow:0 8px 38px #0002}
        .composebox:focus-within{border-color:#638a83}
        #draft{display:block;resize:none;width:100%;max-height:260px;min-height:60px;background:transparent;border:0;outline:none!important;color:#f1f4f5;font-size:21px;line-height:1.5;padding:4px 6px 8px}
        #draft::placeholder{color:#75868f}
        .composeControls{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:10px;padding-top:6px}
        .hint{font-size:16.5px;color:#677983;padding:0 5px;flex:1 1 240px}
        .composeBtns{display:flex;gap:7px;margin-left:auto}
        #stop{border:1px solid #465a64;background:#26353d;border-radius:9px;padding:8px 12px}
        #submit{border:0;background:#b4e9d9;color:#17352c;border-radius:9px;width:48px;height:48px;display:grid;place-items:center}
        #submit:hover:not(:disabled){background:#d0faeb}
        #submit svg{width:23px;height:23px}
        #notice{width:min(100%,820px);margin:0 auto 8px;color:#99abb3;font-size:16.5px;min-height:17px}
        #notice.warn{color:#f0c58d}
        #projects{display:flex;flex-direction:column;gap:4px;max-height:230px;overflow:auto;min-height:0;scrollbar-width:thin;scrollbar-color:#344149 transparent}
        .projectLink{display:flex;gap:10px;align-items:center;width:100%;border:0;text-align:left;background:transparent;color:#b1bfc7;border-radius:10px;padding:10px 12px;font-size:18px;min-width:0}
        .projectLink:hover{background:#1d2c32;color:#fff}
        .projectLink.active{background:#283a34;color:#d9f8e8}
        .projectLink .folder{color:#8bc9b6;font-size:23px;line-height:1}
        .projectLink .projectName{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        #projectScroll{overflow-y:auto;flex:1;min-height:0;padding:43px clamp(22px,5vw,74px) 60px;scrollbar-color:#344149 transparent;scrollbar-width:thin}
        .projectHeader{max-width:1180px;margin:0 auto 32px}
        .projectKicker{font-size:14px;font-weight:700;letter-spacing:2px;text-transform:uppercase;color:#85b7aa}
        #projectTitle{font-size:40px;font-weight:690;letter-spacing:-1px;margin:7px 0 5px}
        #projectDescription{font-size:17px;color:#9eafb7;margin:0;line-height:1.5}
        #projectMeta{font-size:16px;color:#83959d;margin-top:18px}
        #projectMessage{max-width:1180px;margin:0 auto 18px;color:#9eafb7;font-size:17px}
        .projectGrid{max-width:1180px;margin:auto;display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,310px),1fr));gap:19px}
        .projectCard{min-height:240px;display:flex;flex-direction:column;gap:18px;text-align:left;border:1px solid #33444c;border-radius:18px;background:#18232a;color:#e5ebec;padding:25px;min-width:0;transition:border-color .15s,background .15s,transform .15s}
        .projectCard:hover{background:#202f37;border-color:#74b4a1;transform:translateY(-2px)}
        .projectCard:focus-visible{border-color:#b2e8d9}
        .cardTop{display:flex;align-items:flex-start;justify-content:space-between;gap:10px}
        .cardIcon{display:grid;place-items:center;flex:0 0 39px;width:39px;height:39px;border-radius:12px;background:#2b403d;color:#b8f1db;font-size:24px}
        .cardStatus{display:flex;gap:7px;align-items:center;font-size:14px;font-weight:680;color:#aab8be;white-space:nowrap}
        .cardStatus:before{content:'';width:7px;height:7px;border-radius:50%;background:#71838b}
        .cardStatus.completed:before{background:#77d2b6}
        .cardStatus.responding:before{background:#d8b97d;box-shadow:0 0 0 4px #d8b97d20}
        .cardTitle{font-weight:680;font-size:23px;line-height:1.3;letter-spacing:-.3px;overflow-wrap:anywhere}
        .cardSnippet{color:#a6b5bd;font-size:18px;line-height:1.5;display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;overflow-wrap:anywhere}
        .cardBottom{margin-top:auto;padding-top:16px;border-top:1px solid #314049;display:flex;justify-content:space-between;gap:10px;color:#91a4ac;font-size:15px}
        .cardOpen{color:#b7e8d7;font-weight:650;white-space:nowrap}
        .cardWrap{position:relative;min-width:0;display:flex;flex-direction:column}
        .previewToggle{align-self:stretch;min-height:42px;text-align:left;
          border:1px solid #33444c;border-top:0;background:#18282e;color:#b1dcca;
          border-radius:0 0 15px 15px;padding:8px 18px;font-size:15px;
          font-weight:620;display:flex;align-items:center;justify-content:space-between}
        .previewToggle:hover{background:#26413e;color:#f4fdf8}
        .previewToggle:focus-visible{outline:2px solid #b0e6d9;outline-offset:2px}
        .cardWrap .projectCard{border-radius:18px 18px 0 0;flex:1}
        .cardWrap.previewOpen .projectCard{flex:0 0 auto}
        .cardWrap.previewOpen .previewToggle{border-radius:0}
        .previewPanel{padding:14px 17px;background:#101e23;color:#cfdddf;
          border:1px solid #33444c;border-top:0;border-radius:0 0 15px 15px;
          max-height:245px;overflow-y:auto;overscroll-behavior:contain;
          scrollbar-width:thin;user-select:text}
        .previewEntry{margin:0 0 12px;border-bottom:1px solid #293e42;padding-bottom:10px}
        .previewEntry:last-child{border:0;margin:0;padding:0}
        .previewRole{color:#8cdac1;font-weight:700;font-size:13px;margin-bottom:4px}
        .previewText{white-space:pre-wrap;overflow-wrap:anywhere;font-size:15px;
          line-height:1.48;color:#cedbdd;user-select:text}
        .previewHint{font-size:15px;color:#98abb0;line-height:1.5}
        .previewLive{margin:10px 0;padding:12px 13px;border:1px solid #4c766b;
          background:#15302d;border-radius:11px}
        .previewLiveHeading{font-size:13px;font-weight:750;color:#8fe6bd;
          margin-bottom:6px;letter-spacing:.2px}
        .previewLiveText{font-size:15px;color:#e5f5ee;line-height:1.5;
          white-space:pre-wrap;overflow-wrap:anywhere;user-select:text}
        .previewLive.completed{border-color:#415e60;background:#19292c}
        .previewLive.completed .previewLiveHeading{color:#b4cbd0}
        .previewLive.failed{border-color:#94675d}
        .previewEntry[hidden]{display:none!important}
        .previewRetry{border:1px solid #547168;border-radius:8px;
          background:#294139;color:#dcf9ec;margin-top:9px;padding:6px 11px;
          font-size:14px}
        .previewRetry:hover{background:#385e4f}
        .reorderable{cursor:grab;user-select:none}
        .reorderable:active{cursor:grabbing}
        .reorderable.dragging{opacity:.48;transform:none}
        .reorderable.dragBefore{outline:3px solid #b1e6d6;outline-offset:3px}
        .reorderable.dragAfter{outline:3px dashed #b1e6d6;outline-offset:3px}
        .spaceCard{position:relative}
        .cardWrap .cardTop{padding-right:0}
        .cardWrap .projectCard{width:100%;height:auto;padding-top:25px}
        .cardWrap .cardTop{padding-right:65px}
        .cardWrap .cardTop{padding-right:0}
        .cardOptionsButton{position:absolute;right:21px;top:21px;z-index:1;
          border:1px solid #425b61;background:#26363d;color:#d1e7e1;
          border-radius:9px;font-size:24px;line-height:1;width:41px;height:37px;
          display:grid;place-items:center}
        .cardOptionsButton:hover:not(:disabled),.cardOptionsButton[aria-expanded="true"]{
          background:#35534f;border-color:#9ed9ca;color:#fff}
        .optionsMenu{position:fixed;z-index:45;min-width:175px;
          display:flex;flex-direction:column;gap:3px;background:#1c2c33;
          border:1px solid #547068;border-radius:13px;padding:7px;
          box-shadow:0 16px 48px #000b}
        .optionsMenu button{border:0;background:transparent;
          text-align:left;border-radius:8px;padding:10px 12px;font-size:17px}
        .optionsMenu button:hover,.optionsMenu button:focus-visible{
          background:#2a413e;color:#f5fcf8}
        .optionsMenu .danger{color:#f4b6ad}
        .optionsMenu .danger:hover,.optionsMenu .danger:focus-visible{
          background:#563431;color:#fff}
        #optionsTop{font-size:22px;line-height:1;padding:9px 14px}
        .deleteShade{position:fixed;inset:0;z-index:50;background:#071016e8;
          display:grid;place-items:center;padding:22px}
        .deleteDialog{width:min(100%,520px);background:#19252d;
          border:1px solid #765650;box-shadow:0 20px 75px #0009;
          border-radius:19px;padding:27px;display:flex;flex-direction:column;gap:16px}
        .deleteDialog h2{font-size:26px;margin:0;font-weight:690}
        .deleteDialog p{font-size:16px;line-height:1.55;color:#b6c3c8;margin:0}
        .deleteDialog strong{color:#f0f5f5;overflow-wrap:anywhere}
        #deleteError{font-size:15px;line-height:1.4;color:#f3bb9c}
        .deleteButtons{display:flex;justify-content:flex-end;gap:10px}
        .deleteButtons button{border:1px solid #465b61;background:#26353e;
          border-radius:10px;padding:10px 18px;font-size:17px}
        #deleteConfirm{color:#fff;background:#a13e43;border-color:#b65b60;font-weight:680}
        #deleteConfirm:hover:not(:disabled){background:#bc4d53}
        #renameTop{font-size:16px}
        .renameShade{position:fixed;inset:0;z-index:50;background:#071016e8;
          display:grid;place-items:center;padding:22px}
        .renameDialog{width:min(100%,520px);background:#19252d;border:1px solid #567269;
          box-shadow:0 20px 75px #0009;border-radius:19px;padding:27px;
          display:flex;flex-direction:column;gap:16px}
        .renameDialog h2{font-size:26px;margin:0;font-weight:690}
        .renameDialog p{font-size:16px;line-height:1.55;color:#9cabb3;margin:0}
        #renameInput{font-size:20px;min-height:52px;padding:11px 13px;width:100%;
          background:#0e1920;color:#f1f7f5;border:1px solid #4c625f;border-radius:10px}
        #renameError{font-size:15px;line-height:1.4;color:#f3bb9c}
        .renameButtons{display:flex;justify-content:flex-end;gap:10px}
        .renameButtons button{border:1px solid #465b61;background:#26353e;
          border-radius:10px;padding:10px 18px;font-size:17px}
        #renameSave{color:#102e27;background:#b0e6d9;border-color:#b0e6d9;font-weight:680}
        #renameSave:hover:not(:disabled){background:#d0faeb}
        @media(max-width:720px){#renameTop{padding:9px 8px;font-size:14px}}
        #projectEmpty{grid-column:1/-1;color:#889ca5;font-size:19px;padding:24px 0}
        .libraryHeader{display:flex;justify-content:space-between;align-items:center;margin:16px 5px 8px 12px}
        .libraryHeader .section{margin:0}
        #librarySearch{width:100%;min-height:43px;border:1px solid #34434b;border-radius:10px;background:#1a252b;color:#eef4f4;padding:8px 13px;font-size:17px}
        #librarySearch::placeholder{color:#718791}
        #libraryInfo{color:#7c8f99;font-size:14px;padding:9px 10px 5px}
        #libraryChats{min-height:0;flex:1;overflow:auto;scrollbar-width:thin;scrollbar-color:#344149 transparent;padding-right:2px}
        .libraryChat{display:flex;flex-direction:column;gap:3px;text-align:left;width:100%;border:0;border-radius:9px;background:transparent;padding:10px 11px;color:#bdcbd1;min-width:0}
        .libraryChat:hover{background:#1c2a30;color:#f2f8f6}
        .libraryChat.active{background:#2a3833;color:#d7f7e8}
        .libraryTitle{font-size:17px;line-height:1.35;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;width:100%}
        .libraryDetail{display:flex;align-items:center;gap:8px;font-size:13px;color:#80969d;white-space:nowrap;overflow:hidden}
        .libraryProject{color:#9ed1bf;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .libraryLoadMore{border:1px solid #30434b;background:#1b292f;color:#aac4cc;border-radius:9px;padding:10px;margin-top:7px;font-size:15px;width:100%}
        .libraryLoadMore:hover{background:#2b3d45}
        .libraryLoadMore:disabled{opacity:.4}
        .top{flex-shrink:0}
        .top .brand{padding:0 15px 0 0;font-size:24px;white-space:nowrap}
        .top .brand .mark{height:35px;width:35px;font-size:23px}
        .top .brand .sub{font-size:12px}
        .top .iconbutton{padding:10px 14px}
        #home{white-space:nowrap}
        #newTop{background:#b0e6d9;border-color:#b0e6d9;color:#13342e;font-weight:680}
        #newTop:hover{background:#d0faeb}
        .top .titlewrap{align-items:center;gap:14px;flex:1}
        #projectScroll{padding-bottom:65px}
        .projectHeader{max-width:1210px}
        .projectGrid{max-width:1210px}
        .spaceCard{min-height:270px}
        .spaceCard .cardIcon{width:53px;height:53px;font-size:29px;flex-basis:53px}
        .spaceCard .cardTitle{font-size:28px}
        .collectionControls{max-width:1210px;margin:0 auto 20px;display:flex;align-items:center;gap:12px}
        #cardSearch{font-size:18px;flex:1;min-width:0;max-width:500px;border:1px solid #34434b;border-radius:11px;background:#18252c;color:#eef4f4;padding:11px 15px}
        #cardSearch::placeholder{color:#7d919a}
        #cardLoadMore,#cardShowMore{border:1px solid #32444c;background:#1e2c34;border-radius:10px;padding:12px 17px;color:#d0e4df}
        #cardLoadMore:hover,#cardShowMore:hover{background:#2b3e45}
        @media(max-width:720px){.top .brand .sub,.top #connection,.top #websiteTop{display:none!important}.top{gap:6px}.top .iconbutton{padding:9px}.top .titlewrap{gap:8px}#title{font-size:17px}#projectTitle{font-size:31px}}

        @media(max-width:960px){#shell{grid-template-columns:minmax(0,1fr)}.top{padding:0 15px}.label,#connection{display:none!important}#messages{padding:23px 17px}.composeArea{padding:8px 13px 15px}.projectGrid{grid-template-columns:repeat(auto-fit,minmax(min(100%,260px),1fr))}}
      </style>
      <div id="shell">
        <aside hidden>
          <div class="brand"><div class="mark">✳</div><div>Neko</div></div>
          <button class="new" id="new">＋ <span>New chat</span></button>
          <div class="projectSection"><span class="section">PROJECTS</span><button class="projectSync" id="projectSync" aria-label="Refresh project list" title="Refresh project list">↻</button></div>
          <nav id="projects" aria-label="Projects"></nav>
          <div class="section">YOUR TABS</div>
          <nav id="chats" aria-label="Open tabs"></nav>
          <div class="libraryHeader"><span class="section">ALL CHATS</span><button id="libraryRefresh" class="projectSync" aria-label="Refresh chat library" title="Refresh chat library">↻</button></div>
          <input id="librarySearch" placeholder="Search your chats…" aria-label="Search loaded chats"/>
          <div id="libraryInfo" role="status"></div>
          <nav id="libraryChats" aria-label="Chat history"></nav>
          <button id="libraryMore" class="libraryLoadMore" hidden>Load older chats</button>
          <footer>
            <select class="workspace" id="workspaces" aria-label="Workspace" hidden></select>
            <button class="low" id="openLinkButton"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M10 13a5 5 0 0 0 7 .4l3-3a5 5 0 1 0-7-7l-1.8 1.8"/><path d="M14 11a5 5 0 0 0-7-.4l-3 3a5 5 0 1 0 7 7L13 19"/></svg> Open chat link</button>
            <div id="openLink" hidden><input id="linkValue" placeholder="Paste conversation URL" aria-label="Conversation URL"/><button id="linkGo">Open</button></div>
            <button class="low" id="website"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M9 4H4v16h16v-5M13 4h7v7M20 4l-10 10"/></svg> Open in browser</button>
          </footer>
        </aside>
        <main>
          <div class="top"><div class="titlewrap"><div class="brand"><div class="mark">✳</div><div>Neko</div></div><button class="iconbutton" id="home" title="Return to projects">← Projects</button><span id="title">Loading…</span></div><div class="topright"><span id="connection">Connected</span><button class="iconbutton" id="refresh">↻ Refresh</button><button class="iconbutton" id="optionsTop" aria-label="Conversation options" title="Conversation options" aria-haspopup="menu" aria-expanded="false" hidden>⋯</button><button class="iconbutton" id="websiteTop">Browser ↗</button><button class="iconbutton" id="newTop">＋ New chat</button></div></div>
          <section id="projectScroll" hidden aria-label="Project conversations">
            <div class="projectHeader">
              <div class="projectKicker" id="projectKicker">SPACES</div>
              <h1 id="projectTitle">Projects</h1>
              <p id="projectDescription">Your conversations, all in one place.</p>
              <div id="projectMeta"></div>
            </div>
            <div class="collectionControls" id="collectionControls" hidden><input id="cardSearch" type="search" placeholder="Search conversations…" aria-label="Search conversations"/></div>
            <div id="projectMessage" role="status"></div>
            <div id="projectCards" class="projectGrid"></div>
            <div class="collectionControls"><button id="cardShowMore" hidden>Show more cards</button><button id="cardLoadMore" hidden>Load older chats</button></div>
          </section>
          <div id="messageScroll">
            <div id="historyLoading" class="historyPending" role="status" hidden>
              <div class="loadingLabel">Loading conversation…</div>
              <div class="loadingHint">Retrieving saved messages</div>
              <div class="loadingBubble"></div><div class="loadingBubble"></div><div class="loadingBubble"></div>
            </div>
            <div id="historyBlank" class="historyBlank" hidden>
              <div id="historyBlankTitle">No messages to show</div>
              <div id="historyBlankHint" class="historyHint"></div>
            </div>
            <div id="messages"></div><div class="empty" id="empty" hidden><div class="orb">✳</div><h1>Where shall we begin?</h1><p>A quieter space for all the things you want to explore.</p></div></div>
          <div class="composeArea"><div id="notice" aria-live="polite"></div><div class="composer"><div class="composebox"><textarea id="draft" rows="2" spellcheck="true" placeholder="Write a message..." maxlength="24000" aria-label="Message"></textarea><div class="composeControls"><span class="hint">Enter to send · Shift+Enter for a new line</span><div class="composeBtns"><button id="stop" hidden>Stop</button><button id="submit" aria-label="Send message"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12 7-7 7 7M12 5v14"/></svg></button></div></div></div></div></div>
        </main>
        <div class="optionsMenu" id="chatOptions" role="menu" aria-label="Conversation options" hidden>
          <button id="optionRename" role="menuitem">Rename</button>
          <button id="optionDelete" class="danger" role="menuitem">Delete</button>
        </div>
        <div class="deleteShade" id="deleteShade" hidden>
          <div class="deleteDialog" role="dialog" aria-modal="true" aria-labelledby="deleteHeading" aria-describedby="deleteDescription">
            <h2 id="deleteHeading">Delete conversation?</h2>
            <p id="deleteDescription">Delete <strong id="deleteTitle"></strong>? This removes the conversation from your account and Neko. You won't be able to recover it.</p>
            <div id="deleteError" role="alert" hidden></div>
            <div class="deleteButtons"><button id="deleteCancel">Cancel</button><button id="deleteConfirm">Delete conversation</button></div>
          </div>
        </div>
        <div class="renameShade" id="renameShade" hidden>
          <div class="renameDialog" role="dialog" aria-modal="true" aria-labelledby="renameHeading" aria-describedby="renameDescription">
            <h2 id="renameHeading">Rename conversation</h2>
            <p id="renameDescription">This updates the conversation title everywhere it appears.</p>
            <input id="renameInput" type="text" maxlength="180" aria-label="Conversation title" autocomplete="off"/>
            <div id="renameError" role="alert" hidden></div>
            <div class="renameButtons"><button id="renameCancel">Cancel</button><button id="renameSave">Save</button></div>
          </div>
        </div>
      </div>
    `;
    $('new').addEventListener('click', () => send('new'));
    $('newTop').addEventListener('click', () => send('new'));
    $('home').addEventListener('click',()=>send('cards-back'));
    $('cardSearch').addEventListener('input',()=>renderCards());
    $('cardLoadMore').addEventListener('click',()=>send('library-more'));
    $('cardShowMore').addEventListener('click',()=>{visibleCardLimit+=200;renderCards();});
    $('optionsTop').addEventListener('click',event=>{
      const id=state?.selectedId;
      if(!isSavedChatTitle(id))return;
      const entry=(state?.library?.chats||[]).find(x=>x.id===id)||
        (state?.project?.chats||[]).find(x=>x.id===id);
      toggleOptions(event.currentTarget,id,
        entry?.title||state?.selectedTitle||state?.pane?.title||'');
    });
    $('optionRename').addEventListener('click',()=>{
      if(!activeOptions)return;
      const {id,title,anchor}=activeOptions;
      closeOptions(false);
      openRename(id,title,anchor);
    });
    $('optionDelete').addEventListener('click',()=>{
      if(!activeOptions)return;
      const {id,title}=activeOptions;
      openDelete(id,title);
    });
    $('chatOptions').addEventListener('keydown',event=>{
      if(event.key==='Escape'){event.preventDefault();closeOptions();return;}
      if(event.key==='ArrowDown'||event.key==='ArrowUp'){
        event.preventDefault();
        const buttons=[$('optionRename'),$('optionDelete')];
        const selected=buttons.indexOf(shadow.activeElement);
        buttons[(selected+(event.key==='ArrowDown'?1:buttons.length-1))%
          buttons.length].focus();
      }
    });
    shadow.addEventListener('click',event=>{
      if(activeOptions&&!event.target.closest('#chatOptions')&&
         !event.target.closest('#optionsTop')&&
         !event.target.closest('.cardOptionsButton'))closeOptions(false);
    });
    $('deleteConfirm').addEventListener('click',confirmDelete);
    $('deleteCancel').addEventListener('click',closeDelete);
    $('deleteShade').addEventListener('click',event=>{
      if(event.target===$('deleteShade'))closeDelete();
    });
    $('deleteShade').addEventListener('keydown',event=>{
      if(event.key==='Escape'){event.preventDefault();closeDelete();}
      if(event.key==='Tab'){
        const controls=[$('deleteCancel'),$('deleteConfirm')]
          .filter(x=>!x.disabled);
        if(!controls.length)return;
        const current=controls.indexOf(shadow.activeElement);
        if(event.shiftKey&&current<=0){
          controls[controls.length-1].focus();event.preventDefault();
        }else if(!event.shiftKey&&current===controls.length-1){
          controls[0].focus();event.preventDefault();
        }
      }
    });
    window.addEventListener('resize',()=>{if(activeOptions)refreshOptionsAnchor();});
    $('renameSave').addEventListener('click',saveRename);
    $('renameCancel').addEventListener('click',closeRename);
    $('renameInput').addEventListener('keydown',event=>{
      if(event.key==='Enter'){event.preventDefault();saveRename();}
      if(event.key==='Escape'){event.preventDefault();closeRename();}
    });
    $('renameInput').addEventListener('input',()=>{
      if(renameDialog&&!renameDialog.waiting){
        $('renameError').hidden=true;
        $('renameSave').disabled=!$('renameInput').value.trim();
      }
    });
    $('renameShade').addEventListener('click',event=>{
      if(event.target===$('renameShade'))closeRename();
    });
    $('renameShade').addEventListener('keydown',event=>{
      if(event.key==='Escape'){event.preventDefault();closeRename();}
      if(event.key==='Tab'){
        const controls=[$('renameInput'),$('renameCancel'),$('renameSave')]
          .filter(x=>!x.disabled);
        if(!controls.length)return;
        const current=controls.indexOf(shadow.activeElement);
        if(event.shiftKey&&current<=0){
          controls[controls.length-1].focus();event.preventDefault();
        }else if(!event.shiftKey&&current===controls.length-1){
          controls[0].focus();event.preventDefault();
        }
      }
    });
    $('refresh').addEventListener('click', () => {
      if(!state?.project?.visible){send('refresh');return;}
      send(state.project.route==='home'?'project-refresh':
        state.project.route==='unfiled'?'library-refresh':'project-reload');
    });
    $('projectSync').addEventListener('click',()=>send('project-refresh'));
    $('libraryRefresh').addEventListener('click',()=>send('library-refresh'));
    $('libraryMore').addEventListener('click',()=>send('library-more'));
    $('librarySearch').addEventListener('input',()=>renderLibrary());
    $('website').addEventListener('click', () => send('external'));
    $('websiteTop').addEventListener('click', () => send('external'));

    $('workspaces').addEventListener('change', e => send('workspace',{index: Number(e.target.value)}));
    $('openLinkButton').addEventListener('click', () => {
      $('openLink').hidden = !$('openLink').hidden;
      if (!$('openLink').hidden) $('linkValue').focus();
    });
    const openLink = () => {
      const url = $('linkValue').value.trim();
      if (url) send('open', {url});
      $('linkValue').value = ''; $('openLink').hidden = true;
    };
    $('linkGo').addEventListener('click', openLink);
    $('linkValue').addEventListener('keydown', e => {
      if (e.key === 'Enter') {e.preventDefault(); openLink();}
    });
    const draft = $('draft');
    function emitDraft() {
      // Save before a tab-switch event can replace the composer value.
      if (state?.selectedId) send('draft',{id:state.selectedId,text:draft.value});
    }
    draft.addEventListener('input', () => {
      draft.style.height='auto';
      draft.style.height=Math.min(260,Math.max(60,draft.scrollHeight))+'px';
      $('submit').disabled = !draft.value.trim() || !!state?.pane?.sending ||
        !!state?.pane?.uncertain || singleOutgoing.has(state?.selectedId);
      emitDraft();
    });
    function submit() {
      const id=state?.selectedId;
      if (submitting || singleOutgoing.has(id) || !draft.value.trim() ||
          state?.pane?.sending || state?.pane?.uncertain)return;
      const text=draft.value;
      singleOutgoing.set(id,window.__nekoOutgoing.create(id,text,
        state?.pane?.messages||[]));
      singleSuppressedDraft.set(id,text);
      submitting=true;
      draft.value='';
      // The outgoing bubble appears before any network acknowledgement.
      showSinglePendingImmediately(id);
      send('send',{id:id||'',text});
      $('submit').disabled=true;
    }
    $('submit').addEventListener('click', submit);
    $('stop').addEventListener('click',()=>{ if(state?.selectedId) send('stop',{id:state.selectedId}); });
    draft.addEventListener('keydown', e=>{
      if(e.key === 'Enter' && (!e.shiftKey || e.ctrlKey)){
        e.preventDefault(); submit();
      }
    });
    window.__nekoShell = {version:1,post:send,receive,livePreview:receiveLivePreview,
      exportHotState:()=>({
        selectedId:state?.selectedId||null,
        singleDraft:$('draft')?.value||'',
        singleScroll:$('messageScroll')?.scrollTop||0,
        singleFollowTail,
        outgoing:[...singleOutgoing].map(([id,item])=>[
          id,{...item,seenIds:[...item.seenIds]}]),
        suppressed:[...singleSuppressedDraft],
        workspace:window.__nekoWorkspace?.exportHotState?.()||null
      }),
      restoreHotState:data=>{
        if(!data||typeof data!=='object')return;
        singleOutgoing.clear();
        for(const [id,pending] of data.outgoing||[]){
          if(!pending||typeof pending.text!=='string')continue;
          singleOutgoing.set(id,{...pending,seenIds:new Set(pending.seenIds||[])});
        }
        singleSuppressedDraft.clear();
        for(const [id,text] of data.suppressed||[])
          if(typeof text==='string')singleSuppressedDraft.set(id,text);
        singleFollowTail=data.singleFollowTail!==false;
        hotSingleScroll={
          id:data.selectedId,draft:data.singleDraft||'',
          scroll:Number.isFinite(data.singleScroll)?data.singleScroll:0
        };
        window.__nekoWorkspace?.restoreHotState?.(data.workspace);
      },
      inspect:()=>({
      mounted:!!host?.isConnected,htmlSurface:true,
      conversationButtons:$('chats')?.children.length || 0,
      renderedMessages:$('messages')?.children.length || 0,
      composerPresent:!!$('draft'),frontendOrigin:location.origin,
      libraryCount:state?.library?.chats?.length||0,
      libraryTotal:state?.library?.total??null,
      libraryRows:$('libraryChats')?.querySelectorAll('.libraryChat').length||0,
      libraryHasMore:!!state?.library?.hasMore,
      libraryTitles:[...($('libraryChats')?.querySelectorAll('.libraryTitle')||[])]
        .slice(0,12).map(x=>x.textContent),
      selected:!!state?.selectedId,
      activeTabCount:$('chats')?.querySelectorAll('.chat.active').length || 0,
      fontFamily:getComputedStyle($('shell')).fontFamily,
      baseFontPx:parseFloat(getComputedStyle($('shell')).fontSize),
      tabFontPx:parseFloat(getComputedStyle($('chats').querySelector('.chat .open') || $('shell')).fontSize),
      composerFontPx:parseFloat(getComputedStyle($('draft')).fontSize),
      projectVisible:!$('projectScroll').hidden,
      route:state?.project?.route||'home',
      spaceCardCount:$('projectCards').querySelectorAll('.spaceCard').length,
      chatCardCount:$('projectCards').querySelectorAll('.projectCard:not(.spaceCard)').length,
      cardOrderIds:[...$('projectCards').children]
        .filter(x=>x.dataset.orderId).map(x=>x.dataset.orderId),
      cardOrderRevision:state?.cardOrder?.revision??-1,
      cardOrderError:state?.cardOrder?.error||'',
      dragActive:!!dragState,
      sidebarVisible:!shadow.querySelector('aside').hidden,
      renameCardButtons:$('projectCards').querySelectorAll('.cardRename').length,
      renameButtonVisible:!$('optionsTop').hidden,
      optionsMenuVisible:!$('chatOptions').hidden,
      deleteDialogVisible:!$('deleteShade').hidden,
      optionButtonCount:$('projectCards').querySelectorAll('.cardOptionsButton').length,
      expandedPreviewCount:expandedPreviews.size,
      visiblePreviewCount:$('projectCards').querySelectorAll('.previewPanel').length,
      liveWatchCount:livePreviewWatches.size,
      liveWatchedIds:[...livePreviewWatches],
      livePanelCount:$('projectCards').querySelectorAll('.previewLive').length,
      liveUpdatesApplied:livePreviewUpdatesApplied,
      liveStates:Object.entries(state?.cardPreview?.live||{}).map(([id,v])=>({
        id,completed:v.completed,status:v.status,textLength:v.text?.length||0,
        sequence:v.sequence
      })),
      previewLoadingCount:(state?.cardPreview?.pending||[]).length,
      previewRevision:state?.cardPreview?.revision??-1,
      previewStates:Object.entries(state?.cardPreview?.items||{})
        .map(([id,x])=>({id,state:x.state,messageCount:x.messages?.length||0})),
      renameDialogVisible:!$('renameShade').hidden,
      renameResultRevision:state?.rename?.revision??-1,
      historyLoading:!$('historyLoading').hidden,
      newChatWelcomeVisible:!$('empty').hidden,
      historyBlankVisible:!$('historyBlank').hidden,
      projectName:$('projectTitle').textContent,
      projectCards:$('projectCards').querySelectorAll('.projectCard').length,
      projectCardTitles:[...$('projectCards').querySelectorAll('.cardTitle')]
        .map(x=>x.textContent).slice(0,12),
      projectCardStatuses:[...$('projectCards').querySelectorAll('.cardStatus')]
        .map(x=>x.textContent).slice(0,12)
    })};
    // In dev mode scripts load from local files. Wait for every script,
    // including the workspace renderer, before requesting hydration.
    if(document.readyState==='loading')
      document.addEventListener('DOMContentLoaded',()=>send('ready'),{once:true});
    else send('ready');
  }

  let lastLibraryKey='',libraryDisplayLimit=300;
  function renderLibrary() {
    const library=state?.library || {};
    const query=($('librarySearch')?.value||'').trim().toLocaleLowerCase();
    const groups=state?.project?.projects || [];
    const projectNames=new Map(groups.map(x=>[x.id,x.name]));
    const signature=[library.revision,library.loading,library.loaded,
      library.error,library.hasMore,library.total,state?.selectedId,
      !!state?.project?.visible,query,
      groups.map(x=>x.id+':'+x.name).join(','),libraryDisplayLimit].join('|');
    if(signature===lastLibraryKey)return;
    lastLibraryKey=signature;
    const list=$('libraryChats');
    const preservedScroll=list.scrollTop;
    list.replaceChildren();
    const entries=library.chats||[];
    const matches=query?entries.filter(x=>
      (x.title||'').toLocaleLowerCase().includes(query) ||
      (projectNames.get(x.projectId)||'').toLocaleLowerCase().includes(query)
    ):entries;
    const visible=matches.slice(0,libraryDisplayLimit);
    const fragment=document.createDocumentFragment();
    for(const item of visible){
      const row=document.createElement('button');
      row.className='libraryChat'+(state.selectedId===item.id &&
        !state?.project?.visible?' active':'');
      row.type='button';row.title=item.title||'Untitled chat';
      row.addEventListener('click',()=>send('library-open',{id:item.id}));
      const title=document.createElement('span');
      title.className='libraryTitle';title.textContent=item.title||'Untitled chat';
      const secondary=document.createElement('span');
      secondary.className='libraryDetail';
      const timestamp=item.updated?new Date(item.updated):null;
      if(timestamp && !Number.isNaN(timestamp.getTime())){
        const age=document.createElement('span');
        age.textContent=timestamp.toLocaleDateString(undefined,{
          day:'numeric',month:'short',year:'numeric'});
        secondary.append(age);
      }
      if(item.projectId){
        const group=document.createElement('span');group.className='libraryProject';
        group.textContent='▣ '+(projectNames.get(item.projectId)||'Project');
        secondary.append(group);
      }
      row.append(title,secondary);fragment.append(row);
    }
    if(matches.length>visible.length){
      const more=document.createElement('button');
      more.className='libraryLoadMore';more.type='button';
      more.textContent='Show '+(matches.length-visible.length)+' more loaded chats';
      more.addEventListener('click',()=>{libraryDisplayLimit+=300;renderLibrary();});
      fragment.append(more);
    }
    if(matches.length===0){
      const empty=document.createElement('div');
      empty.className='libraryDetail';empty.style.padding='12px';
      empty.textContent=library.loading?'Loading chats…':
        query?'No matching loaded chats.':
        library.loaded?'No conversations found.':'Waiting for sign-in…';
      fragment.append(empty);
    }
    list.append(fragment);list.scrollTop=preservedScroll;
    $('libraryRefresh').disabled=!!library.loading;
    $('libraryMore').hidden=!library.hasMore;
    $('libraryMore').disabled=!!library.loading;
    $('libraryMore').textContent=library.loading?'Loading…':'Load older chats';
    $('libraryInfo').textContent=library.error||
      (library.loading?'Loading chat history…':
       query?matches.length+' matches among '+entries.length+' loaded':
       entries.length+(library.total!==null && library.total!==undefined?
         ' of '+library.total:'')+' conversations');
  }
  function closeOptions(restoreFocus=true){
    if(!activeOptions)return;
    const anchor=activeOptions.anchor;
    anchor?.setAttribute('aria-expanded','false');
    $('optionsTop').setAttribute('aria-expanded','false');
    activeOptions=null;
    $('chatOptions').hidden=true;
    if(restoreFocus&&anchor?.isConnected)anchor.focus();
  }
  function refreshOptionsAnchor(){
    if(!activeOptions)return;
    const candidate=activeOptions.header?$('optionsTop'):
      [...$('projectCards').querySelectorAll('.cardOptionsButton')]
        .find(x=>x.dataset.optionId===activeOptions.id);
    if(!candidate||candidate.hidden){
      closeOptions(false);return;
    }
    activeOptions.anchor=candidate;
    candidate.setAttribute('aria-expanded','true');
    const menu=$('chatOptions');
    const bounds=candidate.getBoundingClientRect();
    const left=Math.max(10,Math.min(bounds.right-menu.offsetWidth,
      window.innerWidth-menu.offsetWidth-10));
    const top=bounds.bottom+menu.offsetHeight+8<=window.innerHeight?
      bounds.bottom+6:Math.max(10,bounds.top-menu.offsetHeight-6);
    menu.style.left=left+'px';menu.style.top=top+'px';
  }
  function toggleOptions(anchor,id,title){
    if(renameDialog||deleteDialog||dragState||state?.rename?.busy||
       state?.delete?.busy||!isSavedChatTitle(id))return;
    if(activeOptions?.id===id&&activeOptions?.header===(anchor.id==='optionsTop')){
      closeOptions();return;
    }
    closeOptions(false);
    activeOptions={anchor,id,title:title||'Untitled conversation',
      header:anchor.id==='optionsTop'};
    $('chatOptions').hidden=false;
    refreshOptionsAnchor();
    $('optionRename').focus();
  }
  function openDelete(id,title){
    const origin=activeOptions?.anchor||shadow.activeElement;
    closeOptions(false);
    if(renameDialog||state?.delete?.busy||!isSavedChatTitle(id))return;
    deleteDialog={
      id,title,previousFocus:origin,
      openingRevision:state?.delete?.revision??-1,
      waiting:false
    };
    $('deleteTitle').textContent=title||'Untitled conversation';
    $('deleteError').textContent='';
    $('deleteError').hidden=true;
    $('deleteShade').hidden=false;
    updateDeleteUi();
    $('deleteCancel').focus();
  }
  function closeDelete(){
    if(!deleteDialog||deleteDialog.waiting||state?.delete?.busy)return;
    const previous=deleteDialog.previousFocus;
    deleteDialog=null;
    $('deleteShade').hidden=true;
    $('deleteError').hidden=true;
    if(previous?.isConnected)previous.focus();
  }
  function confirmDelete(){
    if(!deleteDialog||deleteDialog.waiting||state?.delete?.busy)return;
    deleteDialog.waiting=true;
    $('deleteError').hidden=true;
    send('delete-chat',{id:deleteDialog.id});
    updateDeleteUi();
  }
  function updateDeleteUi(){
    if(!deleteDialog)return;
    const result=state?.delete||{};
    if(result.revision>deleteDialog.openingRevision &&
       result.conversationId===deleteDialog.id){
      deleteDialog.openingRevision=result.revision;
      deleteDialog.waiting=false;
      if(result.successful){closeDelete();return;}
      $('deleteError').textContent=result.error||
        'Deletion could not be confirmed.';
      $('deleteError').hidden=false;
    }
    const busy=!!result.busy||deleteDialog.waiting;
    $('deleteCancel').disabled=busy;
    $('deleteConfirm').disabled=busy;
    $('deleteConfirm').textContent=busy?'Deleting…':'Delete conversation';
  }
  function closeRename(){
    if(!renameDialog || renameDialog.waiting || state?.rename?.busy)return;
    const previous=renameDialog.previousFocus;
    renameDialog=null;
    $('renameShade').hidden=true;
    $('renameError').hidden=true;
    if(previous?.isConnected)previous.focus();
  }
  function openRename(id,title,returnFocus=null){
    if(state?.rename?.busy || !/^[0-9a-f-]{36}$/i.test(id))return;
    renameDialog={
      id,
      previousFocus:returnFocus||shadow.activeElement,
      openingRevision:state?.rename?.revision??-1,
      waiting:false
    };
    $('renameInput').value=title||'';
    $('renameError').hidden=true;
    $('renameError').textContent='';
    $('renameShade').hidden=false;
    $('renameInput').disabled=false;
    updateRenameUi();
    $('renameInput').focus();
    $('renameInput').select();
  }
  function saveRename(){
    if(!renameDialog||renameDialog.waiting)return;
    const title=$('renameInput').value.trim();
    if(!title||title.length>180){
      $('renameError').hidden=false;
      $('renameError').textContent='Enter a title between 1 and 180 characters.';
      return;
    }
    renameDialog.waiting=true;
    $('renameError').hidden=true;
    send('rename-chat',{id:renameDialog.id,title});
    updateRenameUi();
  }
  function updateRenameUi(){
    if(!renameDialog)return;
    const result=state?.rename||{};
    if(result.revision>renameDialog.openingRevision &&
       result.conversationId===renameDialog.id){
      renameDialog.openingRevision=result.revision;
      renameDialog.waiting=false;
      if(result.successful){
        closeRename();
        return;
      }
      $('renameError').hidden=false;
      $('renameError').textContent=result.error||'Rename could not be confirmed.';
    }
    const busy=!!result.busy||renameDialog.waiting;
    $('renameInput').disabled=busy&&renameDialog.waiting;
    $('renameSave').disabled=busy||!$('renameInput').value.trim();
    $('renameCancel').disabled=busy&&renameDialog.waiting;
    $('renameSave').textContent=busy?'Saving…':'Save';
  }

  // Drag-and-drop never changes Neko project membership. The signed-in
  // website remains the authority for conversations and project folders.
  let dragState=null;
  // Expanded state is in-memory, separate from Neko's persisted card order.
  const expandedPreviews=new Set();

  const livePreviewWatches=new Set();
  let livePreviewUpdatesApplied=0;
  function syncLiveWatches(){
    const desired=new Set();
    if(state?.project?.visible && state?.project?.route!=='home'){
      const wrappers=$('projectCards')?.querySelectorAll('.cardWrap.previewOpen')||[];
      for(const wrapper of wrappers){
        const id=wrapper.dataset.orderId;
        if(isSavedChatTitle(id))desired.add(id);
      }
    }
    for(const id of [...livePreviewWatches]){
      if(!desired.has(id)){
        livePreviewWatches.delete(id);
        if(state?.cardPreview?.live && Object.hasOwn(state.cardPreview.live,id))
          delete state.cardPreview.live[id];
        send('card-preview-watch',{id,enabled:false});
      }
    }
    for(const id of desired){
      if(!livePreviewWatches.has(id)){
        livePreviewWatches.add(id);
        send('card-preview-watch',{id,enabled:true});
      }
    }
  }
  function receiveLivePreview(changes){
    if(!changes||typeof changes!=='object')return;
    if(!state?.cardPreview)return;
    const live=state.cardPreview.live||(state.cardPreview.live={});
    for(const [id,update] of Object.entries(changes)){
      if(!livePreviewWatches.has(id)||!isSavedChatTitle(id)||!update||
         update.id!==id)continue;
      if((live[id]?.sequence||0)>=(update.sequence||0))continue;
      live[id]=update;livePreviewUpdatesApplied++;
      drawLiveCardTail(id);
    }
  }
  function drawLiveCardTail(id){
    const panel=$('preview-'+id);
    if(!panel)return;
    const record=state?.cardPreview?.live?.[id];
    const old=panel.querySelector('.previewLive');
    if(!record){old?.remove();return;}
    const earlierStatus=panel.querySelector('.previewHint');
    if(earlierStatus && record.text){
      earlierStatus.textContent=
        state?.cardPreview?.items?.[id]?.state==='error'
          ?'Earlier saved messages are unavailable; live output is shown below.'
          :'Loading earlier messages; live output is shown below.';
    }
    // Live entries carry a message ID to avoid doubling a saved reply.
    // A server history fetch may already contain this same assistant
    // message. Hide that historical entry while its live version is shown.
    for(const entry of panel.querySelectorAll('.previewEntry')){
      const matching=entry.dataset.messageId &&
        entry.dataset.messageId===record.messageId;
      entry.hidden=!!matching;
    }
    let box=old;
    if(!box){
      box=document.createElement('div');
      box.className='previewLive';
      box.setAttribute('role','status');
      box.setAttribute('aria-live','off');
      // Keep the live output below preceding user/assistant messages.
      const retry=panel.querySelector('.previewRetry');
      if(retry)panel.insertBefore(box,retry);
      else panel.append(box);
    }
    let heading=box.querySelector('.previewLiveHeading');
    if(!heading){
      heading=document.createElement('div');
      heading.className='previewLiveHeading';box.append(heading);
    }
    heading.textContent=record.completed?
      (record.error?'Neko · Interrupted':'Neko · Completed'):
      'Neko · Live';
    let content=box.querySelector('.previewLiveText');
    if(!content){
      content=document.createElement('div');
      content.className='previewLiveText';box.append(content);
    }
    content.textContent=record.text||
      (record.completed?'No text in the latest response yet.':'Generating…');
    box.classList.toggle('completed',!!record.completed);
    box.classList.toggle('failed',!!record.error);
  }

  let collectionRoute='',visibleCardLimit=200;
  function orderCards(scope,items,idFor){
    const scopes=state?.cardOrder?.scopes;
    const ids=scopes&&Object.hasOwn(scopes,scope)&&Array.isArray(scopes[scope])
      ?scopes[scope]:[];
    if(!ids.length)return items;
    const rank=new Map(ids.map((id,index)=>[id,index]));
    return [...items].map((value,index)=>({value,index})).sort((a,b)=>{
      const aRank=rank.has(idFor(a.value))?rank.get(idFor(a.value)):Infinity;
      const bRank=rank.has(idFor(b.value))?rank.get(idFor(b.value)):Infinity;
      return aRank===bRank?a.index-b.index:aRank-bRank;
    }).map(x=>x.value);
  }
  function reorderCardNodes(source,target,after){
    if(!source||!target||source===target)return;
    const grid=$('projectCards');
    if(!grid||source.parentElement!==grid||target.parentElement!==grid)return;
    grid.insertBefore(source,after?target.nextSibling:target);
    send('reorder-cards',{
      scope:source.dataset.orderScope,
      source:source.dataset.orderId,
      target:target.dataset.orderId,
      after
    });
  }
  function registerReorder(element,scope,id,label){
    element.draggable=true;
    element.classList.add('reorderable');
    element.dataset.orderId=id;
    element.dataset.orderScope=scope;
    element.title=element.title||label;
    element.addEventListener('dragstart',event=>{
      if(renameDialog||deleteDialog||activeOptions||dragState||
         event.target.closest('.cardOptionsButton,.previewToggle,.previewPanel')){
        event.preventDefault();return;
      }
      dragState={element,scope,id,dropped:false};
      element.classList.add('dragging');
      if(event.dataTransfer){
        event.dataTransfer.effectAllowed='move';
        event.dataTransfer.setData('text/plain',id);
      }
    });
    element.addEventListener('dragover',event=>{
      if(!dragState||dragState.scope!==scope||dragState.element===element)return;
      event.preventDefault();
      if(event.dataTransfer)event.dataTransfer.dropEffect='move';
      for(const child of $('projectCards').children)
        child.classList.remove('dragBefore','dragAfter');
      const siblings=[...$('projectCards').children];
      const after=siblings.indexOf(dragState.element)<siblings.indexOf(element);
      element.classList.add(after?'dragAfter':'dragBefore');
    });
    element.addEventListener('drop',event=>{
      if(!dragState||dragState.scope!==scope||dragState.element===element)return;
      event.preventDefault();event.stopPropagation();
      const from=dragState.element;
      const siblings=[...$('projectCards').children];
      const after=siblings.indexOf(from)<siblings.indexOf(element);
      dragState.dropped=true;
      reorderCardNodes(from,element,after);
      for(const child of $('projectCards').children)
        child.classList.remove('dragBefore','dragAfter');
    });
    element.addEventListener('dragend',()=>{
      if(!dragState)return;
      dragState.element.classList.remove('dragging');
      for(const child of $('projectCards').children)
        child.classList.remove('dragBefore','dragAfter');
      dragState=null;
    });
    // An accessible alternative to dragging: focus a card and use
    // Alt+Arrow keys to move it one position in the visible grid.
    element.addEventListener('keydown',event=>{
      if(!event.altKey || !['ArrowUp','ArrowDown','ArrowLeft','ArrowRight']
          .includes(event.key))return;
      if(event.target.closest('.cardOptionsButton,.previewToggle,.previewRetry'))return;
      const direction=event.key==='ArrowUp'||event.key==='ArrowLeft'?-1:1;
      const siblings=[...$('projectCards').children];
      const position=siblings.indexOf(element);
      const neighbor=siblings[position+direction];
      if(!neighbor||neighbor.dataset.orderScope!==scope)return;
      event.preventDefault();
      reorderCardNodes(element,neighbor,direction>0);
      element.focus();
    });
  }
  function renderCards(){
    const p=state?.project||{},library=state?.library||{};
    const route=p.route||'home';
    const projects=p.projects||[];
    const chosen=projects.find(x=>x.id===p.selectedId);
    const home=route==='home'||(route==='project'&&!chosen);
    const unfiled=route==='unfiled';
    const project=home||unfiled?null:chosen;
    const allUnfiled=(library.chats||[]).filter(x=>!x.projectId);
    const scope=home?'spaces':unfiled?'unfiled':project.id;
    const listing=orderCards(scope,unfiled?allUnfiled:(project?(p.chats||[]):[]),x=>x.id);
    const routeKey=home?'home':(unfiled?'unfiled':'project:'+project.id);
    if(collectionRoute!==routeKey){
      collectionRoute=routeKey;
      visibleCardLimit=200;
      $('cardSearch').value='';
      $('projectScroll').scrollTop=0;
    }
    const query=$('cardSearch').value.trim().toLocaleLowerCase();
    const matches=query?listing.filter(x=>(x.title||'').toLocaleLowerCase().includes(query)
      ||(x.snippet||'').toLocaleLowerCase().includes(query)):listing;
    $('collectionControls').hidden=home;
    $('cardShowMore').hidden=home||matches.length<=visibleCardLimit;
    $('cardShowMore').textContent='Show '+Math.min(200,Math.max(0,matches.length-visibleCardLimit))+' more chats';
    $('cardLoadMore').hidden=!unfiled||!library.hasMore;
    $('cardLoadMore').disabled=!!library.loading;
    $('cardLoadMore').textContent=library.loading?'Loading more…':'Load older conversations';
    $('projectKicker').textContent=home?'SPACES':unfiled?'YOUR CONVERSATIONS':'PROJECT';
    $('projectTitle').textContent=home?'Your spaces':(unfiled?'Chats':project.name);
    $('projectDescription').textContent=home
      ?'Choose a project, or browse conversations outside projects.'
      :unfiled?'Conversations outside projects.'
      :(project.description||'Conversations in this project.');
    $('projectMeta').textContent=home
      ?projects.length+' projects · '+allUnfiled.length+' chats outside projects'
      :matches.length+(matches.length===1?' conversation':' conversations')+
        (query?' matching':'');
    $('projectMessage').textContent=state?.cardOrder?.error||
      (unfiled?library.error:p.error)||state.error||
      (p.refreshingCatalog?'Refreshing spaces…':
       project&&p.loading?'Loading project conversations…':
       unfiled&&library.loading?'Loading conversations…':'');
    const cards=$('projectCards');cards.replaceChildren();
    if(home){
      const spaces=orderCards('spaces',[{id:null,name:'Chats',description:'Conversations outside projects',
        count:allUnfiled.length,icon:'☷'},...projects.map(x=>({
          id:x.id,name:x.name,description:x.description||'Project',
          count:(library.chats||[]).filter(y=>y.projectId===x.id).length,
          icon:'▣'}))],x=>x.id||'__unfiled__');
      for(const space of spaces){
        const card=document.createElement('button');
        card.type='button';card.className='projectCard spaceCard';
        card.title='Open '+space.name;
        registerReorder(card,'spaces',space.id||'__unfiled__',space.name);
        card.addEventListener('click',()=>send(space.id?'project':'unfiled',
          space.id?{id:space.id}:{}));
        const icon=document.createElement('span');
        icon.className='cardIcon';icon.textContent=space.icon;
        const title=document.createElement('div');
        title.className='cardTitle';title.textContent=space.name;
        const description=document.createElement('div');
        description.className='cardSnippet';description.textContent=space.description;
        const bottom=document.createElement('div');bottom.className='cardBottom';
        const count=document.createElement('span');
        count.textContent=space.count+(space.count===1?' loaded chat':' loaded chats');
        const open=document.createElement('span');
        open.className='cardOpen';open.textContent='Explore →';
        bottom.append(count,open);
        card.append(icon,title,description,bottom);cards.append(card);
      }
      return;
    }
    if(!matches.length&&!p.loading&&!library.loading){
      const empty=document.createElement('div');empty.id='projectEmpty';
      empty.textContent=query?'No conversations match this search.':
        'No conversations here yet.';
      cards.append(empty);
    }
    for(const chat of matches.slice(0,visibleCardLimit)){
      const wrapper=document.createElement('div');wrapper.className='cardWrap';
      wrapper.tabIndex=0;
      wrapper.setAttribute('role','group');
      wrapper.setAttribute('aria-label','Reorder '+(chat.title||'conversation'));
      registerReorder(wrapper,scope,chat.id,chat.title||'conversation');
      const card=document.createElement('button');
      card.className='projectCard';card.type='button';
      card.title='Open '+(chat.title||'conversation');
      card.addEventListener('click',()=>send(unfiled?'library-open':'project-open',{id:chat.id}));
      const upper=document.createElement('div');upper.className='cardTop';
      const icon=document.createElement('span');
      icon.className='cardIcon';icon.textContent='☷';upper.append(icon);
      if(!unfiled){
        const status=document.createElement('span');status.className='cardStatus';
        status.textContent=chat.status==='Responding'?'Responding':
          chat.status==='Completed'?'Completed':'Status unknown';
        if(chat.status==='Responding')status.classList.add('responding');
        if(chat.status==='Completed')status.classList.add('completed');
        upper.append(status);
      }
      const title=document.createElement('div');title.className='cardTitle';
      title.textContent=chat.title||'Untitled conversation';
      const snippet=document.createElement('div');snippet.className='cardSnippet';
      snippet.textContent=chat.snippet||'Open to read this conversation.';
      const bottom=document.createElement('div');bottom.className='cardBottom';
      const date=document.createElement('span');
      const updated=chat.updated?new Date(chat.updated):null;
      date.textContent=updated&&!Number.isNaN(updated.getTime())
        ?'Updated '+updated.toLocaleDateString(undefined,{
          day:'numeric',month:'short',year:'numeric'}):'Date unavailable';
      const open=document.createElement('span');open.className='cardOpen';
      open.textContent='Open chat →';bottom.append(date,open);
      card.append(upper,title,snippet,bottom);
      const options=document.createElement('button');options.type='button';
      options.className='cardOptionsButton';options.textContent='⋯';
      options.setAttribute('aria-label','Options for '+(chat.title||'conversation'));
      options.setAttribute('aria-haspopup','menu');
      options.setAttribute('aria-expanded','false');
      options.title='Conversation options';
      options.disabled=!!state?.rename?.busy||!!state?.delete?.busy;
      options.dataset.optionId=chat.id;
      options.draggable=false;
      options.addEventListener('dragstart',event=>{
        event.preventDefault();event.stopPropagation();
      });
      options.addEventListener('click',event=>{
        event.stopPropagation();
        toggleOptions(options,chat.id,chat.title);
      });
      const expanded=expandedPreviews.has(chat.id);
      const toggle=document.createElement('button');
      toggle.type='button';toggle.className='previewToggle';
      toggle.draggable=false;
      toggle.setAttribute('aria-label',(expanded?'Hide':'Show')+
        ' last lines for '+(chat.title||'conversation'));
      toggle.setAttribute('aria-expanded',String(expanded));
      toggle.setAttribute('aria-controls','preview-'+chat.id);
      toggle.textContent=expanded?'⌃ Hide preview':'⌄ Show last lines';
      toggle.addEventListener('dragstart',event=>{
        event.preventDefault();event.stopPropagation();
      });
      toggle.addEventListener('click',event=>{
        event.stopPropagation();
        if(expandedPreviews.has(chat.id))expandedPreviews.delete(chat.id);
        else {
          expandedPreviews.add(chat.id);
          const preview=state?.cardPreview||{};
          if(!preview.items?.[chat.id]&&!preview.pending?.includes(chat.id))
            send('card-preview',{id:chat.id});
        }
        renderCards();
        syncLiveWatches();
      });
      wrapper.classList.toggle('previewOpen',expanded);
      const pin=document.createElement('button');
      pin.type='button';pin.className='workspacePin';
      const pinned=!!state?.panelWorkspace?.panels?.some(x=>x.id===chat.id);
      pin.textContent=pinned?'✓ In workspace':'＋ Add to workspace';
      pin.disabled=pinned;
      pin.addEventListener('click',event=>{
        event.stopPropagation();
        send('panel-add',{id:chat.id});
      });
      wrapper.append(card,options,toggle,pin);
      if(expanded){
        const panel=document.createElement('div');
        panel.id='preview-'+chat.id;
        panel.className='previewPanel';panel.setAttribute('role','region');
        panel.setAttribute('aria-label','Recent conversation text');
        panel.draggable=false;
        panel.addEventListener('dragstart',event=>{
          event.preventDefault();event.stopPropagation();
        });
        const preview=state?.cardPreview||{};
        const info=preview.items?.[chat.id];
        const loading=preview.pending?.includes(chat.id);
        if(loading||!info){
          const hint=document.createElement('div');
          hint.className='previewHint';
          hint.setAttribute('role','status');
          hint.textContent='Loading recent messages…';
          panel.append(hint);
        }else if(info.state==='ready'&&info.messages?.length){
          for(const message of info.messages){
            const entry=document.createElement('div');
            entry.className='previewEntry';
            entry.dataset.messageId=message.messageId||'';
            const role=document.createElement('div');role.className='previewRole';
            role.textContent=message.role;
            const text=document.createElement('div');text.className='previewText';
            text.textContent=message.text;
            entry.append(role,text);panel.append(entry);
          }
          const retry=document.createElement('button');retry.type='button';
          retry.className='previewRetry';retry.textContent='↻ Refresh preview';
          retry.addEventListener('click',event=>{
            event.stopPropagation();
            send('card-preview',{id:chat.id,retry:true});
          });
          panel.append(retry);
        }else{
          const hint=document.createElement('div');
          hint.className='previewHint';
          hint.setAttribute('role','status');
          hint.textContent=info.error||'No recent text messages to preview.';
          panel.append(hint);
          if(info.state==='error'||info.state==='ready'){
            const retry=document.createElement('button');retry.type='button';
            retry.className='previewRetry';
            retry.textContent=info.state==='error'?'Try again':'↻ Refresh preview';
            retry.addEventListener('click',event=>{
              event.stopPropagation();
              send('card-preview',{id:chat.id,retry:true});
            });
            panel.append(retry);
          }
        }
        wrapper.append(panel);
      }
      cards.append(wrapper);
      if(expanded)drawLiveCardTail(chat.id);
    }
  }

  function pendingSingleNode(id,pending){
    const bubble=document.createElement('article');
    bubble.className='message user pending';
    bubble.dataset.pendingId=id||'new';
    const heading=document.createElement('div');
    heading.className='speaker';heading.textContent='You';
    const content=document.createElement('div');content.className='content';
    if(window.__nekoMarkdown?.ready)
      window.__nekoMarkdown.render(content,pending.text);
    else content.textContent=pending.text;
    const status=document.createElement('div');
    status.className='pendingStatus';
    status.textContent=window.__nekoOutgoing.labels[pending.stage];
    status.classList.toggle('warn',
      pending.stage==='uncertain'||pending.stage==='failed');
    bubble.append(heading,content,status);
    if(pending.stage==='failed'){
      const restore=document.createElement('button');
      restore.textContent='Restore to composer';
      restore.addEventListener('click',()=>{
        const draft=$('draft');
        draft.value=draft.value?
          draft.value+(draft.value.endsWith('\n')?'':'\n')+pending.text:
          pending.text;
        singleOutgoing.delete(id);
        singleSuppressedDraft.delete(id);
        submitting=false;
        send('draft',{id,text:draft.value});
        draft.focus();
        bubble.remove();
        $('submit').disabled=!draft.value.trim();
      });
      bubble.append(restore);
    }
    return bubble;
  }
  function showSinglePendingImmediately(id){
    const pending=singleOutgoing.get(id);
    if(!pending||!shadow)return;
    const messages=$('messages');
    if(!messages)return;
    messages.append(pendingSingleNode(id,pending));
    messages.hidden=false;
    $('empty').hidden=true;
    singleFollowTail=true;
    followSingleLatest($('messageScroll'));
  }

  function renderSingleActivity(item){
    const kind=item.kind||'tool_activity';
    const activity=document.createElement('article');
    activity.className='message activity '+kind;
    const detail=document.createElement('details');
    if(item.id && singleActivityOpen.has(item.id))detail.open=true;
    detail.addEventListener('toggle',()=>{
      if(!item.id)return;
      if(detail.open)singleActivityOpen.add(item.id);
      else singleActivityOpen.delete(item.id);
    });
    const title=document.createElement('summary');
    title.textContent=(kind==='thinking'?'◈ ':kind==='tool_call'?'⌘ ':'▣ ')+
      (item.title||kind.replace('_',' '));
    const body=document.createElement('div');
    body.className='activityContent';
    if(window.__nekoMarkdown?.ready)
      window.__nekoMarkdown.render(body,item.text||'No text provided.');
    else body.textContent=item.text||'No text provided.';
    detail.append(title,body);activity.append(detail);
    return activity;
  }

  function receive(next) {
    if (!next || next.version < lastVersion) return;
    lastVersion = next.version;
    const previousId = selected;
    const previousProject=!!state?.project?.visible;
    const previousWorkspace=!!state?.panelWorkspace?.visible;
    // Large chat lists are sent only when their revision changes, not on
    // every streaming message. Retain the previous list between updates.
    if(next.library && !Array.isArray(next.library.chats))
      next.library.chats=state?.library?.revision===next.library.revision
        ?(state.library.chats||[]):[];
    const previousLive=state?.cardPreview?.live||Object.create(null);
    state = next; selected = next.selectedId;
    if(!next.canvasOnly){
      const top=shadow?.querySelector('.top');
      if(top)top.hidden=false;
      const aside=shadow?.querySelector('aside');
      if(aside)aside.hidden=false;
    }
    if(next.canvasOnly){
      // The canvas is Neko's entire visible application. The legacy Spaces,
      // tab and single-chat renderers are retained internally for transport
      // compatibility, but are never mounted or refreshed on this route.
      const top=shadow?.querySelector('.top');
      if(top)top.hidden=true;
      const aside=shadow?.querySelector('aside');
      if(aside)aside.hidden=true;
      window.__nekoWorkspace?.receive(next);
      return;
    }
    if(previousId!==selected){
      singleToolGroupCache.clear();
      singleActivityOpen.clear();
    }
    if(previousId!==selected &&
       typeof previousId==='string' && previousId.startsWith('local-draft-') &&
       selected && singleOutgoing.has(previousId) &&
       !singleOutgoing.has(selected)){
      const outgoing=singleOutgoing.get(previousId);
      singleOutgoing.delete(previousId);
      outgoing.id=selected;
      singleOutgoing.set(selected,outgoing);
      if(singleSuppressedDraft.has(previousId)){
        singleSuppressedDraft.set(selected,singleSuppressedDraft.get(previousId));
        singleSuppressedDraft.delete(previousId);
      }
    }
    submitting=!!singleOutgoing.get(selected);

    // A full shell snapshot may race with a smaller live-tail update.
    // Keep the newest sequence per conversation instead of reverting a
    // generating card to an older text fragment.
    if(next.cardPreview){
      const incoming=Object.assign(Object.create(null),next.cardPreview.live||{});
      for(const [id,tail] of Object.entries(previousLive))
        if(!Object.hasOwn(incoming,id) ||
          (tail.sequence||0)>(incoming[id].sequence||0))
          incoming[id]=tail;
      next.cardPreview.live=incoming;
    }
    const p=next.project || {};
    renderLibrary();
    $('projectSync').disabled=!!p.refreshingCatalog;
    $('refresh').disabled=!!p.visible && !!p.refreshingCatalog;
    $('refresh').textContent='↻ Refresh';
    $('home').hidden=p.visible&&(p.route||'home')==='home';
    $('home').textContent=p.visible?'← Spaces':(p.route==='project'?'← Project':p.route==='unfiled'?'← Chats':'← Spaces');
    const projectsNav=$('projects');
    projectsNav.replaceChildren();
    const overview=document.createElement('button');
    overview.className='projectLink'+(p.visible&&!p.selectedId?' active':'');
    overview.type='button';overview.textContent='▦  Project dashboard';
    overview.addEventListener('click',()=>send('projects'));
    projectsNav.append(overview);
    for(const project of p.projects || []){
      const button=document.createElement('button');
      button.type='button';
      button.className='projectLink'+(p.visible&&project.id===p.selectedId?' active':'');
      const ico=document.createElement('span');ico.className='folder';ico.textContent='▣';
      const name=document.createElement('span');name.className='projectName';
      name.textContent=project.name;
      button.title=project.name;button.append(ico,name);
      button.addEventListener('click',()=>send('project',{id:project.id}));
      projectsNav.append(button);
    }
    // Full-width card browser replaces the old sidebar.
    const showProject=!!p.visible;
    $('projectScroll').hidden=!showProject;
    $('messageScroll').hidden=showProject;
    shadow.querySelector('.composeArea').hidden=showProject;
    if(showProject)renderCards();
    syncLiveWatches();
    $('optionsTop').hidden=showProject||!isSavedChatTitle(next.selectedId);
    updateRenameUi();
    updateDeleteUi();
    if(activeOptions)refreshOptionsAnchor();

    const nav = $('chats');
    nav.replaceChildren();
    for (const item of next.tabs || []) {
      const row = document.createElement('div');
      // Multiple tabs can reference the same conversation. Only the
      // selected *tab instance*, not every matching conversation ID, is active.
      row.className = 'chat' + (item.index === next.selectedIndex ? ' active':'');
      const open = document.createElement('button');
      open.type='button';open.className='open';open.textContent=item.title || 'New chat';
      open.title=item.title || 'New chat';
      open.addEventListener('click',()=>send('select',{index:item.index}));
      const close=document.createElement('button');
      close.type='button';close.className='close';close.textContent='×';close.title='Close tab';
      close.setAttribute('aria-label','Close '+open.textContent);
      close.addEventListener('click',()=>send('close',{index:item.index}));
      row.append(open,close); nav.append(row);
    }
    const workspace=$('workspaces');
    workspace.hidden=(next.workspaces || []).length < 2;
    workspace.replaceChildren();
    (next.workspaces || []).forEach((name,i)=>{
      const o=document.createElement('option');o.value=i;o.textContent=name;
      workspace.append(o);
    });
    workspace.value=String(next.workspaceIndex || 0);
    const pane=next.pane || {};
    const draft=$('draft');
    const isSavedChat=typeof next.selectedId==='string' &&
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(next.selectedId);
    const paneMatches=!!next.selectedId && pane.id===next.selectedId;
    const loadingChat=!showProject && isSavedChat && (!paneMatches || !!pane.loading);
    $('title').textContent=showProject?'':
      (loadingChat ? (next.selectedTitle || pane.title || 'Loading conversation…') :
       (paneMatches ? (pane.title || next.selectedTitle) : next.selectedTitle)) || (showProject?'':'New chat');
    const scroll=$('messageScroll');
    if(previousId!==selected||((previousProject||previousWorkspace)&&
       !showProject&&!next.panelWorkspace?.visible)){
      singleNeedsInitialTail=true;
      singleFollowTail=true;
    }
    if(!scroll.__nekoTailListener){
      scroll.__nekoTailListener=true;
      scroll.addEventListener('scroll',()=>{
        if(!singleAutoScrolling)
          singleFollowTail=scroll.scrollHeight-scroll.scrollTop-scroll.clientHeight<=150;
      });
      scroll.addEventListener('wheel',event=>{
        if(event.deltaY<0)singleFollowTail=false;
      },{passive:true});
    }
    const messages=$('messages');
    messages.replaceChildren();
    const outgoing=paneMatches?singleOutgoing.get(selected):null;
    if(outgoing){
      const result=window.__nekoOutgoing.reconcile(outgoing,
        pane.messages||[],{
          sending:!!pane.sending,uncertain:!!pane.uncertain,
          status:pane.status||'',
          phase:/^Not sent:|^Send failed:/i.test(pane.status||'')
            ?'send_failed':''
        });
      if(result.event==='confirmed')singleOutgoing.delete(selected);
    }
    if(singleSuppressedDraft.has(selected) &&
       !singleOutgoing.has(selected) && pane.draft==='')
      singleSuppressedDraft.delete(selected);
    const pending=paneMatches?singleOutgoing.get(selected):null;
    submitting=!!pending && pending.stage!=='failed';
    // Project overview never builds hidden conversation message nodes.
    // Long chats may contain hundreds of messages and large text blocks.
    const records=showProject||!paneMatches||loadingChat?[]:(pane.messages || []);
    let pendingPlaced=false;
    for(const block of window.__nekoToolGroups.sequences(records)){
      if(pending&&!pendingPlaced){
        const entries=block.type==='tools'?block.items:[block.item];
        if(entries.some(item=>item.role!=='user' &&
            !pending.seenIds.has(item.id))){
          messages.append(pendingSingleNode(selected,pending));
          pendingPlaced=true;
        }
      }
      if(block.type==='tools'){
        messages.append(window.__nekoToolGroups.disclosure(
          document,block.items,renderSingleActivity,{
            cache:singleToolGroupCache,
            wrapperClass:'message activity tool_group',
            contentClass:'toolGroupContent'
          }));
        continue;
      }
      const item=block.item;
      const kind=item.kind||'message';
      if(kind==='thinking'||kind==='media'){
        messages.append(renderSingleActivity(item));
        continue;
      }
      if(!item.text?.trim())continue;
      const message=document.createElement('article');
      message.className='message '+(item.role==='user'?'user':'assistant');
      const heading=document.createElement('div');
      heading.className='speaker';heading.textContent=item.role==='user'?'You':'Neko';
      const content=document.createElement('div');
      content.className='content';
      if(window.__nekoMarkdown?.ready)
        window.__nekoMarkdown.render(content,item.text);
      else content.textContent=item.text;
      message.append(heading,content);
      messages.append(message);
    }
    if(pending&&!pendingPlaced&&!showProject)
      messages.append(pendingSingleNode(selected,pending));
    // A saved chat awaiting history is never an empty New chat draft.
    $('historyLoading').hidden=!loadingChat;
    const blankHistory=!showProject && isSavedChat && !loadingChat && records.length===0;
    $('historyBlank').hidden=!blankHistory;
    $('historyBlankTitle').textContent=paneMatches && !pane.historyLoaded
      ?'Conversation history unavailable':'No messages to show';
    $('historyBlankHint').textContent=paneMatches && !pane.historyLoaded
      ?(pane.status || 'Use Refresh to try again.'):
       'This conversation has no saved text messages.';
    $('empty').hidden=showProject || isSavedChat || records.length>0 || !!pending;
    messages.hidden=loadingChat || (records.length===0 && !pending);
    if(!showProject&&!next.panelWorkspace?.visible&&!loadingChat&&
       (records.length||pending)){
      if(singleNeedsInitialTail)singleNeedsInitialTail=false;
      if(singleFollowTail)followSingleLatest(scroll);
    }
    if((previousId!==next.selectedId || shadow.activeElement!==draft) &&
       !pending && !singleSuppressedDraft.has(selected)){
      draft.value=paneMatches?(pane.draft || ''):'';
      draft.style.height='auto';
      draft.style.height=Math.min(260,Math.max(60,draft.scrollHeight))+'px';
    }
    const status=paneMatches?(pane.status || ''):'';
    const warn=paneMatches && (!!pane.uncertain ||
      /Not sent|failed|unavailable|uncertain|error/i.test(status));
    let notice='';
    if (next.error) notice=next.error;
    else if (warn) notice=status;
    else if (pane.sending) notice='Sending message…';
    else if (pane.generating) notice='Neko is responding…';
    else if (loadingChat || /Loading history|initializing/i.test(status)) notice='Loading conversation…';
    $('notice').textContent=notice;
    $('notice').classList.toggle('warn',warn || !!next.error);
    $('submit').disabled=!draft.value.trim()||!!pane.sending||!!pane.uncertain||
      !!pending||loadingChat||!paneMatches;
    if(hotSingleScroll && hotSingleScroll.id===selected){
      if(!pending && !draft.value && hotSingleScroll.draft)
        draft.value=hotSingleScroll.draft;
      if(!singleFollowTail){
        scroll.scrollTop=hotSingleScroll.scroll;
        const position=hotSingleScroll.scroll;
        requestAnimationFrame(()=>{if(scroll.isConnected)scroll.scrollTop=position;});
      }
      hotSingleScroll=null;
    }
    $('stop').hidden=!pane.generating || !!pane.isDraft || !paneMatches;
    $('connection').textContent=next.dispatcherReady ? 'Connected' : 'Connecting';
    window.__nekoWorkspace?.receive(next);
  }
  if (document.documentElement) mount();
  else document.addEventListener('DOMContentLoaded',mount,{once:true});
})();