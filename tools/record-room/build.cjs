// 打包两份产物：
//   1) outputs/record-room/room.js          —— 独立原型（data.json 内联）
//   2) outputs/webview2-shell/music-room.js —— 塞进播放器网页壳的覆盖层
const fs = require('fs');
const path = require('path');
const esbuild = require('esbuild');

const root = __dirname;
const repo = path.resolve(root, '../..');
const standaloneOut = path.join(repo, 'outputs/record-room');
const shellOut = path.join(repo, 'outputs/webview2-shell');

// 独立原型：把 data.json 内联进 bundle（双击也能跑）
const shellOnly = process.argv.includes('--shell');
if (!shellOnly) {
  const data = JSON.parse(fs.readFileSync(path.join(standaloneOut, 'data.json'), 'utf8'));
  fs.writeFileSync(path.join(root, 'src/_data.js'), 'export const DATA = ' + JSON.stringify(data) + ';\n');
}

const base = {
  bundle: true,
  format: 'iife',
  target: ['chrome110'],
  minify: true,
  legalComments: 'none',
};

if (!shellOnly) esbuild.buildSync({ ...base, entryPoints: [path.join(root, 'src/entry-standalone.js')], outfile: path.join(standaloneOut, 'room.js') });
esbuild.buildSync({ ...base, entryPoints: [path.join(root, 'src/entry-shell.js')], outfile: path.join(shellOut, 'music-room.js') });

// 样式两处共用一份
if (!shellOnly) fs.copyFileSync(path.join(shellOut, 'music-room.css'), path.join(standaloneOut, 'room.css'));

const kb = p => (fs.statSync(p).size / 1024).toFixed(0) + 'KB';
console.log('music-room.js', kb(path.join(shellOut, 'music-room.js')));
