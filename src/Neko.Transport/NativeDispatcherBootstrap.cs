using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Neko.Transport;

/// <summary>
/// Bootstrap without a message: capture Rspack's module require from an
/// ordinary module factory during page startup, then retrieve the already
/// loaded first-party submission exports and the existing React app scope.
/// The production asset name is discovered dynamically; no credentials,
/// turn tokens or message bodies are inspected.
/// </summary>
internal static class NativeDispatcherBootstrap
{
    public const string InstallScript="""
      (() => {
        const ready=window.__nekoTransport;
        if(typeof ready?.submit==='function' &&
           typeof ready?.selectedModel==='function' &&
           typeof ready?.cancel==='function')return {ready:true,method:'existing'};
        const require=window.__nekoRequire;
        if(typeof require!=='function')return {ready:false,reason:'require_not_captured'};
        const exported=(name,ids)=>{
          for(const id of ids){
            if(!id)continue;
            try{
              const value=require(id)?.[name];
              if(typeof value==='function')return value;
            }catch{}
          }
          for(const module of Object.values(require.c||{})){
            const value=module?.exports?.[name];
            if(typeof value==='function')return value;
          }
          return null;
        };
        const submit=exported(__SUBMIT_EXPORT__,[
          window.__nekoSubmitModuleId,...__SUBMIT_MODULE_FALLBACKS__]);
        const cancel=exported('d',['vW7']);
        if(typeof submit!=='function')
          return {ready:false,reason:'submit_export_unavailable'};
        const origin=document.querySelector(__COMPOSER_SELECTOR__)||
          document.querySelector('main')||
          document.querySelector('#root')||document.body;
        let element=origin,fiber;
        while(element&&!fiber){
          const name=Object.keys(element).find(k=>k.startsWith('__reactFiber$'));
          if(name)fiber=element[name];
          element=element.parentElement;
        }
        let scope=null;
        for(let i=0;fiber&&i<180&&!scope;i++,fiber=fiber.return){
          let hook=fiber.memoizedState;
          for(let j=0;hook&&typeof hook==='object'&&j<80;j++,hook=hook.next){
            const v=hook.memoizedState?.current;
            if(v?.node && typeof v.get==='function' &&
               typeof v.set==='function' && v.queryClient){
              scope=v;break;
            }
          }
        }
        if(!scope)return {ready:false,reason:'react_scope_not_ready'};
        // The site's own follow-up composer uses scope.get(Ow.s, id).
        // That returns {slug, thinkingEffort}. Reuse the SAME selection
        // rather than silently injecting model:'auto' (Instant).
        let modelModule=null;
        try{modelModule=require(window.__nekoModelModuleId||__MODEL_MODULE_FALLBACK__)}catch{}
        // The site's global conversation-model atom provides a relatively
        // stable fingerprint if its module ID changes in a future Rspack build.
        if(!modelModule?.s || modelModule.a!==__MODEL_MARKER__)
          modelModule=Object.values(require.c||{}).map(m=>m?.exports)
            .find(e=>e?.a===__MODEL_MARKER__ && !!e.s)||null;
        const validSlug=slug=>typeof slug==='string' &&
          /^[a-zA-Z0-9_.-]{1,90}$/.test(slug);
        let catalogModule=null;
        try{catalogModule=require(window.__nekoCatalogModuleId||__CATALOG_MODULE_FALLBACK__)}catch{}
        // Warm the site's normal catalog query once at startup. The model
        // selector can be mounted before its async account model data has
        // arrived. No prompt is sent and React Query deduplicates requests.
        try{
          if(catalogModule?.c)
            Promise.resolve(scope.query?.getOrFetch?.(catalogModule.c))
              .catch(()=>{});
        }catch{}
        // Model IDs and availability come from the account's FIRST-PARTY
        // /models response, not a guessed consumer model identifier.
        const readCatalog=()=>{
          try{
            if(catalogModule?.c){
              const result=scope.get(catalogModule.c);
              if(Array.isArray(result?.data?.options))return result.data;
            }
          }catch{}
          try{
            const cached=scope.queryClient?.getQueryData?.([__MODEL_CACHE_KEY__]);
            if(Array.isArray(cached?.options))return cached;
          }catch{}
          return null;
        };
        const currentModel=id=>{
          if(modelModule?.s){
            for(const key of [id,modelModule.a].filter(Boolean)){
              try{
                const choice=scope.get(modelModule.s,key);
                if(choice && validSlug(choice.slug)){
                  const effort=typeof choice.thinkingEffort==='string' &&
                    /^(zero|min|standard|extended|xhigh|max|ultra)$/.test(choice.thinkingEffort)
                    ?choice.thinkingEffort:null;
                  return {slug:choice.slug,thinkingEffort:effort,
                    source:'site_selection'};
                }
              }catch{}
            }
          }
          return {slug:'auto',thinkingEffort:null,source:'automatic_model'};
        };
        // Every Neko send uses the highest effort explicitly offered by
        // this signed-in account. An old saved 'site' preference cannot
        // downgrade a request. Never invent effort values or a model slug.
        const selectedModel=(id)=>{
          const current=currentModel(id);
          const catalog=readCatalog();
          if(!catalog)return null;
          const versions=Array.isArray(catalog.versionOptions)
            ?catalog.versionOptions:[];
          const own=versions.find(v=>
            v?.slugs?.includes(current.slug))||versions[0];
          // Prefer the current model family on equal effort. Across
          // families, however, the highest supported effort wins.
          // Do not select hidden, unavailable, Pro or Work-only models.
          const offered=[
            ...(own?.options||[]),...(catalog.options||[]),
            ...versions.flatMap(v=>v?.options||[])
          ];
          const effortOrder=['zero','min','standard','extended',
            'xhigh','max','ultra'];
          const rank=value=>effortOrder.indexOf(value);
          const effortOf=item=>typeof item==='string'?item:
            item?.thinking_effort??item?.thinkingEffort??null;
          let best=null;
          const seen=new Set();
          for(const option of offered){
            if(!validSlug(option?.slug)||option.slug==='auto'||
               option.isAvailable===false||seen.has(option.slug))continue;
            seen.add(option.slug);
            const config=catalog.modelConfigBySlug?.[option.slug];
            if(!config||config.isWorkModeModel===true ||
               config.tags?.includes('hidden') ||
               config.reasoningType==='pro' ||
               config.reasoningType==='none')continue;
            const explicit=option.lane==='thinking' ||
              config.reasoningType==='reasoning' ||
              config.reasoningType==='auto' ||
              config.configurableThinkingEffort===true;
            if(!explicit)continue;
            const choices=[
              ...(Array.isArray(config.thinkingEfforts)
                ?config.thinkingEfforts:[]),
              ...offered.filter(v=>v?.slug===option.slug)
                .map(v=>v.thinkingEffort),
              ...(Array.isArray(catalog.sliderSettings)
                ?catalog.sliderSettings.filter(v=>
                    v?.modelSlug===option.slug)
                    .map(v=>v.thinkingEffort):[])
            ];
            const available=choices.map(effortOf)
              .filter(v=>rank(v)>=0)
              .sort((a,b)=>rank(b)-rank(a));
            if(!available.length)continue;
            const thinking=option.lane==='thinking' ||
              config.reasoningType==='reasoning';
            const candidate={
              slug:option.slug,thinkingEffort:available[0],
              source:'catalog_max',rank:rank(available[0]),
              thinking
            };
            if(!best || candidate.rank>best.rank ||
               (candidate.rank===best.rank &&
                candidate.thinking&&!best.thinking))
              best=candidate;
          }
          // Strict: no auto/Instant fallback when the account catalog
          // doesn't establish an eligible supported reasoning level.
          return best?{
            slug:best.slug,thinkingEffort:best.thinkingEffort,
            source:best.source
          }:null;
        };
        Object.defineProperty(window,'__nekoTransport',{
          configurable:true,enumerable:false,
          value:{scope,submit,selectedModel,
            cancel:(id)=>typeof cancel==='function'
              ?cancel(scope,id,undefined,{eventSource:'skip'})
              :Promise.reject(Object.assign(new Error('Native cancellation unavailable'),
                {name:'CancelUnavailable'}))}
        });
        return {ready:true,method:'loaded_rspack_require'};
      })()
      """;

    // The module identifier changes between frontend builds. Locate the
    // module *owning* the named first-party export, not a hard-coded Mx ID.
    // The indexer only inspects script source already loaded in the WebView.
    internal static (int Offset,string ModuleId,string RequireParameter)?
        FindSubmitFactory(string source,TransportSettings settings)
    {
        var marker=settings.SubmitExportName+":";
        var search=0;
        while(true)
        {
            var export=source.IndexOf(marker,search,StringComparison.Ordinal);
            if(export<0)return null;
            var left=Math.Max(0,export-2048);
            var prefix=source[left..export];
            var matches=System.Text.RegularExpressions.Regex.Matches(prefix,
                @"(?<module>[\w$]+)\s*:\s*(?:\(function\((?<args>[\w$,\s]+)\)\s*\{|function\((?<args>[\w$,\s]+)\)\s*\{|\((?<args>[\w$,\s]+)\)\s*=>\s*\{)");
            for(var i=matches.Count-1;i>=0;i--)
            {
                var match=matches[i];
                var args=match.Groups["args"].Value.Split(',');
                if(args.Length!=3 || args.Any(a=>
                    !System.Text.RegularExpressions.Regex.IsMatch(a.Trim(),@"^[\w$]+$")))
                    continue;
                // The named export normally appears in the first statement.
                // Avoid stopping in an unrelated nested function.
                if(export-(left+match.Index+match.Length)>500)break;
                return (left+match.Index+match.Length,
                    match.Groups["module"].Value,args[2].Trim());
            }
            search=export+marker.Length;
        }
    }

    // The app's current model selector is a keyed module export. Like the
    // submit export, its short Rspack ID changes between website builds.
    // Detect the factory whose named 's' export owns the globally keyed
    // conversation model selector. Use source already loaded in Chromium,
    // never hardcode an imagined model identifier.
    internal static string? FindModelFactory(string source,TransportSettings settings)
    {
        var marker=settings.ModelMarker;
        var search=0;
        while(true)
        {
            var index=source.IndexOf(marker,search,StringComparison.Ordinal);
            if(index<0)return null;
            var left=Math.Max(0,index-4000);
            var prefix=source[left..index];
            var matches=System.Text.RegularExpressions.Regex.Matches(prefix,
                @"(?<module>[\w$]+)\s*:\s*(?:\(function\((?<args>[\w$,\s]+)\)\s*\{|function\((?<args>[\w$,\s]+)\)\s*\{|\((?<args>[\w$,\s]+)\)\s*=>\s*\{)");
            for(var i=matches.Count-1;i>=0;i--)
            {
                var m=matches[i];
                if(m.Groups["args"].Value.Split(',').Length!=3)continue;
                var start=left+m.Index+m.Length;
                if(index-start>3500)continue;
                var factoryPrefix=source[start..Math.Min(index,start+500)];
                if(!System.Text.RegularExpressions.Regex.IsMatch(
                    factoryPrefix,@"\bs\s*:\s*\(\)\s*=>"))
                    continue;
                return m.Groups["module"].Value;
            }
            search=index+marker.Length;
        }
    }

    internal static string? FindCatalogFactory(string source,TransportSettings settings)
    {
        var search=0;
        while(true)
        {
            var index=source.IndexOf(settings.ModelCacheKey,search,StringComparison.Ordinal);
            if(index<0)return null;
            search=index+settings.ModelCacheKey.Length;
            if(!source[Math.Max(0,index-80)..index].Contains(
                "queryKey:",StringComparison.Ordinal))continue;
            var prefix=source[Math.Max(0,index-2200)..index];
            var matches=System.Text.RegularExpressions.Regex.Matches(prefix,
                @"(?<module>[\w$]+)\s*:\s*\(function\([\w$,\s]+\)\s*\{");
            if(matches.Count>0)
                return matches[^1].Groups["module"].Value;
        }
    }

    public static async Task<(bool Ready,string Reason)> TryInstallAsync(CoreWebView2 core,TransportSettings settings)
    {
        try{
            var script=InstallScript
                .Replace("__SUBMIT_EXPORT__",JsonSerializer.Serialize(settings.SubmitExportName))
                .Replace("__SUBMIT_MODULE_FALLBACKS__",
                    string.Join(",",settings.SubmitModuleFallbacks.Select(id=>JsonSerializer.Serialize(id))))
                .Replace("__COMPOSER_SELECTOR__",JsonSerializer.Serialize(settings.ComposerSelector))
                .Replace("__MODEL_MODULE_FALLBACK__",JsonSerializer.Serialize(settings.ModelModuleFallback))
                .Replace("__CATALOG_MODULE_FALLBACK__",JsonSerializer.Serialize(settings.CatalogModuleFallback))
                .Replace("__MODEL_MARKER__",JsonSerializer.Serialize(settings.ModelMarker))
                .Replace("__MODEL_CACHE_KEY__",JsonSerializer.Serialize(settings.ModelCacheKey));
            var raw=await core.ExecuteScriptAsync(script);
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            var ok=root.TryGetProperty("ready",out var v)&&v.GetBoolean();
            return (ok,root.TryGetProperty(ok?"method":"reason",out var reason)
                ?reason.GetString()??"unknown":"unknown");
        }catch(Exception e){return (false,"script_error:"+e.GetType().Name);}
    }

    /// <summary>Make first-party Send and Cancel available without a message.
    /// One page reload may be necessary to capture the live ESM module loader.
    /// </summary>
    public static async Task<(bool Ready,string Reason)> InitializeAsync(
        CoreWebView2 core,TransportSettings settings,CancellationToken cancellation=default)
    {
        var already=await TryInstallAsync(core,settings);
        if(already.Ready)return already;
        var capture=await CaptureRequireOnReloadAsync(core,settings,cancellation);
        var result=await TryInstallAsync(core,settings);
        if(result.Ready)return result;
        try
        {
            using var parsed=JsonDocument.Parse(capture);
            var root=parsed.RootElement;
            return (false,root.TryGetProperty("reason",out var reason)
                ?reason.GetString()??result.Reason:result.Reason);
        }
        catch(JsonException){return result;}
    }

    public static async Task<string> CaptureRequireOnReloadAsync(CoreWebView2 core,TransportSettings settings,
        CancellationToken cancellation=default)
    {
        var parsed=new List<(string Id,string Url)>();
        var sources=core.GetDevToolsProtocolEventReceiver("Debugger.scriptParsed");
        var pauses=core.GetDevToolsProtocolEventReceiver("Debugger.paused");
        var hit=new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handled=false;
        string? moduleId=null;
        string? modelModuleId=null;
        string? catalogModuleId=null;
        string? requireParameter=null;
        void OnScript(object? _,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            try{
                using var j=JsonDocument.Parse(e.ParameterObjectAsJson);
                var root=j.RootElement;
                var url=root.TryGetProperty("url",out var u)?u.GetString()??"":"";
                var id=root.TryGetProperty("scriptId",out var s)?s.GetString()??"":"";
                if(!string.IsNullOrEmpty(id) &&
                   Uri.TryCreate(url,UriKind.Absolute,out var uri) &&
                   uri.Scheme=="https" && uri.Host==settings.SiteHost &&
                   uri.AbsolutePath.StartsWith(settings.AssetPrefix,StringComparison.Ordinal) &&
                   uri.AbsolutePath.EndsWith(".js",StringComparison.Ordinal))
                    parsed.Add((id,url));
            }catch(Exception){}
        }
        async void OnPause(object? _,CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            if(handled)return;
            handled=true;
            try{
                using var j=JsonDocument.Parse(e.ParameterObjectAsJson);
                var frames=j.RootElement.GetProperty("callFrames");
                var value="factory_frame_unavailable";
                foreach(var frame in frames.EnumerateArray().Take(7))
                {
                    var frameId=frame.GetProperty("callFrameId").GetString();
                    var raw=await core.CallDevToolsProtocolMethodAsync(
                        "Debugger.evaluateOnCallFrame",
                        JsonSerializer.Serialize(new{
                            callFrameId=frameId,
                            expression="""
                                (() => {
                                  const capturedRequire=__REQUIRE__;
                                  if(typeof capturedRequire!=='function')
                                    return 'no_factory_require';
                                  Object.defineProperty(window,'__nekoRequire',{
                                    configurable:true,enumerable:false,
                                    value:capturedRequire});
                                  Object.defineProperty(window,'__nekoSubmitModuleId',{
                                    configurable:true,enumerable:false,
                                    value:__MODULE__});
                                  Object.defineProperty(window,'__nekoModelModuleId',{
                                    configurable:true,enumerable:false,
                                    value:__MODEL_MODULE__});
                                  Object.defineProperty(window,'__nekoCatalogModuleId',{
                                    configurable:true,enumerable:false,
                                    value:__CATALOG_MODULE__});
                                  return 'require_captured';
                                })()
                                """.Replace("__REQUIRE__",requireParameter??"undefined")
                                   .Replace("__MODULE__",JsonSerializer.Serialize(moduleId))
                                   .Replace("__MODEL_MODULE__",JsonSerializer.Serialize(modelModuleId))
                                   .Replace("__CATALOG_MODULE__",JsonSerializer.Serialize(catalogModuleId)),
                            returnByValue=true
                        }));
                    using var eval=JsonDocument.Parse(raw);
                    var str=eval.RootElement.TryGetProperty("result",out var result)
                        && result.TryGetProperty("value",out var v)
                        ?v.GetString()??"":"";
                    if(str=="require_captured"){value=str;break;}
                }
                hit.TrySetResult(value);
            }
            catch(Exception ex){hit.TrySetResult("pause_error:"+ex.GetType().Name);}
            finally{
                try{await core.CallDevToolsProtocolMethodAsync("Debugger.resume","{}");}
                catch(Exception){}
            }
        }

        sources.DevToolsProtocolEventReceived+=OnScript;
        pauses.DevToolsProtocolEventReceived+=OnPause;
        var enabled=false;
        var stage="debugger_enable";
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Debugger.enable","{}");
            enabled=true;
            // Re-enabling replays Debugger.scriptParsed for already evaluated
            // ESM bundles. Merely enabling once after page load can yield zero.
            stage="debugger_replay_disable";
            await core.CallDevToolsProtocolMethodAsync("Debugger.disable","{}");
            enabled=false;
            stage="debugger_replay_enable";
            await core.CallDevToolsProtocolMethodAsync("Debugger.enable","{}");
            enabled=true;
            await Task.Delay(650,cancellation);
            stage="inspect_loaded_modules";
            var target="";
            int line=0,col=0;
            foreach(var script in parsed.ToArray().DistinctBy(x=>x.Id).Take(400))
            {
                string source;
                try{
                    var response=await core.CallDevToolsProtocolMethodAsync(
                        "Debugger.getScriptSource",
                        JsonSerializer.Serialize(new{scriptId=script.Id}));
                    using var doc=JsonDocument.Parse(response);
                    source=doc.RootElement.GetProperty("scriptSource").GetString()??"";
                }catch(Exception){continue;}
                // The selector can live in this same Rspack bundle, as in
                // captured frontend builds, or in an earlier chunk.
                // Do not scan *later* chunks on the startup critical path.
                modelModuleId??=FindModelFactory(source,settings);
                catalogModuleId??=FindCatalogFactory(source,settings);
                var factory=FindSubmitFactory(source,settings);
                if(factory is null)continue;
                target=script.Url;
                var at=factory.Value.Offset;
                moduleId=factory.Value.ModuleId;
                requireParameter=factory.Value.RequireParameter;
                for(var i=0;i<at;i++){
                    if(source[i]=='\n'){line++;col=0;}
                    else col++;
                }
                break;
            }
            if(target=="")return "{\"ready\":false,\"reason\":\"submit_factory_not_found\"}";
            stage="set_breakpoint";
            var bp=await core.CallDevToolsProtocolMethodAsync(
                "Debugger.setBreakpointByUrl",
                JsonSerializer.Serialize(new{url=target,
                    lineNumber=line,columnNumber=col}));
            using(var json=JsonDocument.Parse(bp))
                if(!json.RootElement.TryGetProperty("breakpointId",out _))
                    return "{\"ready\":false,\"reason\":\"factory_breakpoint_not_set\"}";

            // Initializer only: do this while the app is minimized and no
            // conversations are generating. The normal user page reloads once.
            stage="reload_document";
            core.Reload();
            stage="wait_for_factory";
            var done=await Task.WhenAny(hit.Task,Task.Delay(14000,cancellation));
            var result=done==hit.Task?await hit.Task:"factory_not_executed_during_reload";
            if(result!="require_captured")
                return JsonSerializer.Serialize(new{ready=false,reason=result});
            stage="install_dispatcher";
            for(var i=0;i<24;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                await Task.Delay(350,cancellation);
                var installed=await TryInstallAsync(core,settings);
                if(installed.Ready)
                    return JsonSerializer.Serialize(new{ready=true,
                        capture=result,method=installed.Reason,
                        calibrationMessage=false});
            }
            var failed=await TryInstallAsync(core,settings);
            return JsonSerializer.Serialize(new{ready=false,capture=result,
                reason=failed.Reason});
        }
        catch(OperationCanceledException){
            return "{\"ready\":false,\"reason\":\"cancelled\"}";
        }
        catch(Exception e){
            return JsonSerializer.Serialize(new{ready=false,
                reason="bootstrap_error:"+stage+":"+e.GetType().Name});
        }
        finally
        {
            try{if(enabled)await core.CallDevToolsProtocolMethodAsync(
                "Debugger.resume","{}");}catch(Exception){}
            try{if(enabled)await core.CallDevToolsProtocolMethodAsync(
                "Debugger.disable","{}");}catch(Exception){}
            pauses.DevToolsProtocolEventReceived-=OnPause;
            sources.DevToolsProtocolEventReceived-=OnScript;
        }
    }
}
