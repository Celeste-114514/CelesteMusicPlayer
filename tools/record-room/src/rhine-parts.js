import * as T from 'three';
import { RoundedBoxGeometry } from 'three/addons/geometries/RoundedBoxGeometry.js';
import { mergeVertices } from 'three/addons/utils/BufferGeometryUtils.js';


const cache=new Map();
function cached(key,fn){if(!cache.has(key))cache.set(key,fn());return cache.get(key);}
export function group(parent,name,x=0,y=0,z=0){
  const g=new T.Group();g.name=name;g.position.set(x,y,z);parent.add(g);return g;
}
export function mesh(g,name,geo,m,x,y,z){
  const obj=new T.Mesh(geo,m);obj.name=name;obj.position.set(x,y,z);g.add(obj);return obj;
}
export function box(g,name,m,x,y,z,w,h,d,r=0,segments=1){
  const geo=cached(['box',w,h,d,r,segments].join(','),()=>{
    const geo=r?mergeVertices(new RoundedBoxGeometry(w,h,d,segments,r)):new T.BoxGeometry(w,h,d);
    geo.clearGroups();return geo;
  });
  return mesh(g,name,geo,m,x,y,z);
}
export function tube(g,name,m,x,y,z,r,h,axis='y',n=48,r2=r){
  const geo=cached(['tube',r,r2,h,n].join(','),()=>new T.CylinderGeometry(r,r2,h,n));
  const o=mesh(g,name,geo,m,x,y,z);
  if(axis==='x')o.rotation.z=-Math.PI/2;
  if(axis==='z')o.rotation.x=Math.PI/2;return o;
}
export function torus(g,name,m,x,y,z,r,t,axis='y',segments=96){
  const geo=cached(['torus',r,t,segments].join(','),()=>new T.TorusGeometry(r,t,7,segments));
  const o=mesh(g,name,geo,m,x,y,z);
  if(axis==='y')o.rotation.x=-Math.PI/2;
  if(axis==='x')o.rotation.y=Math.PI/2;return o;
}
export function face(g,name,m,x,y,z,w,h,side='front'){
  const o=mesh(g,name,cached(['face',w,h].join(','),()=>new T.PlaneGeometry(w,h)),m,x,y,z);
  if(side==='back')o.rotation.y=Math.PI;
  if(side==='top')o.rotation.x=-Math.PI/2;return o;
}
export function roundPanel(w,d,r,holes,thick){
  const s=new T.Shape(),l=-w/2,b=-d/2,rr=w/2,t=d/2;
  s.moveTo(l+r,b);s.lineTo(rr-r,b);s.quadraticCurveTo(rr,b,rr,b+r);
  s.lineTo(rr,t-r);s.quadraticCurveTo(rr,t,rr-r,t);s.lineTo(l+r,t);s.quadraticCurveTo(l,t,l,t-r);
  s.lineTo(l,b+r);s.quadraticCurveTo(l,b,l+r,b);
  for(const [x,z,rad] of holes){const h=new T.Path();h.absarc(x,-z,rad,0,2*Math.PI,true);s.holes.push(h);}
  const geo=new T.ExtrudeGeometry(s,{depth:thick,bevelEnabled:true,bevelThickness:.004,bevelSize:.004,bevelSegments:2,curveSegments:40});
  geo.rotateX(-Math.PI/2);return geo;
}
