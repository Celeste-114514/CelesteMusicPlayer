const fs=require('fs'),path=require('path'),{spawn}=require('child_process'),{chromium}=require('playwright-core');

const wait=ms=>new Promise(r=>setTimeout(r,ms));

(async()=>{const config=path.join(process.env.LOCALAPPDATA,'CelesteMusicPlayer');
const saved=new Map(['eq-curve.json','dsp-extra.json'].map(n=>[path.join(config,n),fs.existsSync(path.join(config,n))?fs.readFileSync(path.join(config,n)):null]));
const child=spawn(process.argv[2],['--w7-background-test'],{stdio:'ignore'});
let browser;
try{for(let i=0;
i<60;
i++){try{browser=await chromium.connectOverCDP('http://127.0.0.1:9347');
break}catch{await wait(500)}}if(!browser)throw Error('CDP unavailable');
let p;
for(let i=0;
i<90;
i++){p=browser.contexts()[0].pages().find(p=>p.url().includes('celeste.internal'));
if(p)break;
await wait(500)}await p.waitForFunction(()=>window.CelesteTransport&&window.CelesteShell);
const data=await p.evaluate(async()=>{const call=(operation,extra={})=>CelesteTransport.request('dsp',{operation,...extra});
const out={initial:await call('get'),modules:await call('modules'),rack:await call('rack'),bypass:await call('bypass'),profiles:await call('profiles'),controls:{},steps:[]};
for(const module of ['headroom','rg','src','comp','channel','stereo','matrix','fir']){try{out.controls[module]=await call('module',{module})}catch(e){out.controls[module]={error:e.message}}}for(const [operation,extra]of [['set',{preampDb:-3}],['undo',{}],['redo',{}],['ab',{}],['set',{preampDb:-5}],['ab',{}],['set',{preampDb:-7}],['ab',{}],['undo',{}],['redo',{}]]){const reply=await call(operation,extra);
out.steps.push({operation,reply,state:await call('get')})}return out});
fs.writeFileSync(process.argv[3],JSON.stringify(data,null,2));
console.log('WROTE DSP baseline/scenario',process.argv[3]);
await p.evaluate(()=>CelesteShell.send('window',{action:'close'}));
for(let i=0;
i<40&&child.exitCode===null;
i++)await wait(250);
}finally{await browser?.close();
if(child.exitCode===null){child.kill();
await new Promise(r=>child.once('exit',r))}for(const [f,bytes]of saved){if(bytes)fs.writeFileSync(f,bytes);
else if(fs.existsSync(f))fs.unlinkSync(f)}}})().catch(e=>{console.error(e);
process.exitCode=1});



