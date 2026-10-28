(() => {
  if(location.origin!=='https://neko.internal')return;
  // All HTML comes from MarkdownIt's html:false renderer, never from chat text.
  // HTML blocks/scripts in model output stay escaped. Images never auto-fetch.
  // Linkify plain URLs (including conversation links) in normal prose, while
  // MarkdownIt's inline-code/fenced-code rules keep code examples untouched.
  const md=typeof window.markdownit==='function'
    ?window.markdownit({
        html:false,breaks:true,linkify:true,typographer:false
      }):null;
  if(md){
    // Keep standard Markdown rendering independent of the message provider.
    md.validateLink=(url)=>/^(https?:\/\/|mailto:)/i.test(url);
    const esc=md.utils.escapeHtml;
    md.renderer.rules.image=(tokens,index)=>{
      const token=tokens[index];
      const alt=token.content||'Image';
      return '<span class="nekoMediaPlaceholder" role="note">▧ Image: '+
        esc(alt.slice(0,200))+' (preview unavailable)</span>';
    };
    md.renderer.rules.link_open=(tokens,index,options,env,self)=>{
      const token=tokens[index];
      const href=token.attrGet('href')||'';
      if(!md.validateLink(href))return '';
      token.attrSet('target','_blank');
      token.attrSet('rel','noopener noreferrer');
      return self.renderToken(tokens,index,options);
    };
    const linkClose=md.renderer.rules.link_close;
    md.renderer.rules.link_close=(tokens,index,options,env,self)=>
      linkClose?linkClose(tokens,index,options,env,self):self.renderToken(tokens,index,options);
    // Common task checkboxes, displayed but not interactive edits to the service.
    md.core.ruler.after('inline','neko_task_labels',state=>{
      for(const item of state.tokens){
        if(item.type!=='inline'||!item.children?.length)continue;
        const text=item.children[0];
        if(text?.type!=='text')continue;
        const matched=/^\[([ xX])\]\s+/.exec(text.content);
        if(!matched)continue;
        const prev=state.tokens[state.tokens.indexOf(item)-1];
        if(prev?.type!=='paragraph_open')continue;
        text.content=text.content.slice(matched[0].length);
        const check=new state.Token('html_inline','',0);
        check.content='<input type="checkbox" tabindex="-1" disabled'+
          (matched[1]!==' '?' checked':'')+' aria-label="Task item">';
        item.children.unshift(check);
      }
    });
  }
  const css=document.createElement('style');
  css.textContent=`
    .nekoMarkdown{line-height:1.52;overflow-wrap:anywhere;word-break:normal;white-space:normal}
    .nekoMarkdown p{margin:.1em 0 .68em}
    .nekoMarkdown p:last-child{margin-bottom:0}
    .nekoMarkdown h1,.nekoMarkdown h2,.nekoMarkdown h3,.nekoMarkdown h4{font-weight:730;line-height:1.25;margin:1em 0 .5em}
    .nekoMarkdown h1{font-size:1.48em}.nekoMarkdown h2{font-size:1.3em}
    .nekoMarkdown h3{font-size:1.14em}.nekoMarkdown h4{font-size:1.04em}
    .nekoMarkdown strong{font-weight:750;color:#f3faf6}
    .nekoMarkdown em{font-style:italic}
    .nekoMarkdown ul,.nekoMarkdown ol{margin:.25em 0 .75em;padding-left:1.55em}
    .nekoMarkdown li{margin:.17em 0}.nekoMarkdown li>p{display:inline}
    .nekoMarkdown pre{overflow:auto;max-width:100%;margin:.45em 0 .8em;padding:12px 13px;
      border:1px solid #3a4d56;border-radius:9px;background:#0a151a;white-space:pre}
    .nekoMarkdown code{font-family:Consolas,'Cascadia Code',monospace;font-size:.89em;
      border-radius:4px;background:#26353c;padding:.12em .3em;white-space:break-spaces}
    .nekoMarkdown pre code{background:transparent;padding:0;white-space:pre;word-break:normal}
    .nekoMarkdown blockquote{border-left:3px solid #77baa6;padding:.2em .85em;margin:.5em 0;
      color:#b3ccc7;background:#1c3031}
    .nekoMarkdown a{color:#97e0ce;text-decoration:underline;text-underline-offset:2px}
    .nekoMarkdown a:hover{color:#d4ffec}
    .nekoMarkdown hr{border:0;border-top:1px solid #42605d;margin:1em 0}
    .nekoMarkdown table{display:block;overflow-x:auto;border-collapse:collapse;
      margin:.55em 0;max-width:100%}
    .nekoMarkdown th,.nekoMarkdown td{padding:7px 10px;border:1px solid #42595d;text-align:left}
    .nekoMarkdown th{background:#203635;font-weight:720}
    .nekoMarkdown input[type=checkbox]{accent-color:#94dec2;margin-right:.35em;pointer-events:none}
    .nekoMediaPlaceholder{display:inline-block;padding:4px 8px;border:1px dashed #58716d;
      border-radius:7px;color:#a8c3bc;font-size:.94em}
    .nekoMarkdown .nekoDiagrams{padding:6px}
  `;
  // Styles live in the local frontend shadow root, not the source page.
  const insertCss=()=>{
    const shadow=document.getElementById('neko-shell-surface')?.shadowRoot;
    if(shadow&&!shadow.querySelector('#nekoMarkdownCss')){
      const cloned=css.cloneNode(true);cloned.id='nekoMarkdownCss';shadow.append(cloned);
    }
  };
  const fill=(element,text)=>{
    insertCss();
    const value=typeof text==='string'?text:'';
    const signature=value;
    if(element.__nekoRendered===signature)return;
    element.__nekoRendered=signature;
    element.classList.add('nekoMarkdown');
    if(!md){element.textContent=value;return;}
    element.innerHTML=md.render(value.slice(0,150000));
    // Treat links as explicit user interactions, not WebView navigations.
    for(const a of element.querySelectorAll('a[href]')){
      a.addEventListener('click',event=>{
        const href=a.getAttribute('href')||'';
        if(!md.validateLink(href)){event.preventDefault();return;}
        event.preventDefault();
        window.__nekoShell?.post?.('open-external-url',{url:href});
      });
    }
  };
  window.__nekoMarkdown={render:fill,ready:!!md};
})();
