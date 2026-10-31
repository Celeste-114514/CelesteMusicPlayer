/* Give Windows caption rectangles, not a stream of synthetic move commands. */
(() => {
  if (!window.chrome?.webview) return;
  let frame=0, last='', dragging=false;
  const interactive='button,input,select,textarea,a,label,.header-tools,.header-actions,.np-head-actions,.window-actions';
  // 当前真正承担拖动的那块表面：正在播放页打开时是它的头部，否则是主标题栏。
  function pickSurface(){
    const detail=document.getElementById('now-playing');
    const live=detail&&!detail.hidden&&getComputedStyle(detail).display!=='none'?detail.querySelector('.np-head'):null;
    return live||document.querySelector('.titlebar');
  }
  function update(){
    frame=0;
    const rects=[], scale=window.devicePixelRatio||1;
    if(!document.body.classList.contains('is-fullscreen')) {
      const surface=pickSurface();
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
  /* 主窗已经没有原生边框了（聚焦时那圈主题色带就是它画的），改大小由这里接管：
     离窗口边 6px 内按下 -> 发 resize-start 带边名，宿主起循环按光标位移重设窗口。 */
  const EDGE=6;
  const CURSORS={n:'ns-resize',s:'ns-resize',e:'ew-resize',w:'ew-resize',
                 ne:'nesw-resize',sw:'nesw-resize',nw:'nwse-resize',se:'nwse-resize'};
  function edgeAt(x,y){
    const w=window.innerWidth,h=window.innerHeight;
    let e='';
    if(y<EDGE)e+='n';else if(y>=h-EDGE)e+='s';
    if(x<EDGE)e+='w';else if(x>=w-EDGE)e+='e';
    return e;
  }
  let edgeCursor='';
  function trackEdge(e){
    if(document.body.classList.contains('is-fullscreen'))return;
    const edge=edgeAt(e.clientX,e.clientY);
    if(edge===edgeCursor)return;
    edgeCursor=edge;
    document.body.style.cursor=edge?CURSORS[edge]:'';
  }
  document.addEventListener('pointermove',trackEdge,{passive:true});
  /* caption-regions 交给原生命中测试；这条 pointer 路径是它失效时的兜底，
     也是迷你窗口一直在用的那条（网页握着指针事件，不依赖 WM_NCHITTEST 能否到父窗）。 */
  function begin(e){
    if(e.button!==0||document.body.classList.contains('is-fullscreen'))return;
    const edge=edgeAt(e.clientX,e.clientY);
    if(edge){e.preventDefault();window.CelesteShell.send('window',{action:'resize-start',edge});return;}
    const target=e.target instanceof Element?e.target:null;
    if(!target||target.closest(interactive))return;
    const surface=pickSurface();
    if(!surface||!surface.contains(target))return;
    e.preventDefault();
    try{surface.setPointerCapture(e.pointerId);}catch{}
    dragging=true;
    window.CelesteShell.send('window',{action:'drag-start'});
  }
  function end(){
    if(!dragging)return;
    dragging=false;
    window.CelesteShell.send('window',{action:'drag-end'});
  }
  document.addEventListener('pointerdown',begin,true);
  for(const name of ['pointerup','pointercancel','lostpointercapture','blur'])document.addEventListener(name,end,true);
  schedule();
})();
