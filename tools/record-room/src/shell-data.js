// 把网页壳的数据翻成唱片室要的形状。
// 壳里已经有：getAlbums()（含专辑艺术家、年份、曲目、封面地址）、getLibrary()（曲目）。
// 封面/头像走 https://celeste-cover.internal 两个虚拟路径，宿主已给出跨源许可。

function hashOf(str) {
  let h = 2166136261;
  for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619); }
  return Math.abs(h);
}

// 侧脊颜色：从名字派生一个低饱和的纸色。真正的封面色拿不到（跨源像素读不了），
// 这里只做装饰，不假装是专辑配色。
function spineTint(key) {
  const h = hashOf(key) % 360;
  const s = 22 + (h % 7) * 3;
  const l = 40 + (h % 5) * 3;
  return `hsl(${h}, ${s}%, ${l}%)`;
}

export function avatarUrl(name) {
  return 'https://celeste-cover.internal/avatar/' + encodeURIComponent(name);
}

export function buildRoomData(shell) {
  const albums = shell.getAlbums() || [];
  const lib = shell.getLibrary() || [];
  const byId = new Map();
  for (const t of lib) byId.set(String(t.id), t);

  const map = new Map();
  for (const al of albums) {
    const name = String(al.artist || '未知艺术家').trim() || '未知艺术家';
    let a = map.get(name);
    if (!a) { a = { name, avatar: avatarUrl(name), tint: spineTint(name), albums: [] }; map.set(name, a); }

    const ids = al.trackIds || [];
    const tracks = ids.map(id => byId.get(String(id))).filter(Boolean);
    a.albums.push({
      title: al.title || '未知专辑',
      year: al.year || null,
      cover: al.coverUrl || null,
      tracks: al.trackCount || tracks.length,
      dur: al.duration || 0,
      list: tracks.slice(0, 12).map(t => ({
        t: t.title || '—',
        s: Math.round(t.duration || t.durationSeconds || 0),
      })),
      tint: spineTint((al.id || '') + (al.title || '')),
      trackIds: ids,
    });
  }

  const artists = [...map.values()];
  artists.forEach(a => a.albums.sort((x, y) =>
    ((x.year || 9999) - (y.year || 9999)) || String(x.title).localeCompare(String(y.title), 'zh-Hans-CN')));
  artists.sort((a, b) => String(a.name).localeCompare(String(b.name), 'zh-Hans-CN'));

  const albumCount = artists.reduce((n, a) => n + a.albums.length, 0);
  return { artists, albumCount };
}
