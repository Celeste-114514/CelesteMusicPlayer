/* 无头测试 mock 宿主：模拟 window.chrome.webview（WebView2 注入对象）。
   必须在页面脚本之前注入——页面脚本执行时就注册 message 监听、上报 ready。
   上行消息（网页 → C#）全部记进 window.__posted，供断言。 */
window.__posted = [];
window.__wvHandlers = [];
window.__wvErrors = [];
window.chrome = {
  webview: {
    postMessage: function(m){
      try { window.__posted.push(JSON.parse(m)); }
      catch(e){ window.__posted.push({__parseError: String(m)}); }
    },
    addEventListener: function(ev, fn){
      if (ev === 'message') window.__wvHandlers.push(fn);
    }
  }
};
/* 模拟 C# 下行：把消息对象派发给页面注册的所有 handler */
window.__send = function(o){
  var data;
  try { data = JSON.stringify(o); } catch(e){ return; }
  for (var i = 0; i < window.__wvHandlers.length; i++){
    try { window.__wvHandlers[i]({ data: data }); }
    catch(e){ window.__wvErrors.push(String(e && e.message)); }
  }
};
/* 页面里 onclick 等抛出的未捕获错误也记下来（无头环境默认静默） */
window.addEventListener('error', function(e){ window.__wvErrors.push(String(e.message)); });
/* 生成 SVG data-url 假封面（无网络也能显示图） */
window.covS = function(k){
  var pal = ['#c0392b','#8e44ad','#2980b9','#16a085','#d35400','#2c3e50','#e67e22','#27ae60'];
  var s = '<svg xmlns="http://www.w3.org/2000/svg" width="300" height="300">'
        + '<rect width="300" height="300" fill="' + pal[k % 8] + '"/>'
        + '<circle cx="150" cy="150" r="92" fill="#fff" opacity=".22"/>'
        + '<text x="150" y="182" font-size="112" fill="#fff" text-anchor="middle" '
        + 'font-family="sans-serif">' + (k + 1) + '</text></svg>';
  return 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(s)));
};
