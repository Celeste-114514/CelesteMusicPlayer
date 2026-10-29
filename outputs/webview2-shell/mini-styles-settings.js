/* Shared desktop-host style library; no localStorage fork between main and mini. */
(() => {
'use strict';
const node=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
const request=(action,payload={})=>window.CelesteTransport.request('appSettings',{operation:'miniStyles',action,...payload});
async function mount(host){
 const status=node('p','正在读取样式库…');status.setAttribute('role','status');host.append(status);
 try{paint(await request('get'));}catch(e){status.textContent='加载失败：'+e.message;}
 function paint(data){
 if(!host.isConnected)return;host.querySelectorAll('.mini-style-workspace').forEach(e=>e.remove());status.textContent='选择后立即应用；编辑后点击「另存并使用」。';
 const work=node('div');work.className='mini-style-workspace';work.style.cssText='display:grid;gap:14px;max-width:680px';host.append(work);
 const label=node('label','当前样式 '),select=node('select');select.id='mini-style-select';for(const s of data.styles)select.append(new Option(s.name+(s.id.startsWith('user-')?' · 用户样式':''),s.id));select.value=data.selected;label.append(select);work.append(label);
 const current=data.styles.find(s=>s.id===data.selected)||data.styles[0],draft=JSON.parse(JSON.stringify(current));
 draft.name=current.id.startsWith('user-')?current.name:current.name+' · 我的版本';
 const preview=node('div');preview.style.cssText='border:1px solid var(--line);border-radius:12px;padding:20px;display:grid;gap:12px';preview.setAttribute('aria-label','样式预览');
 const title=node('strong','歌曲名称 · Celeste'),lyric=node('strong','把世界调小，把音乐放大。'),hint=node('span','歌手 · 专辑'),bar=node('div');bar.style.cssText='height:4px;border-radius:4px;background:linear-gradient(to right,var(--preview-accent) 35%,var(--preview-track) 35%)';const art=node('div');art.setAttribute('aria-label','封面预览');art.style.background='linear-gradient(135deg,#bc8f80,#789998)';preview.append(art,title,hint,lyric,bar);work.append(preview);
 function update(){const c=draft.colors||{};const dark=draft.mode==='dark'||(draft.mode==='follow'&&document.documentElement.dataset.theme==='dark');preview.style.background=c.background||(dark?'#242125':'#fcf9f8');preview.style.color=c.text||(dark?'#f4eff4':'#302c38');hint.style.color=c.muted||(dark?'#bdb4be':'#97838e');lyric.style.fontSize=draft.lyricSize+'px';preview.style.textAlign=draft.textAlign||'center';art.hidden=draft.layout==='text-only';art.style.width=(draft.coverSize||62)+'px';art.style.height=(draft.coverSize||62)+'px';art.style.borderRadius=(draft.cornerRadius??7)+'px';art.style.marginLeft=draft.layout==='cover-right'?'auto':'0';preview.style.setProperty('--preview-accent',c.accent||'var(--accent)');preview.style.setProperty('--preview-track',c.track||'var(--accent-soft)');}
 const nameLabel=node('label','样式名称 '),name=node('input');name.value=draft.name;name.maxLength=60;name.oninput=()=>draft.name=name.value;nameLabel.append(name);work.append(nameLabel);
 const modeLabel=node('label','明暗 '),mode=node('select');for(const [v,t]of [['follow','跟随主程序'],['light','浅色'],['dark','深色']])mode.append(new Option(t,v));mode.value=draft.mode;mode.onchange=()=>{draft.mode=mode.value;update();};modeLabel.append(mode);work.append(modeLabel);
 const sizeLabel=node('label','歌词字号 '),size=node('input');size.type='number';size.min='14';size.max='32';size.value=draft.lyricSize;size.oninput=()=>{draft.lyricSize=Number(size.value);update();};sizeLabel.append(size);work.append(sizeLabel);
 for(const [key,text,choices]of [['layout','布局',[['cover-left','封面在左'],['cover-right','封面在右'],['text-only','纯文字']]],['textAlign','文字对齐',[['left','左'],['center','居中'],['right','右']]]]){const label=node('label',text+' '),input=node('select');for(const [v,t]of choices)input.append(new Option(t,v));input.value=draft[key]||(key==='layout'?'cover-left':'center');input.onchange=()=>{draft[key]=input.value;update()};label.append(input);work.append(label)}
 for(const [key,text,min,max,fallback]of [['coverSize','封面尺寸',40,72,62],['cornerRadius','封面圆角',0,36,7]]){const label=node('label',text+' '),input=node('input');input.type='number';input.min=min;input.max=max;input.value=draft[key]??fallback;input.oninput=()=>{draft[key]=Number(input.value);update()};label.append(input);work.append(label)}
 const colors=node('div');colors.style.cssText='display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px';work.append(colors);
 for(const [key,text]of Object.entries({background:'背景',text:'文字',muted:'次要文字',accent:'强调色',track:'未播放进度条',popup:'菜单背景',onAccent:'播放键图标'})){
 const row=node('label'),inherit=node('input'),picker=node('input');inherit.type='checkbox';inherit.checked=!draft.colors[key];picker.type='color';picker.value=draft.colors[key]||({background:'#fcf9f8',text:'#302c38',muted:'#97838e',accent:'#ee466c',track:'#f5dce4',popup:'#fffdfb',onAccent:'#ffffff'}[key]);picker.disabled=inherit.checked;
 inherit.onchange=()=>{picker.disabled=inherit.checked;if(inherit.checked)delete draft.colors[key];else draft.colors[key]=picker.value;update();};picker.oninput=()=>{draft.colors[key]=picker.value;update();};row.append(node('span',text+' '),picker,inherit,document.createTextNode('跟随主程序'));colors.append(row);
 }
 const actions=node('div');actions.style.cssText='display:flex;gap:8px;flex-wrap:wrap';work.append(actions);
 function button(text,fn){const b=node('button',text);b.className='set-mini-button';b.onclick=async()=>{b.disabled=true;try{const result=await fn();if(result?.styles)paint(result);if(result?.canceled)status.textContent='已取消';else status.textContent='已完成并保存';}catch(e){status.textContent='未保存：'+e.message;}finally{b.disabled=false;}};actions.append(b);}
 select.onchange=async()=>{try{paint(await request('select',{id:select.value}));}catch(e){status.textContent=e.message;select.value=data.selected;}};
 button('另存并使用',()=>request('save',{json:JSON.stringify(draft)}));button('导入样式…',()=>request('import'));button('导出当前样式…',()=>request('export',{id:data.selected}));if(data.selected.startsWith('user-'))button('删除此用户样式',()=>request('remove',{id:data.selected}));
 const details=node('details'),summary=node('summary','用文件制作样式');details.append(summary,node('p','导出一个样式作为模板，用文本编辑器修改名称、明暗、颜色和歌词字号，再导入。格式为 celeste-mini-style，版本 1。支持配色、歌词字号、封面位置/尺寸/圆角和文字对齐；不执行 HTML 或脚本。'));work.append(details);update();
 }
}
window.CelesteMiniStyles={mount};
})();
