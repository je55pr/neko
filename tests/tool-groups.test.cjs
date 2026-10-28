const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');

function loadGrouping() {
  const window={};
  vm.runInNewContext(fs.readFileSync(
    path.join(__dirname,'..','src','ui','NekoToolGroups.js'),'utf8'),{
    location:{origin:'https://neko.internal'},window
  });
  return window.__nekoToolGroups;
}

const activities=[
  {id:'first',kind:'tool_call',title:'Calling functions.exec'},
  {id:'second',kind:'tool_call',title:'Calling api.tool.call_tool'},
  {id:'third',kind:'tool_result',title:'Tool result: api.tool.call_tool'},
  {id:'fourth',kind:'tool_result',title:'Tool result: functions.exec'}
];

function fakeDocument(){
  return {createElement(tag){
    return {
      tagName:tag.toUpperCase(),className:'',textContent:'',
      open:false,children:[],
      append(...children){this.children.push(...children);},
      replaceChildren(...children){this.children=children;}
    };
  }};
}

test('four consecutive call/result entries use one expander',()=>{
  const grouping=loadGrouping();
  const document=fakeDocument();
  const cache=new Map();
  const makeLeaf=item=>{
    const detail=document.createElement('details');
    detail.textContent=item.title;
    return detail;
  };
  const blocks=grouping.sequences([
    {id:'user',kind:'user',text:'Prompt'},...activities,
    {id:'answer',kind:'message',text:'Final answer'},
    ...activities.slice(0,2)
  ]);
  assert.deepEqual(Array.from(blocks,x=>x.type),
    ['item','tools','item','tools']);
  const group=grouping.disclosure(document,blocks[1].items,makeLeaf,{
    cache,wrapperClass:'nwToolGroup',contentClass:'nwToolGroupBody'
  });
  assert.equal(group.children[0].textContent,'4 tool events');
  assert.equal(group.children[1].children.length,4);
  assert.equal(group.children[1].children[0].textContent,
    'Calling functions.exec');
  group.open=true;
  const extended=grouping.disclosure(document,[
    ...activities,{id:'fifth',kind:'tool_result',title:'Another result'}
  ],makeLeaf,{cache,wrapperClass:'nwToolGroup'});
  assert.equal(extended,group);
  assert.equal(extended.open,true);
  assert.equal(extended.children[0].textContent,'5 tool events');
  assert.equal(extended.children[1].children.length,5);
});

test('thinking, media, and normal messages split tool runs',()=>{
  const grouping=loadGrouping();
  for(const kind of ['thinking','media','message','user']){
    const result=grouping.sequences([
      activities[0],{kind,id:'break',text:'Visible boundary'},activities[1]
    ]);
    assert.deepEqual(Array.from(result,x=>x.type),
      ['tools','item','tools']);
  }
});

test('one isolated tool retains its own expander',()=>{
  const grouping=loadGrouping(),document=fakeDocument();
  const leaf=document.createElement('details');
  leaf.textContent='Calling functions.exec';
  assert.equal(grouping.disclosure(document,[activities[0]],()=>leaf),leaf);
  assert.equal(grouping.label(1),'1 tool event');
});

test('invisible empty assistant records cannot split a visible tool run',()=>{
 const grouping=loadGrouping();
 const hidden=(n,kind='message')=>({
  id:'hidden-'+n,kind,role:'assistant',text:''
 });
 const run=grouping.sequences([
  {id:'call-1',kind:'tool_call',title:'Calling functions.exec'},
  {id:'result-1',kind:'tool_result',title:'Tool output'},
  hidden(1),
  {id:'call-2',kind:'tool_call',title:'Calling api.tool.call_tool'},
  hidden(2,'progress'),
  {id:'call-3',kind:'tool_call',title:'Calling functions.exec'},
  {id:'result-3',kind:'tool_result',title:'Tool output'}
 ]);
 assert.deepEqual(Array.from(run,x=>x.type),['tools']);
 assert.equal(run[0].items.length,5);
 const group=grouping.disclosure(fakeDocument(),run[0].items,
  item=>({id:item.id}));
 assert.equal(group.children[0].textContent,'5 tool events');
});
test('real visible progress always splits tool runs, empty user text still separates turns',()=>{
 const grouping=loadGrouping();
 const before=[{id:'a',kind:'tool_call'},{id:'b',kind:'tool_result'}];
 const after=[{id:'c',kind:'tool_call'},{id:'d',kind:'tool_result'}];
 const progress={id:'progress',kind:'progress',role:'assistant',
  text:'I found an issue; checking it now.'};
 const run=grouping.sequences([...before,progress,...after]);
 assert.deepEqual(Array.from(run,x=>x.type),['tools','item','tools']);
 assert.equal(run[1].item,progress);
 const emptyAttachmentUser={id:'user',role:'user',kind:'message',text:''};
 assert.deepEqual(Array.from(grouping.sequences([
  ...before,emptyAttachmentUser,...after
 ]),x=>x.type),['tools','item','tools']);
});

test('stream updates preserve each tool detail open state by event id',()=>{
  const grouping=loadGrouping(),document=fakeDocument();
  const cache=new Map();
  const child=item=>{
    const node=document.createElement('details');
    node.textContent=item.title;
    return node;
  };
  const first=grouping.disclosure(document,activities.slice(0,3),
    child,{cache});
  first.children[1].children[1].open=true;
  first.open=true;
  const updated=grouping.disclosure(document,[
    {...activities[0],title:'Updated call'},
    {...activities[1],title:'Updated call'},
    {...activities[2],title:'Updated result'},
    {...activities[3]}
  ],child,{cache});
  assert.equal(updated,first);
  assert.equal(updated.open,true);
  assert.equal(updated.children[1].children[1].open,true);
  assert.equal(updated.children[1].children[1].textContent,'Updated call');
  assert.equal(updated.children[1].children[0].open,false);
});
