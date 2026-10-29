const test=require('node:test');
const assert=require('node:assert/strict');
const {inspect}=require('../scripts/check-public-source.cjs');

test('published names are checked as well as file content',()=>{
  const identifying=['chat','g','p','t'].join('');
  assert(inspect('src/'+identifying+'-service.cs','').length>0);
  assert(inspect('src/generic.cs','class Is'+identifying+'Url {}').length>0);
});

test('spaced and hyphenated provider names are rejected',()=>{
  assert(inspect('generic.txt',['Chat',' G','P','T'].join('')).includes('provider name variant in source'));
  assert(inspect('generic.txt',['open','-ai'].join('')).includes('provider name variant in source'));
  assert(inspect(['src/open','_ai-core.cs'].join(''),'').includes('provider name variant in filename'));
  assert(inspect('generic.txt',['class IsOpen','_AIUrl {}'].join('')).includes('provider name variant in source'));
});

test('embedded credentials and unique IDs are rejected without logging secrets',()=>{
  const key=['sk','-','x'.repeat(45)].join('');
  assert(inspect('generic.txt',key).length>0);
  const uuid=['12345678','1234','1234','abcd','123456789abc'].join('-');
  assert(inspect('generic.txt',uuid).length>0);
  const email=['person','@','example.test'].join('');
  assert(inspect('generic.txt',email).length>0);
});

test('generic source and local virtual host are allowed',()=>{
  assert.deepEqual(inspect('src/Neko.App/OfflineBoardStore.cs',
    'const url = "https://neko.internal/index.html";'),[]);
  assert.deepEqual(inspect('tests/neutral.test.cjs',
    'public interface IConversationProvider { }'),[]);
});
