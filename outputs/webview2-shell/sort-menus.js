/* 排序下拉。外观与行为都交给统一控件（ui-controls.js），这里只负责：
   ① 找到页面上的排序 select ② 保留原来的胶囊外观与"排列你的音乐"提示
   ③ 升/降序按钮的老逻辑 */
(() => {
  'use strict';
  const controls = window.CelesteControls;
  const popup = window.CelestePopup;

  document.querySelectorAll('.sort-picker select').forEach(select => {
    const wrapper = select.parentElement;
    wrapper.classList.add('enhanced-sort');
    controls.enhanceSelect(select, {
      variant: 'bare',
      menuClass: 'sort-menu',
      caption: '排列你的音乐',
      placement: 'bottom-end'
    });
  });

  document.querySelectorAll('.sort-direction').forEach(button => {
    const update = () => {
      const ascending = !button.textContent.includes('降序');
      button.dataset.ascending = String(ascending);
      const text = ascending ? '升序' : '降序';
      if (button.textContent !== text) button.textContent = text;
      button.setAttribute('aria-label', ascending ? '当前升序，切换为降序' : '当前降序，切换为升序');
    };
    new MutationObserver(update).observe(button, { childList: true, characterData: true, subtree: true });
    update();
  });

  // 换页、缩放窗口时把浮层收掉：浮层留在旧页面上只会挡住新页面
  document.addEventListener('celeste:navigate', () => popup.closeAll());
  window.addEventListener('resize', () => popup.closeAll());
})();
