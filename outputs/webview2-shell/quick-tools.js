(()=>{
'use strict';
const $=id=>document.getElementById(id),web=window.chrome?.webview,motion=window.CelesteMotion;
const send=(command,extra={})=>web?.postMessage({command,...extra});
const openPanel=id=>{const panel=$(id);if(window.CelesteQuickPanels)window.CelesteQuickPanels.toggle(panel);else panel.hidden=!panel.hidden;};
function formatGain(value){const rounded=Math.round(Number(value)||0);return rounded>0?'+'+rounded:String(rounded);}

for(const name of ['properties','eq','speed'])$(name+'-toggle').onclick=()=>{openPanel(name+'-panel');send(name)};
$('location').onclick=()=>send('location');

/* 十段均衡器：十根竖条横排，频点与量程对齐主程序（32…16K，±15 dB，步进 1） */
const bandLabels=['32','64','125','250','500','1K','2K','4K','8K','16K'],sliders=[],outputs=[];
for(const label of bandLabels){
  const band=document.createElement('div');band.className='eq-band';
  const value=document.createElement('output');value.textContent='0';
  const host=document.createElement('div');host.className='slider-host';
  const input=document.createElement('input');
  input.type='range';Object.assign(input,{min:-15,max:15,step:1,value:0});
  input.setAttribute('aria-label',label+' Hz 增益');
  input.oninput=()=>{value.textContent=formatGain(input.value);$('eq-preset').value='Custom';send('eq',{gains:sliders.map(slider=>Number(slider.value)),preset:'Custom'})};
  host.append(input);
  const freq=document.createElement('span');freq.className='freq';freq.textContent=label;
  band.append(value,host,freq);$('eq-bands').append(band);
  sliders.push(input);outputs.push(value);
}

/* 播放速度：同一条 0.5–2.00× 滑条；「音调不变」= 保持音高 */
const rate=$('speed-rate');
const showRate=value=>$('speed-value').textContent=Number(value).toFixed(2)+'×';
const pitchRadios=()=>document.querySelectorAll('input[name=speed-pitch]');
const preservePitch=()=>document.querySelector('input[name=speed-pitch]:checked')?.value!=='follow';
const sendSpeed=()=>send('speed',{value:Number(rate.value),pitch:preservePitch()});
rate.oninput=()=>showRate(rate.value);
rate.onchange=()=>{showRate(rate.value);sendSpeed()};
$('speed-reset').onclick=()=>{rate.value='1';showRate(1);sendSpeed()};
pitchRadios().forEach(radio=>radio.onchange=sendSpeed);

$('eq-preset').onchange=()=>send('eq',{preset:$('eq-preset').value});

web?.addEventListener('message',e=>{
  const m=e.data;
  if(m.kind==='properties'){
    const list=$('file-properties');list.replaceChildren();
    for(const [key,value]of Object.entries(m.values)){const term=document.createElement('dt'),detail=document.createElement('dd');term.textContent=key;detail.textContent=String(value);list.append(term,detail)}
    motion?.fadeIn?.(list);
  }
  if(m.kind==='eq'){
    const gains=Array.isArray(m.gains)?m.gains:[];
    sliders.forEach((slider,index)=>{const gain=Math.max(-15,Math.min(15,Number(gains[index]??0)));slider.value=gain;outputs[index].textContent=formatGain(gain)});
    if(m.preset)$('eq-preset').value=m.preset;
  }
  if(m.kind==='speed'){
    rate.value=String(m.rate??1);showRate(rate.value);
    pitchRadios().forEach(radio=>radio.checked=(radio.value==='preserve')===!!m.pitch);
  }
  if(m.kind==='state'){
    for(const id of ['properties-toggle','location'])$(id).disabled=!m.queue.length;
    const enabled=!!m.volumeEnabled;
    sliders.forEach(slider=>slider.disabled=!enabled);
    $('eq-preset').disabled=!enabled;rate.disabled=!enabled;$('speed-reset').disabled=!enabled;
    pitchRadios().forEach(radio=>radio.disabled=!enabled);
  }
});
})();
