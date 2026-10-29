'use strict';
// Reject identifying content from every tracked public source file.
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

const modelName=['g','p','t'].join('');
const identifyingPatterns=[
  {label:'provider name variant',re:new RegExp(
    String.raw`\b(?:chat[\s_.-]*${modelName}|open[\s_.-]*ai)\b`,'i')},
  {label:'provider model identifier',re:new RegExp(
    String.raw`\b${modelName}\b`,'i')},
  {label:'local user path',re:/(?:[a-z]:\\users\\|\/users\/)[^\s'"<>]+/i},
  {label:'email address',re:/\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b/i},
  {label:'secret-like credential',re:/\b(?:sk[-_]|gho_|ghu_|ghs_)[a-z0-9_-]{24,}\b/i},
  {label:'bearer credential',re:/\bbearer\s+[a-z0-9._-]{24,}/i},
  {label:'hardcoded unique identifier',re:/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i},
  {label:'private network endpoint',re:/\b(?:10\.\d{1,3}|192\.168|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b/},
  {label:'provider endpoint',re:/\/(?:backend[-_]api|api\/conversation|api\/auth)\b/i}
];

function inspect(relative,content){
  const problems=[];
  const candidate=relative.toLowerCase();
  for(const name of forbiddenNames){
    if(candidate.includes(name))problems.push('identifying filename');
    if(content.toLowerCase().includes(name))problems.push('identifying source term');
  }
  for(const rule of identifyingPatterns){
    if(rule.re.test(relative))problems.push(rule.label+' in filename');
    if(rule.re.test(content))problems.push(rule.label+' in source');
  }
  return [...new Set(problems)];
}

function check(){
  const gitPath=process.platform==='win32'
    ?'C:\\Program Files\\Git\\cmd\\git.exe':'/usr/bin/git';
  let files;
  try{
    files=execFileSync(gitPath,['ls-files','-z'],{cwd:root})
      .toString('utf8').split('\0').filter(Boolean);
  }catch{
    console.error('Source audit requires a Git worktree.');
    return 2;
  }
  let count=0;
  for(const relative of files){
    const file=path.join(root,relative);
    if(!fs.statSync(file).isFile())continue;
    const content=fs.readFileSync(file,'utf8');
    const issues=inspect(relative,content);
    for(const issue of issues){
      console.error('Disallowed '+issue+' in '+relative);
      count++;
    }
  }
  if(count){
    console.error('Public source audit failed: '+count+' finding(s).');
    return 1;
  }
  console.log('Public source audit passed: '+files.length+' tracked files.');
  return 0;
}

if(require.main===module)process.exitCode=check();
module.exports={inspect};
