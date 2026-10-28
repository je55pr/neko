'use strict';
// Keep the public repository limited to provider-neutral code and fixtures.
const {execFileSync}=require('node:child_process');
const fs=require('node:fs');
const path=require('node:path');
const root=path.resolve(__dirname,'..');

const forbiddenNames=[
  ['chat','g','p','t'],['open','ai'],['c','g','s'],
  ['giz','mo'],['backend','-','api'],['snor','lax'],
  ['oai','-'],['model','_dil_v2'],['model','_slug'],
  ['je','55pr'],['github','_pat_'],['gh','p_'],
  ['sk','-proj-']
].map(parts=>parts.join('').toLowerCase());

const sensitivePatterns=[
  {label:'provider model identifier',re:/\\bgpt\\b/i},
  {label:'local user path',re:/(?:[a-z]:\\users\\|\/users\/)[^\s'"<>]+/i},
  {label:'email address',re:/\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b/i},
  {label:'secret-like credential',re:/\b(?:sk|gho|ghu|ghs)_[a-z0-9_-]{24,}\b/i},
  {label:'bearer credential',re:/\bbearer\s+[a-z0-9._-]{24,}/i},
  {label:'hardcoded unique identifier',re:/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i},
  {label:'private network endpoint',re:/\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b/}
];

let files;
try{
  const output=execFileSync('git',['ls-files','-z'],{cwd:root});
  files=output.toString('utf8').split('\0').filter(Boolean);
}catch(error){
  process.stderr.write('Safety audit requires a Git worktree.\n');
  process.exit(2);
}

let count=0;
for(const relative of files){
  const file=path.join(root,relative);
  if(!fs.statSync(file).isFile())continue;
  const content=fs.readFileSync(file,'utf8');
  const lower=content.toLowerCase();
  for(const name of forbiddenNames){
    if(lower.includes(name)){
      console.error('Disallowed identifying term in '+relative);
      count++;
    }
  }
  for(const pattern of sensitivePatterns){
    if(pattern.re.test(content)){
      console.error('Disallowed '+pattern.label+' in '+relative);
      count++;
    }
  }
}
if(count){
  console.error('Public source audit failed: '+count+' finding(s).');
  process.exit(1);
}
console.log('Public source audit passed: '+files.length+' tracked files.');
