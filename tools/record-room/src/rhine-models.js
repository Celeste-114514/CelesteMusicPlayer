import * as T from 'three';
import {group,mesh,box,tube,torus,face,roundPanel} from './rhine-parts.js';
import {ink,mat,type} from './rhine-materials.js';

// All dimensions within a study are local. Root scales convert to glTF metres.
const half=Math.PI/2;
export const ARTISTS_PER_FACE = 6;
const num=i=>String(i).padStart(2,'0');
const ivory=mat('Satin ivory ceramic','#cfc7b9',.55,.07);
const rim=mat('Polished opal bevel','#eae3d7',.30,.11);
const insert=mat('Warm grey inner chassis','#9e9b91',.66,.12);
const joint=mat('Inset gasket','#686c64',.77,.06);
const brass=mat('Muted amber ceramic','#a4774e',.48,.22);
const nickel=mat('Vapour finished aluminium','#b7b5a9',.41,.47);
const frost=mat('Frosted resin','#d7cfc2',.46,.07,{transparent:true,opacity:.34,depthWrite:false});
const clear=mat('Moulded optical skin','#e9e7e1',.22,.13,{transparent:true,opacity:.13,depthWrite:false});
const papers=['#dad5c8','#c1c1ba','#e7dfd1','#b5b6b0','#d5d1c8'].map(c=>mat('Archival stock '+c,c,.83));
const tau=Math.PI*2;
function label(name,w,h,draw){
  const printed=ink(name,w,h,draw);
  const m=new T.MeshBasicMaterial({map:printed.map,transparent:true,depthWrite:false,toneMapped:false});m.name=name;return m;
}
function mark(g,name,m,x,y,z,w,h,side){
  const o=face(g,name,m,x,y,z,w,h,side);o.renderOrder=4;return o;
}
function pin(g,x,y,z,r=.021,axis='z'){
  tube(g,'Inset ceramic collar',rim,x,y,z,r*1.36,.012,axis,24);
  const offset=axis==='z'?[0,0,.008]:[0,.008,0];
  tube(g,'Flush fixing',nickel,x+offset[0],y+offset[1],z+offset[2],r,.008,axis,24);
}
function badge(){
  const printed=ink('Archive label stock',1024,256,()=>{});
  return new T.MeshBasicMaterial({map:printed.map,toneMapped:false});
}
export function printCover(g,album,artist,w,h){
    g.fillStyle='#e8e1d3';g.fillRect(0,0,w,h);
    g.save();g.beginPath();g.rect(14,14,612,612);g.clip();
    g.fillStyle='#a0aaaf';g.fillRect(28,338,584,268);
    g.strokeStyle='#7d827a';g.lineWidth=1.5;
    for(let k=0;k<4;k++){g.beginPath();g.arc(479,360,101+k*50,0,tau);g.stroke();}
    g.fillStyle='#ae8562';g.beginPath();g.arc(487,213,34,0,tau);g.fill();
    g.restore();
    g.fillStyle='#363b35';g.font='600 36px "Segoe UI", sans-serif';g.fillText(album.title,32,73,575);
    g.font='24px "Segoe UI", sans-serif';g.fillText(artist,33,116,573);
}
function jacket(parent,name,openSlot=false){
  const g=group(parent,name);
  box(g,'Clear cast back shell',frost,0,0,-.057,1.39,1.37,.058,.028,2);
  box(g,'Paper and tray',papers[0],0,0,openSlot?-.048:-.011,1.302,1.286,openSlot?.009:.042,.005);
  const cover=face(g,'Archive cover print',mat('Cover paper',0xffffff,.7),0,0,.012,1.275,1.266);
  box(g,'Removable frosted cover',clear,0,0,.058,1.385,1.366,.039,.02,2);
  for(const s of [-1,1]){
    box(g,'Side capture rail',rim,s*.679,0,.001,.036,1.329,.136,.012,2);
    if(openSlot&&s>0){
      for(const z of [-.065,.066])box(g,'Polished insertion slot',rim,0,.664,z,1.337,.028,.026,.006);
    }else box(g,'Edge seam',frost,0,s*.664,0,1.337,.037,.133,.011);
    for(const y of [-.628,.628])pin(g,s*.640,y,.076,.018);
  }
  const tab=box(g,'Selection tab',brass,-.420,.677,.012,.16,.051,.138,.009);
  g.userData={cover,tab};
  return g;
}
// Sample authored motion at a stable cadence. glTF then plays identically in other viewers.
const ease=x=>x*x*(3-2*x);
function phase(t,a,b){return ease(T.MathUtils.clamp((t-a)/(b-a),0,1));}
const mix=T.MathUtils.lerp;
const rotation=(x=0,y=0,z=0)=>new T.Quaternion().setFromEuler(new T.Euler(x,y,z,'YXZ')).toArray();
export function cabinet(root){
  const deck=group(root,'Static_turntable');
  tube(deck,'Isolated base',insert,0,.061,0,1.27,.120,'y',96);
  tube(deck,'Swivel bearing cover',ivory,0,.126,0,1.32,.076,'y',96);
  torus(deck,'Rotation witness ring',nickel,0,.156,0,1.248,.013);
  const cabinet=group(root,'Rotating_archive'),bottom=.242,high=3.36;
  for(const y of [bottom,high]){
    box(cabinet,'Perimeter slab',ivory,0,y,0,3.34,.114,3.34,.029,3);
    box(cabinet,'Recessed split line',insert,0,y+(y===bottom?.062:-.062),0,3.255,.017,3.255,.008);
  }
  box(cabinet,'Recessed lid insert',frost,0,high+.068,0,3.12,.038,3.12,.022);
  for(const x of [-1.604,1.604])for(const z of [-1.604,1.604]){
    box(cabinet,'Vertical corner post',ivory,x,1.8,z,.124,3.06,.124,.023,2);pin(cabinet,x,high+.062,z,.026,'y');
  }
  const trays=[];
  for(let f=0;f<4;f++){
    const wall=group(cabinet,'Face_'+num(f+1));wall.rotation.y=f*half;
    box(wall,'Interior shadow plane',insert,0,1.804,1.354,2.68,2.99,.038,.01);
    for(let j=0;j<ARTISTS_PER_FACE;j++){
      const base=3.043-j*.492,d=group(wall,'Tray_'+f+'_'+j,0,base,1.556);
      box(d,'Front bottom rim',rim,0,-.207,.016,3.075,.034,.148,.012);
      box(d,'Top front rim',rim,0,.207,.016,3.075,.038,.148,.012);
      box(d,'Lower tray bed',ivory,0,-.187,-.22,2.15,.052,.54,.011);
      for(const s of [-1,1]){
        box(d,'Vertical end cap',ivory,s*1.514,0,.013,.045,.426,.151,.011);
        box(d,'Telescopic guide',nickel,s*1.042,-.145,-.223,.022,.058,.54,.008);
      }
      box(d,'Recessed spine well',papers[1],.744,-.002,-.403,1.56,.382,.034,.009);
      const spines=Array.from({length:12},(_,k)=>box(d,'Edition spine',papers[(j+k)%5],0,.010,-.147,.100,.363,.442,.007));
      const panel=box(d,'Smoked forepane',frost,0,0,.102,3.04,.405,.025,.012);
      box(d,'Name plate mount',ivory,-.775,0,.118,1.46,.354,.026,.013,2);
      const legend=face(d,'Printed artist card',badge(),-.775,0,.134,1.43,.331);
      const tab=box(d,'Selected file tab',brass,-1.54,.031,.102,.064,.204,.059,.007);
      trays.push({body:d,spines,legend,tab,panel});
    }
  }
  return {cabinet,trays};
}
export const EDITIONS_PER_ROW = 6;
export function editions(root){
  const rows=[group(root,'Lower_archive_bed'),group(root,'Upper_archive_bed',0,1.34,-.94)];
  const buildBed = base => {
  box(base,'Satin bed',ivory,0,.073,0,4.99,.083,1.42,.026,2);
  for(const z of [-.70,.70]){
    box(base,'Translucent raised lip',frost,0,.164,z,4.99,.210,.058,.017,2);
    box(base,'Polished upper arris',rim,0,.265,z,5.0,.023,.062,.009);
  }
  for(const x of [-2.471,2.471])box(base,'Tray return',frost,x,.158,0,.06,.213,1.41,.018);
  box(base,'Tray tab',brass,-2.47,.154,.731,.047,.154,.029,.004);
  };
  rows.forEach(buildBed);
  for(const x of [-2.29,2.29]) {
    box(rows[1],'Upper tier support',ivory,x,-.636,-.31,.14,1.35,.69,.032,2);
    box(rows[1],'Upper tier foot',rim,x,-1.29,-.31,.30,.09,.91,.028,2);
  }
  const legend=face(rows[0],'Tray identification',badge(),-1.875,.159,.737,.80,.168);
  const cases=rows.flatMap((bed,row)=>Array.from({length:EDITIONS_PER_ROW},(_,i)=>jacket(bed,'Sleeve_'+num(row*EDITIONS_PER_ROW+i+1))));
  return {legend,cases,upper:rows[1]};
}

function ring(g,name,m,x,y,z,inside,outside){
  const geo=new T.RingGeometry(inside,outside,128);geo.rotateX(-half);
  return mesh(g,name,geo,m,x,y,z);
}
export function transport(root){
  const cx=.706,cz=.08;
  const chassis=group(root,'Optical_transport',cx,0,cz);
  const caseX=-1.353,caseZ=-.16;
  const dock=group(root,'Edition_cradle',caseX,0,caseZ);
  box(dock,'Cradle foot',ivory,0,.072,0,1.55,.129,.52,.034,2);
  box(dock,'Rear retaining lip',frost,0,.201,-.123,1.487,.254,.059,.018);
  box(dock,'Front retaining lip',frost,0,.148,.131,1.49,.171,.058,.014);
  const sleeve=jacket(dock,'Selected_sleeve',true);sleeve.position.set(0,.809,0);
  for(const x of [-.81,.81])for(const z of [-.76,.76])tube(chassis,'Isolation foot',insert,x,.041,z,.088,.075);
  box(chassis,'Instrument bottom',ivory,0,.129,0,2.024,.148,1.93,.046,3);
  box(chassis,'Case joint',insert,0,.202,0,1.992,.016,1.905,.015);
  mesh(chassis,'Machined carrier plate',roundPanel(2.015,1.928,.049,[[0,0,.753]],.081),ivory,0,.213,0);
  tube(chassis,'Recessed optical sump',insert,0,.210,0,.747,.024,'y',112);
  ring(chassis,'Suspended optical bed',nickel,0,.229,0,.248,.694);
  torus(chassis,'Outer optical surround',rim,0,.288,0,.742,.018);
  torus(chassis,'Secondary stepped rim',ivory,0,.244,0,.687,.014);
  for(const s of [-1,1])box(chassis,'Pickup guide rod',nickel,s*.055,.249,.289,.012,.018,.427,.004);
  box(chassis,'Pickup carriage',ivory,0,.252,.351,.160,.023,.114,.014);
  tube(chassis,'Pickup lens',joint,0,.270,.351,.027,.009);
  tube(chassis,'Central carrier',ivory,0,.256,0,.162,.078,'y',72);
  torus(chassis,'Amber spindle seal',brass,0,.299,0,.109,.018);
  tube(chassis,'Tapered spindle',nickel,0,.312,0,.064,.039,'y',64,.070);
  const lid=group(chassis,'Dust_cover_hinge',0,.326,-.887);
  box(lid,'Frosted dust cover',clear,0,.027,.87,1.951,.052,1.758,.027,3);
  for(const s of [-1,1]){
    box(lid,'Ground side edge',frost,s*.968,.013,.876,.021,.063,1.731,.009);
    box(lid,'Front and rear reveals',frost,0,.013,.871+s*.863,1.937,.057,.022,.009);
    tube(lid,'Captive hinge',ivory,s*.691,.004,0,.045,.217,'x');
    tube(lid,'Hinge sleeve',nickel,s*.691,.004,0,.025,.244,'x');
    for(const z of [-.84,.84])pin(chassis,s*.905,.301,z,.025,'y');
  }
  for(let i=0;i<8;i++){const slit=box(chassis,'Acoustic aperture',insert,.418+i*.044,.299,.815,.012,.005,.102,.003);slit.rotation.y=-.38;}
  const id=label('Drive model and serial',540,96,c=>{type(c,'CELESTE   /   OPTICAL DRIVE',10,40,25,'#393d36',600);type(c,'CD 01      •      STEREO',10,77,20,'#5e625a',500,'monospace');});
  mark(chassis,'Face lettering',id,-.518,.126,.967,.766,.118);
  tube(chassis,'Pilot indicator',brass,.875,.131,.970,.021,.012,'z');
  box(chassis,'Play key recess',joint,.556,.122,.956,.327,.144,.041,.022,2);
  const key=group(chassis,'Physical playback switch',.556,.122,.988);
  box(key,'Tactile amber keycap',brass,0,0,0,.293,.119,.058,.025,3);
  const play=label('Play engraving',160,80,g=>{
    g.fillStyle='#fcf2df';g.beginPath();g.moveTo(63,19);g.lineTo(110,40);g.lineTo(63,61);g.closePath();g.fill();
  });
  const pause=label('Pause engraving',160,80,g=>{
    g.fillStyle='#fcf2df';g.fillRect(57,19,15,42);g.fillRect(88,19,15,42);
  });
  const keyIcon=mark(key,'Play and pause engraving',play,0,0,.031,.213,.101);
  const disc=group(root,'Selected_optical_disc');
  ring(disc,'Optical lead-out',nickel,0,.004,0,.579,.598);
  const discFace=ring(disc,'Printed optical face',mat('Optical print',0xffffff,.65),0,.006,0,.109,.580);
  const back=ring(disc,'Metalised readout layer',nickel,0,-.005,0,.081,.598);back.rotation.z=Math.PI;
  ring(disc,'Clear clamp area',frost,0,.005,0,.075,.110);
  torus(disc,'Laser cut clear edge',nickel,0,0,0,.598,.004);
  torus(disc,'Centre bore',rim,0,0,0,.075,.003);
  torus(disc,'Matrix mould',nickel,0,.006,0,.114,.004);
  const pose=t=>{
    const rise=phase(t,.5,1.95),travel=phase(t,1.98,3.50),drop=phase(t,3.51,4.69);
    return [mix(caseX,cx,travel),.810+1.355*rise-.31*travel-1.55*drop,
      mix(caseZ+.001,cz,travel)+.20*Math.sin(travel*Math.PI)];
  };
  const orient=t=>half*(1-phase(t,2.02,3.5));
  const hinge=t=>rotation(-1.07*phase(t,.05,.99)*(1-phase(t,4.69,5.69)));
  return {sleeve,disc,discFace,lid,key,keyIcon,play,pause,artworkAt(p){
    const t=p*5.69;
    disc.position.fromArray(pose(t));disc.rotation.set(orient(t),0,0);
    lid.quaternion.fromArray(hinge(t));
  }};
}
