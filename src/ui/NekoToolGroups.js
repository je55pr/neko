(() => {
  if(location.origin!=='https://neko.internal')return;
  const TOOL_KINDS=new Set(['tool_call','tool_result','tool_activity']);
  const isTool=item=>TOOL_KINDS.has(item?.kind);
  // Group by *visible* chronology. Empty assistant/status records and blank
  // progress events have no rendered node, so they must not split otherwise
  // adjacent tool calls into multiple disclosures.
  // Preserve user-turn boundaries even when an attachment has no text.
  const isVisible=item=>{
    if(!item)return false;
    if(isTool(item))return true;
    const kind=item.kind||'message';
    if(kind==='thinking'||kind==='media')return true;
    if(item.role==='user')return true;
    return typeof item.text==='string' && !!item.text.trim();
  };
  // Real progress, user messages, thinking and media still separate runs.
  function sequences(items) {
    const result=[];
    let run=[];
    const flush=()=>{
      if(run.length){result.push({type:'tools',items:run});run=[];}
    };
    for(const item of items||[]){
      if(!isVisible(item))continue;
      if(isTool(item)){run.push(item);continue;}
      flush();
      result.push({type:'item',item});
    }
    flush();
    return result;
  }
  const label=n=>n+' tool '+(n===1?'event':'events');
  // Existing groups are updated in place so an opened expander remains open
  // when new SSE activity arrives. Text is always assigned via textContent.
  function disclosure(document,entries,renderChild,options={}) {
    if(entries.length<2 && !options.forceGroup)return renderChild(entries[0],0);
    const key=String(entries[0]?.id||'')||'unknown';
    const cached=options.cache?.get(key);
    const wrapper=cached?.wrapper||document.createElement('details');
    const summary=cached?.summary||document.createElement('summary');
    const content=cached?.content||document.createElement('div');
    wrapper.className=options.wrapperClass||'nekoToolGroup';
    content.className=options.contentClass||'nekoToolGroupContent';
    summary.textContent=label(entries.length);
    // Re-render changing text while preserving expanded details by event ID.
    const previous=cached?.childrenById;
    const children=entries.map((entry,index)=>{
      const node=renderChild(entry,index);
      const old=previous?.get(String(entry.id));
      if(old && node){
        if(old.tagName==='DETAILS' && node.tagName==='DETAILS')
          node.open=old.open;
        const oldDetails=old.querySelectorAll?.('details')||[];
        const newDetails=node.querySelectorAll?.('details')||[];
        for(let i=0;i<Math.min(oldDetails.length,newDetails.length);i++)
          newDetails[i].open=oldDetails[i].open;
      }
      return node;
    });
    content.replaceChildren(...children);
    if(!cached)wrapper.append(summary,content);
    const childrenById=new Map();
    entries.forEach((entry,index)=>{
      if(entry.id!=null)childrenById.set(String(entry.id),children[index]);
    });
    options.cache?.set(key,{wrapper,summary,content,childrenById});
    return wrapper;
  }
  window.__nekoToolGroups={isTool,sequences,label,disclosure};
})();