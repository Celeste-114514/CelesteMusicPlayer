import * as T from 'three';

function map(name,w,h,draw) {
  const canvas=document.createElement('canvas');
  canvas.width=w;canvas.height=h;
  draw(canvas.getContext('2d'),w,h);
  const texture=new T.CanvasTexture(canvas);
  texture.name=name;texture.colorSpace=T.SRGBColorSpace;texture.anisotropy=8;
  return texture;
}
export function mat(name,color,roughness=.53,metalness=0,more={}) {
  const m=new T.MeshStandardMaterial({color,roughness,metalness,...more});m.name=name;return m;
}
export function type(g,s,x,y,size,color,weight=500,family='Arial',align='left') {
  g.fillStyle=color;g.font=weight+' '+size+'px '+family;g.textAlign=align;g.fillText(s,x,y);g.textAlign='left';
}
export function ink(name,w,h,draw) {
  return mat(name,'#ffffff',.62,0,{map:map(name,w,h,draw)});
}
