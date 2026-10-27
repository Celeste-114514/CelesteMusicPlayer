(() => {
  'use strict';
  const el=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;};
  const motion=()=>window.CelesteMotion;
  window.CelesteTagTools={mount(panel,kind,{request,targets,current,writeRows,notify,reload}){
    let alive=true,epoch=0,selected=-1,detail=null,timer=0,searchId='';const root=el('div',undefined,'tags-tool-page');panel.append(root);
    const status=el('p','','panel-note');status.setAttribute('role','status');
    let failBox=null;
    const field=(name,label,value='',type='text')=>{const wrap=el('label',undefined,'tags-tool-field'),input=el('input');input.name=name;input.type=type;input.value=value;input.setAttribute('aria-label',label);wrap.append(el('span',label),input);return {wrap,input};};
    const select=(name,label,options)=>{const wrap=el('label',undefined,'tags-tool-field'),input=el('select');input.name=name;input.setAttribute('aria-label',label);options.forEach(([id,text])=>{const o=el('option',text);o.value=id;input.append(o);});wrap.append(el('span',label),input);return {wrap,input};};
    const button=(text,fn,cls='secondary')=>{const b=el('button',text,cls);b.type='button';b.addEventListener('click',async()=>{b.disabled=true;try{await fn();}catch(e){status.textContent=e.message;notify(e.message);}finally{b.disabled=false;}});return b;};
    const ids=()=>{const list=targets();if(!list.length)throw new Error('先在右侧选择文件。');return list;};
    const row=current()||{};
    if(kind==='online'||kind==='lyrics'){
      const form=el('form',undefined,'tags-tool-form'),title=field('title','标题',row.title||''),artist=field('artist','艺术家',row.artist||''),album=field('album','专辑',row.album||'');
      const source=select('source','来源',kind==='online'?[['Both','MusicBrainz + iTunes'],['All','全部来源'],['MusicBrainz','MusicBrainz'],['iTunes','iTunes'],['Discogs','Discogs'],['Last.fm','Last.fm']]:[['LRClib','LRClib'],['QQ','QQ 音乐']]);
      form.append(title.wrap,artist.wrap);if(kind==='online')form.append(album.wrap);form.append(source.wrap);
      const search=el('button','搜索','primary');search.type='submit';form.append(search);root.append(form,status);
      const results=el('div',undefined,'tags-online-results'),preview=el('div',undefined,'tags-online-preview');root.append(results,preview);
      let translation=null,target=null,location=null;
      if(kind==='lyrics'){
        translation=field('translation','带翻译','','checkbox');translation.input.checked=true;
        target=select('target','写入目标',[['Sidecar','歌曲旁的 .lrc 文件'],['TagPlain','内嵌歌词（纯文本）'],['TagTimestamped','内嵌歌词（保留时间戳）'],['Both','.lrc 文件 + 内嵌歌词']]);
        location=select('location','保存位置',[['songFolder','歌曲所在目录'],['lyricFolder','设置中的歌词文件夹']]);
        root.append(translation.wrap,target.wrap,location.wrap);
      }
      form.addEventListener('submit',async e=>{e.preventDefault();const stamp=++epoch;selected=-1;detail=null;preview.replaceChildren();results.replaceChildren();search.disabled=true;status.textContent='正在搜索…';try{const data=await request(kind==='online'?'searchOnline':'searchLyrics',{title:title.input.value,artist:artist.input.value,album:album.input.value,source:source.input.value});if(!alive||stamp!==epoch)return;searchId=data.searchId||'';status.textContent=`找到 ${(data.candidates||[]).length} 个候选，请选择并核对。${data.note||''}`;(data.candidates||[]).forEach(c=>{const b=button((c.title||c.Title)+' · '+(c.artist||c.Artist)+' · '+(c.album||c.Album||'')+' · '+(c.source||c.Source||''),()=>choose(c.index));b.classList.add('tags-candidate');b.dataset.index=c.index;results.append(b);});motion()?.reveal(results,'list');}catch(error){if(alive&&stamp===epoch)status.textContent=error.message;}finally{search.disabled=false;}});
      async function choose(index){
        selected=index;const stamp=++epoch;preview.replaceChildren();results.querySelectorAll('button').forEach(b=>b.setAttribute('aria-pressed',Number(b.dataset.index)===index));status.textContent='正在加载预览…';
        let data;
        try{data=await request(kind==='online'?'onlineDetail':'lyricPreview',{index,searchId,title:title.input.value,translation:translation?.input.checked});}
        catch(error){if(!alive||stamp!==epoch)return;status.textContent=error.message;preview.append(button('重试',()=>choose(index),'secondary'));motion()?.reveal(preview,'list');return;}
        if(!alive||stamp!==epoch)return;detail=data;
        // 逐文件结果就地展开：失败的文件名和原因留在页面上，不要只弹一句"第一个失败"
        const showFailures=(list,hint)=>{const failed=(list||[]).filter(r=>!r.ok);if(!failed.length)return false;const box=el('div',undefined,'tags-tool-fail');box.append(el('p',hint,'panel-note'));failed.slice(0,8).forEach(r=>box.append(el('p',(r.name||r.id||'未知文件')+'：'+(r.error||'写入失败'),'panel-note')));if(failed.length>8)box.append(el('p',`另有 ${failed.length-8} 个文件失败。`,'panel-note'));failBox?.remove();failBox=box;status.after(box);motion()?.reveal(box,'list');return true;};
        if(kind==='online'){
          const c=data.candidate,checks=el('div',undefined,'tags-field-checks');if(data.cover){const img=el('img');img.src=data.cover;img.alt='在线候选封面';preview.append(img);}
          const values={title:c.title,artist:c.artist,album:c.album,albumArtist:c.albumArtist,year:c.year||'',track:c.track||'',trackCount:c.trackCount||'',disc:c.disc||'',discCount:c.discCount||'',genre:c.genre,lyrics:data.lyrics};
          const labels={title:'标题',artist:'艺术家',album:'专辑',albumArtist:'专辑艺术家',year:'年份',track:'曲目号',trackCount:'曲目总数',disc:'碟片号',discCount:'碟片总数',genre:'流派',lyrics:'歌词'};
          Object.entries(values).forEach(([key,value])=>{const check=field(key,labels[key]+(key==='lyrics'?'':`：${value}`),'','checkbox');check.input.checked=String(value).length>0;checks.append(check.wrap);});const cover=field('cover','封面','','checkbox');cover.input.checked=!!data.cover;checks.append(cover.wrap);preview.append(checks);
          const lyric=el('pre',data.lyrics||'未找到歌词','tags-lyric-preview');preview.append(lyric);
          preview.append(button('将勾选字段写入所选文件',async()=>{const targetIds=ids(),fields={};checks.querySelectorAll('input:checked').forEach(n=>{if(n.name!=='cover')fields[n.name]=String(values[n.name]??'');});if(Object.keys(fields).length){const ok=await writeRows(targetIds.map(id=>({id,fields})),'在线标签已写入');if(ok===false)return;}if(cover.input.checked){await request('coverOnline',{ids:targetIds,index,searchId});await reload();}notify('在线标签处理完成。');},'primary'));
        }else{
          const text=el('pre',data.text||'这个候选没有歌词。','tags-lyric-preview');preview.append(text,button('下载并保存',async()=>{
            const result=await request('lyricSave',{ids:ids(),index,searchId,translation:translation.input.checked,target:target.input.value,location:location.input.value});
            const failed=(result.results||[]).filter(r=>!r.ok);
            if(failed.length){
              if(!showFailures(result.results,'下面这些文件没有写入成功，可以单独重试：'))throw new Error(failed[0].error);
              status.textContent=`${failed.length} 个文件保存失败。`;
              return;
            }
            notify('歌词已保存。');await reload();
          },'primary'));
        }
        status.textContent='确认预览与选中文件相符后再写入。';
        motion()?.reveal(preview,'list');
      }
      translation?.input.addEventListener('change',()=>{if(selected>=0)choose(selected).catch(e=>status.textContent=e.message);});
    }else if(kind==='lyricsBatch'){
      root.append(el('p','按所选曲目的标题、艺术家匹配歌词，沿用原生匹配规则和跳过已有文件选项。','panel-note'));
      const source=select('source','来源',[['LRClib','LRClib'],['QQ','QQ 音乐']]),location=select('location','保存位置',[['songFolder','歌曲所在目录'],['lyricFolder','设置中的歌词文件夹']]),translation=field('translation','带翻译','','checkbox'),skip=field('skipExisting','跳过已有 .lrc 的曲目','','checkbox');skip.input.checked=true;
      const bar=el('progress');bar.max=1;bar.value=0;const log=el('p','','panel-note');
      const start=button('开始批量下载',async()=>{await request('lyricBatchStart',{ids:ids(),source:source.input.value,location:location.input.value,translation:translation.input.checked,skipExisting:skip.input.checked});poll();},'primary'),stop=button('停止',()=>request('lyricBatchStop'));
      root.append(source.wrap,location.wrap,translation.wrap,skip.wrap,start,stop,bar,log,status);
      async function poll(){if(!alive||document.hidden)return;try{const data=await request('lyricBatchStatus');if(!alive)return;start.disabled=data.running;stop.disabled=!data.running;bar.dataset.running=String(!!data.running);const p=data.progress;if(p){bar.max=p.Total||1;bar.value=p.Done;const line=`${p.Done} / ${p.Total} · ${p.Status}`;if(log.textContent!==line){log.textContent=line;motion()?.markChanged(log);}}const s=data.summary;if(s){const line=`已保存 ${s.Saved}，跳过 ${s.Skipped}，未匹配 ${s.NoMatch}，无歌词 ${s.NoLyric}，失败 ${s.Failed}${s.Canceled?' · 已停止':''}`;if(status.textContent!==line){status.textContent=line;motion()?.markChanged(status);}}if(data.error){status.textContent=data.error;}}catch(e){status.textContent=e.message;}}
      timer=setInterval(poll,1000);poll();
    }
    return ()=>{alive=false;epoch++;clearInterval(timer);};
  }};
})();
