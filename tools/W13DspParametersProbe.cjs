const fs=require('fs'),path=require('path'),{spawn}=require('child_process'),{chromium}=require('playwright-core');

const wait=ms=>new Promise(r=>setTimeout(r,ms));

(async()=>{const config=path.join(process.env.LOCALAPPDATA,'CelesteMusicPlayer');
const saved=new Map(['eq-curve.json','dsp-extra.json','dsp-profiles.json'].map(n=>[path.join(config,n),fs.existsSync(path.join(config,n))?fs.readFileSync(path.join(config,n)):null]));
let child=spawn(process.argv[2],['--w7-background-test'],{stdio:'ignore'});
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
await p.exposeFunction('readDspExtra',()=>JSON.parse(fs.readFileSync(path.join(config,'dsp-extra.json'),'utf8')));const data=await p.evaluate(async()=>{const call=(operation,extra={})=>CelesteTransport.request('dsp',{operation,...extra});
const out={initial:await call('get'),modules:await call('modules'),rack:await call('rack'),bypass:await call('bypass'),profiles:await call('profiles'),controls:{},steps:[]};
for(const module of ['headroom','rg','src','comp','channel','stereo','matrix','fir']){try{out.controls[module]=await call('module',{module})}catch(e){out.controls[module]={error:e.message}}}for(const [name,extra] of [
 ['AudioFxChannelBalanceSlider',{value:0.35}], ['AudioFxChannelLeftGainSlider',{value:-3}],
 ['AudioFxChannelRightGainSlider',{value:2}], ['AudioFxChannelMonoCombo',{index:3}],
 ['AudioFxChannelSwapToggle',{value:true}],['AudioFxChannelInvertLToggle',{value:true}],
 ['AudioFxChannelInvertRToggle',{value:true}],['AudioFxChannelLeftDelaySlider',{value:2.5}],
 ['AudioFxChannelRightDelaySlider',{value:1.5}],['DspPowerChannel',{value:true}],
 ['AudioFxChannelCrossfeedCutoffSlider',{value:1100}],['DspPowerXfeed',{value:true}],
 ['DspPowerXfeed',{value:false}],['DspPowerXfeed',{value:true}],
 ['AudioFxChannelCrossfeedSlider',{value:33.3}], ['AudioFxSafetyHeadroomSlider',{value:-4.5}],
 ['AudioFxSafetyLimiterToggle',{value:true}], ['DspPowerHeadroom',{value:false}],
 ['DspPowerHeadroom',{value:true}],['DspPowerChannel',{value:false}],
 ['AudioFxChannelCrossfeedToggle',{value:false}],['DspPowerHeadroom',{value:false}],
 ['AudioFxChannelBalanceSlider',{value:100}],['AudioFxChannelMonoCombo',{index:999}]
 ]) {const reply=await call('setControl',{module:name.includes('Safety')||name==='DspPowerHeadroom'?'headroom':'channel',name,...extra});out.steps.push({name,reply,channel:await call('module',{module:'channel'}),headroom:await call('module',{module:'headroom'}),disk:await window.readDspExtra()});}
return out});
fs.writeFileSync(process.argv[3],JSON.stringify(data,null,2));
console.log('WROTE DSP baseline/scenario',process.argv[3]);
await p.evaluate(()=>CelesteShell.send('window',{action:'close'}));
for(let i=0;
i<40&&child.exitCode===null;
i++)await wait(250);
await browser.close();browser=null;
if(child.exitCode===null){child.kill();await new Promise(r=>child.once('exit',r))}
await wait(500);child=spawn(process.argv[2],['--w7-background-test'],{stdio:'ignore'});
for(let i=0;i<60;i++){try{browser=await chromium.connectOverCDP('http://127.0.0.1:9347');break}catch{await wait(500)}}
for(let i=0;i<90;i++){p=browser.contexts()[0].pages().find(p=>p.url().includes('celeste.internal'));if(p)break;await wait(500)}
await p.waitForFunction(()=>window.CelesteTransport&&window.CelesteShell);
data.restarted=await p.evaluate(async()=>({channel:await CelesteTransport.request('dsp',{operation:'module',module:'channel'}),headroom:await CelesteTransport.request('dsp',{operation:'module',module:'headroom'})}));
fs.writeFileSync(process.argv[3],JSON.stringify(data,null,2));console.log('restart captured');
await p.evaluate(()=>CelesteShell.send('window',{action:'close'}));for(let i=0;i<40&&child.exitCode===null;i++)await wait(250);
}finally{await browser?.close();
if(child.exitCode===null){child.kill();
await new Promise(r=>child.once('exit',r))}for(const [f,bytes]of saved){if(bytes)fs.writeFileSync(f,bytes);
else if(fs.existsSync(f))fs.unlinkSync(f)}}})().catch(e=>{console.error(e);
process.exitCode=1});



