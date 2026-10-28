// Host adapter for the MIT-licensed RhineLabUI ArchiveScene (LBEILC, 2026).
import { ArchiveScene } from './scene';
import { setAlbums, albumAt } from './data';
import { qualityPresets } from './render-quality';
export async function create(container: HTMLElement, albums: any[], onSelect: (album:any)=>void) {
  setAlbums(albums);
  const scene = new ArchiveScene(container);
  scene.setQuality(qualityPresets.original);
  scene.setReduced(matchMedia('(prefers-reduced-motion: reduce)').matches);
  try { await scene.load(new URL('rhine-cassette.glb', document.baseURI).href); }
  catch(error) { scene.dispose(); throw error; }
  let selected = 0, cell = { lane: 0, row: 12 }, disposed = false, frame = 0;
  const select = (index:number, navigation?:any) => {
    selected=index; scene.select(index,navigation); onSelect(albumAt(index));
  };
  scene.onSelect=(index, next) => { if(next)cell=next; select(index,next?{cell:next}:undefined); };
  scene.onNavigate=(axis,direction)=>{
    cell={...cell,[axis==='lane'?'lane':'row']:cell[axis==='lane'?'lane':'row']+direction};
    const lane=((cell.lane%5)+5)%5;
    const indices=albums.map((_,i)=>i).filter(i=>i%5===lane);
    const candidates=indices.length?indices:[lane%albums.length];
    select(candidates[((cell.row-12)%candidates.length+candidates.length)%candidates.length],{cell});
  };
  scene.setMode('archive'); scene.revealImmediately(); select(0);
  const resize=new ResizeObserver(()=>scene.resize()); resize.observe(container);scene.resize();
  let lastFrame=0;
  const tick=(ms:number)=>{if(disposed)return;if(ms-lastFrame>=1000/30){lastFrame=ms;scene.update(ms/1000);}frame=requestAnimationFrame(tick);};
  frame=requestAnimationFrame(tick);
  return {
    select:(index:number)=>select(index),
    detail:(enabled:boolean)=>scene.setMode(enabled?'detail':'archive'),
    quality:(high:boolean)=>scene.setQuality(high?qualityPresets.original:qualityPresets.performance),
    dispose:()=>{disposed=true;cancelAnimationFrame(frame);resize.disconnect();scene.dispose();},
    stats:()=>scene.getStats(),
  };
}
