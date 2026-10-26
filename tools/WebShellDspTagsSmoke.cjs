const assert = require('node:assert/strict');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const {chromium} = require('playwright');
(async () => {
 const browser = await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});
 try {
  const page = await browser.newPage({viewport:{width:1440,height:960}}), errors=[];
  page.on('pageerror',e=>{errors.push(e.message);console.error('PAGEERROR:',e.message);});
  await page.addInitScript(() => {
   window.commands=[];window.failWrites=false;window.failDspWrites=false;window.writeDelay=5;
   const tracks=[{id:'test-a',title:'原始标题',artist:'艺术家',album:'专辑',albumArtist:'艺术家',year:'2024',track:'1',disc:'1',genre:'Jazz',file:'01 - Test.flac',lyrics:'原歌词'}, {id:'test-b',title:'第二首',artist:'艺术家',album:'专辑',albumArtist:'艺术家',year:'2024',track:'2',disc:'1',genre:'Jazz',file:'02 - Test.flac'}];
   let eq={enabled:true,preampDb:-2,presetId:'flat',bands:[{enabled:true,freq:125,gain:2,q:1,type:'Peaking'},{enabled:true,freq:4000,gain:-3,q:1.4,type:'HighShelf'}],undo:true,redo:false,userPresets:[],simple:{Bass:0,Vocal:0,Air:0,Warm:0}};
   const sw=(name,value=true)=>({name,kind:'switch',value,enabled:true}),slider=(name,value=0,min=-18,max=18)=>({name,kind:'slider',value,min,max,step:.1,enabled:true}),combo=(name,items)=>({name,kind:'combo',index:0,items,enabled:true});
   const controls={headroom:[sw('DspPowerHeadroom'),slider('AudioFxSafetyHeadroomSlider',-3,-24,0),sw('AudioFxSafetyLimiterToggle')],rg:[sw('DspPowerRg'),combo('AudioFxRgModeCombo',['关闭','单曲','专辑']),slider('AudioFxRgPreampSlider'),sw('AudioFxRgPreventClippingToggle')],src:[sw('DspPowerSrc'),combo('SrcRateCombo',['原采样率','96 kHz']),combo('SrcQualityCombo',['均衡','透明']),combo('SrcDitherCombo',['关闭','TPDF'])],comp:[sw('DspPowerComp'),sw('DspCompToggle'),slider('DspCompThresholdSlider',-12),slider('DspCompRatioSlider',4,1,20)],channel:[sw('DspPowerChannel'),sw('AudioFxChannelToggle'),slider('AudioFxChannelBalanceSlider',0,-1,1),slider('AudioFxChannelLeftGainSlider'),slider('AudioFxChannelRightGainSlider')],stereo:[sw('DspPowerField'),sw('DspFieldToggle'),slider('DspFieldWidthSlider',100,0,200),slider('DspFieldCenterSlider'),slider('DspFieldSideSlider')],matrix:[sw('DspPowerMatrix'),sw('DspMatrixToggle'),slider('DspMatrixLlSlider',1,-2,2),slider('DspMatrixRlSlider',0,-2,2),slider('DspMatrixLrSlider',0,-2,2),slider('DspMatrixRrSlider',1,-2,2)],fir:[sw('DspPowerFir'),sw('RoomIrEnabledToggle'),slider('RoomIrGainSlider'),slider('RoomTrimSlider')]};
   window.chrome||={};window.chrome.webview={addEventListener:(_,cb)=>window.deliver=data=>cb({data}),postMessage:m=>{
    commands.push(m);if(!m.requestId)return;
    let data={ok:true},ok=true,error='';
    if(m.command==='dsp') {
     if(m.operation==='get') data=structuredClone(eq);
     else if(m.operation==='opraInit'||m.operation==='opraRefresh')data={status:{VendorCount:1,ProductCount:1,EqCount:1,Source:'cache'},vendors:[{id:'brand',name:'测试耳机厂商',count:1}],recent:[],favorites:[],enabled:false};
     else if(m.operation==='opraSearch')data={products:[{ProductId:'product',Name:'测试耳机',VendorName:'测试耳机厂商',EqCount:1}]};
     else if(m.operation==='opraPresets')data={presets:[{EqId:'curve',Author:'测试来源',BandCount:2,PreampDb:-4}]};
     else if(m.operation==='opraPreview')data={eqId:'curve',title:'测试耳机厂商 测试耳机',author:'测试来源',count:2,preampDb:-4,bands:eq.bands,warnings:[],favorite:false};
     else if(m.operation==='opraFavorite')data={favorite:true};
     else if(m.operation==='set'){eq={...eq,enabled:m.enabled,preampDb:m.preampDb,bands:m.bands};data={ok:true};}
     else if(m.operation==='spectrum')data={available:true,bands:[.1,.2,.4,.6,.3,.2,.1]};
     else if(m.operation==='modules')data={modules:{eq:true}};
     else if(m.operation==='bypass'||m.operation==='setBypass')data={bypass:m.value||false};
     else if(m.operation==='module')data={controls:controls[m.module]||[],info:{irName:'room.wav',taps:2048,sampleRate:48000,channels:2,src:'44.1 → 96 kHz'},active:true};
     else if(m.operation==='setControl'){if(window.failDspWrites){ok=false;error='模拟参数提交失败';}else{const c=Object.values(controls).flat().find(c=>c.name===m.name);if(c){if(m.value!==undefined)c.value=m.value;if(m.index!==undefined)c.index=m.index;}}}
     else if(m.operation==='profiles')data={profiles:[{name:'日常',active:true,stamp:'10-26'}]};
     else if(m.operation==='rack')data={isDefault:true,modules:[{id:0,index:0,name:'EQ'},{id:1,index:1,name:'卷积'}]};
     else if(m.operation==='levels')data={session:true,peakDb:-6,clipCount:0,left:{peak:-6,rms:-18},right:{peak:-8,rms:-19},compressorGr:2.5,activeDsp:['EQ'],chain:'44.1 → 96 kHz'};
    }
    if(m.command==='tags') {
     if(m.operation==='list'){const rows=tracks.filter(t=>m.scopeIds?.includes(t.id));data={total:rows.length,rows:structuredClone(rows),offset:0};}
     else if(['addFiles','addFolder','changeDirectory'].includes(m.operation)){data=window.nextTagAdd||{ids:tracks.map(t=>t.id),count:tracks.length,directory:'C:/TestMusic'};delete window.nextTagAdd;}
     else if(m.operation==='rename'){const remap={};m.rows.forEach(r=>{const t=tracks.find(t=>t.id===r.id);remap[t.id]=t.id+'-renamed';t.id=remap[t.id];t.file=r.file+'.flac';});data={count:m.rows.length,remap};}
     else if(m.operation==='read')data={tracks:m.ids.map(id=>({...tracks.find(t=>t.id===id),ok:true}))};
     else if(m.operation==='write') {data={results:m.rows.map(r=>({id:r.id,ok:!window.failWrites,error:window.failWrites?'模拟磁盘占用；已还原':''}))};if(!window.failWrites)m.rows.forEach(r=>Object.assign(tracks.find(t=>t.id===r.id),r.fields));}
     else if(m.operation==='searchOnline')data={candidates:[{index:0,title:'候选标题',artist:'候选艺人',album:'候选专辑',source:'MusicBrainz'}]};
     else if(m.operation==='onlineDetail')data={candidate:{title:'候选标题',artist:'候选艺人',album:'候选专辑',year:2026},lyrics:'歌词预览'};
     else if(m.operation==='searchLyrics')data={candidates:[{index:0,Title:'歌词候选',Artist:'艺人',Album:'专辑'}]};
     else if(m.operation==='lyricPreview')data={text:'[00:01.00]测试歌词'};
     else if(m.operation==='lyricSave')data={results:m.ids.map(id=>({id,ok:true}))};
     else if(m.operation==='lyricBatchStatus')data={running:false,progress:{Done:2,Total:2,Status:'已完成'},summary:{Saved:2,Skipped:0,NoMatch:0,NoLyric:0,Failed:0}};
     else if(m.operation==='batchTransform')data={rows:m.ids.map(id=>({id,fields:{title:'处理后标题'}}))};
    }
    setTimeout(()=>deliver({kind:'celeste.reply',version:1,requestId:m.requestId,ok,data,error}),m.operation==='write'?window.writeDelay:5);
   }};
  });
  await page.goto(pathToFileURL(path.join(__dirname,'../outputs/webview2-shell/index.html')).href);
  await page.evaluate(()=>CelesteShell.navigate('dsp'));await page.waitForSelector('.eq-band');
  assert.equal(await page.locator('#category-page').isVisible(),false);
  assert.equal(await page.locator('.eq-band').count(),2);
  const nodes=await page.locator('.eq-curve .node-ring').evaluateAll(ns=>ns.map(n=>({r:n.getAttribute('r'),x:Number(n.getAttribute('cx')),y:Number(n.getAttribute('cy'))})));
  assert.equal(nodes[0].r,nodes[1].r,'first node must use the same size as every other band');
  assert.equal(await page.locator('.eq-guide').count(),2,'selected band has native dashed guides');
  assert.equal(await page.locator('.eq-node-frequency').count(),2,'native band frequency labels are visible');
  assert.ok(Math.abs(nodes[0].x-(52+Math.log2(125/20)/Math.log2(1000)*800))<.01,'first band shares the logarithmic frequency mapping');
  await page.locator('.opra-vendor-grid').getByRole('button').first().click();await page.waitForSelector('.opra-product-list button');await page.locator('.opra-product-list button').first().click();await page.waitForSelector('.opra-preset-list button');await page.locator('.opra-preset-list button').first().click();await page.waitForSelector('.opra-response');assert.equal(await page.locator('.opra-preset-list button').count(),1,'preset choices remain accessible beside preview');
  await page.getByRole('button',{name:'收藏曲线',exact:true}).last().click();await page.waitForSelector('.opra-preset-actions button');
  await page.getByRole('button',{name:'应用耳机校正',exact:true}).click();await page.waitForTimeout(80);assert.equal(await page.evaluate(()=>commands.some(c=>c.operation==='opraApply'&&c.eqId==='curve')),true,'headphone correction applies chosen curve through native service');
  await page.locator('.opra-browser').scrollIntoViewIfNeeded();await page.screenshot({path:path.join(__dirname,'../outputs/web-dsp-headphones-preview.png')});
  await page.getByLabel('滤波器类型',{exact:true}).selectOption('Notch');await page.waitForTimeout(80);
  assert.equal(await page.evaluate(()=>commands.findLast(c=>c.command==='dsp'&&c.operation==='set').bands[0].type),'Notch');
  await page.locator('.eq-workbench').getByRole('button',{name:'EQ 已启用',exact:true}).click();await page.waitForTimeout(80);
  assert.equal(await page.evaluate(()=>commands.findLast(c=>c.operation==='set').enabled),false,'EQ off must remain off');
  await page.getByRole('button',{name:'添加频段',exact:true}).click();await page.waitForTimeout(80);assert.equal(await page.locator('.eq-band').count(),3);
  for(const id of ['profiles','rack','headroom','rg','src','comp','channel','stereo','matrix','fir','safety']) {
   await page.locator(`[data-module="${id}"]`).click();await page.waitForTimeout(80);
   assert.ok(await page.locator('.dsp-module h2').count()>0,id+' renders');
   if(!['profiles','rack','safety'].includes(id))assert.equal(await page.locator(`[data-visual="${id}"]`).count(),1,id+' has ECHO presentation');
   for(const width of [1024,760]){await page.setViewportSize({width,height:960});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'DSP '+id+' width '+width);}await page.setViewportSize({width:1440,height:960});
   if(['headroom','src','channel','fir','safety'].includes(id))await page.screenshot({path:path.join(__dirname,`../outputs/web-dsp-${id}-preview.png`)});
  }
  const limiter=page.getByRole('button',{name:'安全限制器',exact:true});
  await limiter.click();await page.waitForTimeout(60);
  assert.equal(await limiter.getAttribute('aria-pressed'),'false');
  assert.equal(await page.evaluate(()=>commands.findLast(c=>c.operation==='setControl').module),'headroom','safety writes through the headroom module');
  await page.evaluate(()=>window.failDspWrites=true);await limiter.click();await page.waitForTimeout(60);
  assert.equal(await limiter.getAttribute('aria-pressed'),'false','failed write keeps actual limiter state');
  await page.evaluate(()=>window.failDspWrites=false);
  await page.locator('[data-module="headroom"]').click();await page.waitForTimeout(80);
  assert.match(await page.locator('.dsp-module-metric strong').first().textContent(),/^-3.0 dB$/,'headroom retains native attenuation sign');
  const headroom=page.getByLabel('输入余量',{exact:true});await headroom.fill('-6');await headroom.dispatchEvent('input');
  await page.locator('[data-module="rg"]').click();await page.waitForTimeout(250);
  assert.equal(await page.evaluate(()=>commands.findLast(c=>c.operation==='setControl'&&c.name==='AudioFxSafetyHeadroomSlider').module),'headroom','delayed slider write retains its source module');
  await page.locator('[data-module="channel"]').click();await page.waitForTimeout(80);
  const balance=page.getByLabel('左右平衡',{exact:true});await balance.fill('0.5');await balance.dispatchEvent('input');await balance.dispatchEvent('change');await page.waitForTimeout(80);
  assert.match(await page.locator('.dsp-channel-bias-head strong').textContent(),/偏右/);
  for(const width of [1440,1024,760]){await page.setViewportSize({width,height:960});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'DSP viewport '+width);}
  await page.setViewportSize({width:1440,height:960});await page.locator('[data-module="eq"]').click();await page.waitForTimeout(180);await page.screenshot({path:path.join(__dirname,'../outputs/web-dsp-eq-preview.png')});
  await page.evaluate(()=>CelesteShell.navigate('tags'));await page.waitForTimeout(180);
  assert.equal(await page.locator('.tag-table tbody tr').count(),0,'tag workspace starts empty');
  assert.equal(await page.evaluate(()=>commands.filter(c=>c.command==='tags'&&['read','list'].includes(c.operation)).length),0,'opening tags does not read library or music files');
  await page.getByRole('button',{name:'添加文件…',exact:true}).click();await page.waitForSelector('.tag-table tbody tr');
  assert.deepEqual(await page.locator('.tag-table th[data-column]').allTextContents().then(labels=>labels.map(t=>t.trim())),['标题','艺术家','专辑艺术家','专辑','年份','曲目','流派','时长','文件名','格式','位深采样率','码率','大小','作曲家','碟片号','曲目总数','BPM','注释']);
  const rowTitle=page.locator('.tag-table tbody tr').first().locator('[data-field="title"]');await rowTitle.click();
  assert.notEqual(await rowTitle.getAttribute('contenteditable'),'true','first click selects only');await page.waitForTimeout(60);await rowTitle.click();
  assert.equal(await rowTitle.getAttribute('contenteditable'),'true','second click on selected row edits');await rowTitle.press('Escape');
  await page.locator('.tags-column-picker summary').click();await page.getByLabel('显示注释',{exact:true}).uncheck();assert.equal(await page.locator('th[data-column="comment"]').isVisible(),false);await page.getByLabel('显示注释',{exact:true}).check();await page.locator('.tags-column-picker summary').click();
  await page.getByRole('button',{name:'排序…',exact:true}).click();await page.getByRole('button',{name:'添加规则',exact:true}).click();await page.getByLabel('第 1 排序字段',{exact:true}).selectOption('artist');await page.getByRole('button',{name:'添加规则',exact:true}).click();await page.getByLabel('第 2 排序字段',{exact:true}).selectOption('track');await page.getByLabel('第 2 排序方向',{exact:true}).selectOption('desc');await page.getByRole('button',{name:'应用排序',exact:true}).click();await page.waitForTimeout(80);
  assert.deepEqual(await page.evaluate(()=>commands.findLast(c=>c.operation==='list').sortRules),[{key:'artist',descending:false},{key:'track',descending:true}]);
  await page.locator('.tag-table-wrap').evaluate(n=>{n.style.maxHeight='50px';n.scrollTop=0;});const headerY=await page.locator('th[data-column="title"]').evaluate(n=>n.getBoundingClientRect().y);await page.locator('.tag-table-wrap').evaluate(n=>n.scrollTop=40);assert.ok(Math.abs(await page.locator('th[data-column="title"]').evaluate(n=>n.getBoundingClientRect().y)-headerY)<1,'header remains fixed while rows scroll');await page.locator('.tag-table-wrap').evaluate(n=>{n.style.maxHeight='';n.scrollTop=0;});
  const pollCount=await page.evaluate(()=>commands.filter(c=>c.operation==='spectrum').length);await page.waitForTimeout(220);assert.equal(await page.evaluate(()=>commands.filter(c=>c.operation==='spectrum').length),pollCount,'hidden EQ stops spectrum');
  await page.locator('.tag-table tbody tr').first().click();await page.waitForTimeout(100);
  const title=page.locator('#tag-field-title');await title.fill('改后标题');await page.getByRole('button',{name:/保存改动/}).click();await page.waitForTimeout(100);assert.equal(await page.locator('#tag-field-title').inputValue(),'改后标题');
  await page.evaluate(()=>window.writeDelay=250);await title.fill('保存期间锁定编辑');await page.getByRole('button',{name:/保存改动/}).click();
  assert.equal(await page.locator('.tags-layout').evaluate(n=>n.inert),true,'write locks the draft until completion');
  assert.equal(await page.getByRole('button',{name:'还原',exact:true}).isDisabled(),true);
  await page.waitForFunction(()=>!document.querySelector('.tags-layout').inert);await page.evaluate(()=>window.writeDelay=5);
  await page.locator('#tag-field-title').fill('保存失败保留草稿');await page.evaluate(()=>window.failWrites=true);await page.getByRole('button',{name:/保存改动/}).click();await page.waitForTimeout(80);assert.equal(await page.locator('#tag-field-title').inputValue(),'保存失败保留草稿');assert.equal(await page.getByRole('button',{name:/保存改动/}).isEnabled(),true);await page.evaluate(()=>window.failWrites=false);await page.getByRole('button',{name:'还原',exact:true}).click();await page.waitForTimeout(80);
  await page.locator('.tag-table tbody tr .col-pick input').nth(1).check();await page.locator('#tag-field-albumArtist').fill('统一艺人');await page.getByRole('button',{name:/保存改动/}).click();await page.waitForTimeout(100);const batch=await page.evaluate(()=>commands.findLast(c=>c.command==='tags'&&c.operation==='write'));assert.equal(batch.rows.length,2);assert.deepEqual(Object.keys(batch.rows[0].fields),['albumArtist']);
  await page.locator('.tags-subnav').getByRole('button',{name:'在线标签',exact:true}).click();await page.getByRole('button',{name:'搜索',exact:true}).click();await page.waitForSelector('.tags-candidate');await page.locator('.tags-candidate').click();await page.waitForSelector('.tags-field-checks');assert.match(await page.locator('.tags-online-preview').textContent(),/候选标题/);
  await page.locator('.tags-subnav').getByRole('button',{name:'在线歌词',exact:true}).click();await page.getByRole('button',{name:'搜索',exact:true}).click();await page.waitForSelector('.tags-candidate');await page.locator('.tags-candidate').click();await page.waitForSelector('.tags-lyric-preview');assert.match(await page.locator('.tags-lyric-preview').textContent(),/测试歌词/);
  await page.locator('.tags-subnav').getByRole('button',{name:'批量下载歌词',exact:true}).click();await page.waitForTimeout(80);assert.match(await page.locator('.tags-tool-page').textContent(),/已保存 2/);
  await page.locator('.tags-subnav').getByRole('button',{name:'批量工具',exact:true}).click();await page.getByRole('button',{name:'预览所选文件',exact:true}).click();await page.waitForTimeout(80);assert.match(await page.locator('.tags-transform-preview').first().textContent(),/处理后标题/);
  await page.evaluate(()=>CelesteTags.open(['test-b']));await page.waitForSelector('.tag-table tbody tr');
  assert.equal(await page.locator('.tag-table tbody tr').count(),1,'context entry opens only requested tracks');
  assert.equal(await page.locator('#tag-field-title').inputValue(),'第二首');
  assert.equal(await page.locator('[data-more="tags"]').isEnabled(),true,'transport tag entry is connected');
  await page.getByRole('button',{name:'添加文件…',exact:true}).click();await page.waitForFunction(()=>document.querySelectorAll('.tag-table tbody tr').length===2);
  await page.locator('.tag-table tbody tr').first().click();await page.waitForTimeout(80);
  await page.evaluate(()=>{document.querySelector('.main').scrollTop=0;document.querySelector('.tags-panel').scrollTop=0;});
  await page.screenshot({path:path.join(__dirname,'../outputs/web-tags-preview.png')});
  for(const width of [1440,1024,760]){await page.setViewportSize({width,height:960});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'tags viewport '+width);}
  await page.evaluate(()=>window.nextTagAdd={canceled:true});await page.getByRole('button',{name:'改变工作目录…',exact:true}).click();assert.equal(await page.locator('.tag-table tbody tr').count(),2,'canceling folder chooser preserves workspace');
  await page.evaluate(()=>window.nextTagAdd={ids:['test-b'],count:1,directory:'C:/OtherMusic'});await page.getByRole('button',{name:'改变工作目录…',exact:true}).click();await page.waitForFunction(()=>document.querySelectorAll('.tag-table tbody tr').length===1);assert.match(await page.locator('.tags-working-directory').textContent(),/OtherMusic/);
  await page.locator('.tag-table tbody tr').click();await page.locator('.tags-subnav').getByRole('button',{name:'批量工具',exact:true}).click();await page.getByRole('button',{name:'预览文件名',exact:true}).click();await page.getByRole('button',{name:'应用重命名',exact:true}).click();await page.waitForTimeout(100);assert.deepEqual(await page.evaluate(()=>commands.findLast(c=>c.operation==='list').scopeIds),['test-b-renamed'],'renamed file retains explicit workspace scope');assert.equal(await page.locator('.tag-table tbody tr').count(),1);
  await page.locator('.tags-column-picker summary').click();await page.getByLabel('显示注释',{exact:true}).uncheck();await page.reload();await page.evaluate(()=>CelesteShell.navigate('tags'));await page.waitForSelector('.tag-table');assert.equal(await page.locator('th[data-column="comment"]').isVisible(),false,'column visibility survives reload');assert.equal(await page.locator('.tag-table tbody tr').count(),0,'new tag session starts with empty workspace');assert.equal(await page.evaluate(()=>commands.some(c=>c.command==='tags'&&['list','read'].includes(c.operation))),false,'empty session does not read music');
  assert.deepEqual(errors,[]);
  console.log('PASS: 12 DSP pages, native EQ filter/enable/add and first-node geometry, ECHO visuals, channel balance, hidden spectrum cleanup; tags save/failure draft/multi-field writes, online/lyric previews, batch progress, transforms and scoped tag entries; responsive widths and no JS errors.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
