// Album indices retain identity across the five looping visual lanes.
let albums:any[]=[];
export const archiveColumns=['0','1','2','3','4'];
export const albumAt=(index:number)=>albums[index];
export function setAlbums(value:any[]){albums=value;}
export function columnFiles(lane:number){
 const indices=albums.map((_,i)=>i).filter(i=>i%5===lane);
 return indices.length?indices:[lane%albums.length];
}
export function fileLocation(index:number){
 const lane=index%5,row=12+columnFiles(lane).indexOf(index);
 return {lane,row,slot:lane*32+row};
}
export function fileAtSlot(slot:number){const files=columnFiles(Math.floor(slot/32));return files[Math.max(0,Math.min(files.length-1,(slot%32)-12))];}
