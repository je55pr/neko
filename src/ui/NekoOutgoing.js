(() => {
  if(location.origin!=='https://neko.internal')return;
  // Presentation only: never dispatch a request, determine transport success,
  // or forget a submitted prompt on an uncertain result.
  function create(id,text,history,attachmentNames=[]) {
    return {
      id,text,attachmentNames:[...attachmentNames],
      seenIds:new Set((history||[]).map(x=>x.id)),
      stage:'sending',sawSending:false
    };
  }
  function reconcile(item,history,state) {
    if(!item)return {item:null,event:'none'};
    // A fresh server-assigned user message is proof, unlike the presence of a
    // previous message with identical text or the start of model output.
    const sameText=(a,b)=>typeof a==='string' && typeof b==='string' &&
      a.replace(/\r\n/g,'\n').trim()===b.replace(/\r\n/g,'\n').trim();
    const hasAttachments=(item.attachmentNames?.length||0)>0;
    // Attachment markers and filenames are not unique evidence of a send.
    // An attachment submission must be tied to its own server user-message ID;
    // without that ID, wait for accepted current-turn completion evidence.
    const confirmed=(history||[]).some(msg=>msg.role==='user' &&
      msg.kind!=='local_user' &&
      !item.seenIds.has(msg.id) && !!msg.id &&
      (hasAttachments
        ?!!item.serverUserMessageId && msg.id===item.serverUserMessageId
        :sameText(msg.text,item.text)));
    if(confirmed)return {item:null,event:'confirmed'};
    // The backend sets history_confirmed only after finding a NEW server
    // user message, distinct from every ID captured before the send.
    // That positive evidence can also resolve a stuck uncertain bubble even
    // when this panel's separate display-history GET is throttled.
    if(state?.status==='history_confirmed' &&
       !hasAttachments &&
       (item.sawSending || item.stage==='uncertain') &&
       !state?.sending && !state?.uncertain)
      return {item:null,event:'confirmed'};
    // A *new, saved* assistant reply plus a terminal backend state is also
    // proof that the previous turn has finished. Some history projections
    // omit the matching user node for longer than the assistant reply.
    // Never use an old reply, a live-only response, or an uncertain send.
    const terminal=state?.phase==='completed' &&
      !state?.sending && !state?.generating && !state?.uncertain;
    const newSavedReply=terminal && item.stage==='syncing' &&
      (history||[]).some(msg=>msg.role==='assistant' &&
        msg.kind==='message' && msg.id && !item.seenIds.has(msg.id) &&
        typeof msg.text==='string' && msg.text.trim() &&
        /^finished_/.test(msg.status||''));
    if(newSavedReply)return {item:null,event:'confirmed'};
    // History GETs can be throttled after a successful first-party POST.
    // HTTP 200/event-stream acceptance plus a NEW terminal SSE message from
    // this conversation is positive evidence for this turn. Release the
    // local bubble without requiring another history GET; never infer it
    // from a previous response, a running stream or an uncertain submit.
    const acceptedSse=terminal && state?.status==='sse_accepted' &&
      state?.live?.completed===true && !state.live.error &&
      !!state.live.messageId &&
      state.live.messageId!==item.priorLiveId;
    if(acceptedSse)return {item:null,event:'confirmed'};
    // A verified HTTP 200/SSE-accepted prompt plus a separate authenticated
    // server status of COMPLETE also ends the accepted outgoing bubble even
    // if the final SSE assistant message was lost. Do NOT infer this from
    // a Stop button press or cancel Promise alone.
    const verifiedStop=terminal && item.stage==='syncing' &&
      state?.status==='sse_accepted' &&
      state?.live?.completed===true &&
      state.live.error==='server_complete_final_pending' &&
      !!item.priorLiveId && !!state.live.messageId &&
      state.live.messageId!==item.priorLiveId;
    if(verifiedStop)return {item:null,event:'confirmed'};
    if(state?.uncertain){
      item.stage='uncertain';return {item,event:'uncertain'};
    }
    if(state?.sending){item.sawSending=true;item.stage='sending';}
    const result=state?.status||'';
    if(result==='sse_accepted'||result==='history_confirmed'||
       /^SSE accepted\b|^Message sent\b/i.test(result))item.stage='syncing';
    const definiteFailure=state?.phase==='send_failed' ||
      /^Not sent:|^Send failed:|^send_failed:/.test(result);
    if(definiteFailure && !state?.sending &&
       (state?.phase==='send_failed' || item.sawSending || !!result)){
      item.stage='failed';return {item,event:'failed'};
    }
    // Do not infer success from a streaming response: it could be an old
    // response or a response for another request.
    return {item,event:'pending'};
  }
  const labels={
    sending:'Sending…',syncing:'',
    uncertain:'Delivery unconfirmed · check history before retrying',
    failed:'Not sent · restore your message'
  };
  window.__nekoOutgoing={create,reconcile,labels};
})();