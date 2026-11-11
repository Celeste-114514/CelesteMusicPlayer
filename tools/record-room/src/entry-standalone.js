// 独立原型入口：数据在打包时内联（见 build.cjs），双击/本地起服务都能跑。
import { DATA } from './_data.js';
import { startRoom } from './main.js';

startRoom(DATA, { mode: 'standalone' });
