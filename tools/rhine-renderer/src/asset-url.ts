export const assetUrl=(path:string)=>new URL(path.endsWith('archive-assembly.glb')?'rhine-cassette.glb':'rhine-cassette.glb',document.baseURI).href;
