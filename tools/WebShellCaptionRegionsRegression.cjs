const vm=require('node:vm'),fs=require('node:fs'),assert=require('node:assert/strict');
const source=fs.readFileSync('outputs/webview2-shell/window-drag.js','utf8');
const rect=(left,top,right,bottom)=>({left,top,right,bottom,width:right-left,height:bottom-top});
function run({scale=1,fullscreen=false,detailOpen=false}={}){
 const messages=[],frames=[];
 const el=r=>({hidden:false,getClientRects:()=>[r],getBoundingClientRect:()=>r});
 const title=el(rect(0,0,1000,58)),head=el(rect(0,0,1000,70));
 const detail={hidden:!detailOpen,querySelector:()=>head};
 const controls=[el(rect(0,0,180,58)),el(rect(800,0,1000,58))];
 const observer=class{observe(){}};
 const document={body:{classList:{contains:()=>fullscreen}},getElementById:()=>detail,querySelector:()=>title,querySelectorAll:()=>controls,addEventListener(){}};
 vm.runInNewContext(source,{window:{chrome:{webview:{}},devicePixelRatio:scale,CelesteShell:{send:(kind,data)=>messages.push(data)},addEventListener(){}},document,getComputedStyle:()=>({display:detailOpen?'block':'none'}),MutationObserver:observer,ResizeObserver:observer,requestAnimationFrame:f=>{frames.push(f);return 1;}});
 frames.shift()();return JSON.parse(JSON.stringify(messages[0].rects));
}
assert.deepEqual(run(),[{x:180,y:0,width:620,height:58}]);
assert.deepEqual(run({scale:2}),[{x:360,y:0,width:1240,height:116}]);
assert.deepEqual(run({fullscreen:true}),[]);
assert.equal(run({detailOpen:true})[0].height,70);
console.log('PASS: caption rectangles exclude controls, scale to physical pixels, clear fullscreen and use detail header');
