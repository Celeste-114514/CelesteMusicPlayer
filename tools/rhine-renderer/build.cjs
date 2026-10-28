const esbuild=require('esbuild');
const path=require('node:path');
const result=esbuild.buildSync({entryPoints:[path.join(__dirname,'src/celeste-entry.ts')],bundle:true,format:'iife',globalName:'CelesteRhineRenderer',target:'es2022',minify:true,metafile:true,legalComments:'eof',outfile:path.join(__dirname,'../../outputs/webview2-shell/rhine-renderer.js')});
require('node:fs').writeFileSync(path.join(__dirname,'inputs.json'),JSON.stringify(Object.keys(result.metafile.inputs),null,2));
