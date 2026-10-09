/* 场景：艺术家详情里点专辑卡（xtalbum）→ 专辑详情。
   验证两类详情互斥：页头/左栏必须换成专辑的，不能还挂着艺术家的上下文。 */
setTimeout(function(){
  __T.nav('Artists');
  __T.pushArtists(3);
  __T.artistDetail();
  __T.pushAlbumDetail([1, 2, 3], {tech:'FLAC | 16bit/44.1kHz | 1.4 Mbps | 15:00', dsd:false});
  __T.dump('xtalbum');
}, 300);
