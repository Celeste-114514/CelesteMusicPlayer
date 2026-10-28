/* Give Windows caption rectangles, not a stream of synthetic move commands. */
(() => {
  if (!window.chrome?.webview) return;
  let frame=0, last='';
  const interactive='button,input,select,textarea,a,label,.header-tools,.header-actions,.np-head-actions,.window-actions';
  function update(){
    frame=0;
    const rects=[], scale=window.devicePixelRatio||1;
    if(!document.body.classList.contains('is-fullscreen')) {
      const detail=document.getElementById('now-playing');
      const surface=detail&&!detail.hidden&&getComputedStyle(detail).display!=='none'
        ?detail.querySelector('.np-head'):document.querySelector('.titlebar');
      if(surface&&surface.getClientRects().length){
        const r=surface.getBoundingClientRect();
        let spans=[[r.left,r.right]];
        document.querySelectorAll(interactive).forEach(el=>{
          if(!el.getClientRects().length)return;
          const style=getComputedStyle(el);
          if(style.visibility!=='visible'||style.display==='none')return;
          const b=el.getBoundingClientRect();
          if(b.bottom<=r.top||b.top>=r.bottom)return;
          spans=spans.flatMap(([l,h])=>b.right<=l||b.left>=h?[[l,h]]:[[l,Math.max(l,b.left)],[Math.min(h,b.right),h]]).filter(([l,h])=>h-l>2);
        });
        spans.forEach(([l,h])=>rects.push({x:Math.round(l*scale),y:Math.round(r.top*scale),width:Math.floor((h-l)*scale),height:Math.floor(r.height*scale)}));
      }
    }
    const key=JSON.stringify(rects);if(key===last)return;last=key;
    window.CelesteShell.send('window',{action:'caption-regions',rects});
  }
  const schedule=()=>{if(!frame)frame=requestAnimationFrame(update);};
  window.addEventListener('resize',schedule);
  document.addEventListener('celeste:theme-applied',schedule);
  document.addEventListener('celeste:now-playing',schedule);
  document.addEventListener('celeste:message',e=>{if(['celeste.window','celeste.theme'].includes(e.detail.kind))schedule();});
  new MutationObserver(schedule).observe(document.body,{attributes:true,attributeFilter:['class']});
  const detail=document.getElementById('now-playing');if(detail)new MutationObserver(schedule).observe(detail,{attributes:true,attributeFilter:['class','hidden','style']});
  const bar=document.querySelector('.titlebar');if(bar)new ResizeObserver(schedule).observe(bar);
  const orbit=document.querySelector('.corner-orbit');
  if(orbit){
    new MutationObserver(schedule).observe(orbit,{attributes:true,attributeFilter:['class','style']});
    orbit.addEventListener('transitionend',schedule);
  }
  schedule();
})();
