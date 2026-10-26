(() => {
  'use strict';
  let opened=null;
  const close=()=>{if(!opened)return;opened.button.setAttribute('aria-expanded','false');opened.menu.remove();opened=null;};
  document.querySelectorAll('.sort-picker select').forEach(select=>{
    const wrapper=select.parentElement;wrapper.classList.add('enhanced-sort');
    select.tabIndex=-1;select.setAttribute('aria-hidden','true');
    const button=document.createElement('button');button.type='button';button.className='sort-trigger';button.setAttribute('aria-haspopup','listbox');button.setAttribute('aria-expanded','false');
    const update=()=>{button.textContent=select.selectedOptions[0]?.textContent||'排序';button.setAttribute('aria-label',select.getAttribute('aria-label')+'：'+button.textContent);};
    select.addEventListener('change',update);wrapper.append(button);update();
    function show(focus=false){
      if(opened?.button===button){close();return;}close();
      const menu=document.createElement('div');menu.className='sort-menu';menu.setAttribute('role','listbox');menu.setAttribute('aria-label',select.getAttribute('aria-label'));menu.id=select.id+'-menu';button.setAttribute('aria-controls',menu.id);
      const caption=document.createElement('div');caption.className='sort-menu-caption';caption.textContent='排列你的音乐';menu.append(caption);
      [...select.options].forEach(option=>{
        const item=document.createElement('button');item.type='button';item.setAttribute('role','option');item.setAttribute('aria-selected',String(option.selected));item.textContent=option.textContent;item.dataset.value=option.value;
        item.addEventListener('click',()=>{select.value=option.value;select.dispatchEvent(new Event('change',{bubbles:true}));close();button.focus();});menu.append(item);
      });
      document.body.append(menu);opened={button,menu};button.setAttribute('aria-expanded','true');
      const r=button.getBoundingClientRect();menu.style.left=Math.max(16,Math.min(innerWidth-menu.offsetWidth-16,r.right-menu.offsetWidth))+'px';menu.style.top=Math.max(16,r.bottom+8+menu.offsetHeight>innerHeight-16?r.top-menu.offsetHeight-8:r.bottom+8)+'px';
      if(focus)menu.querySelector('[aria-selected=true]')?.focus();
      menu.addEventListener('keydown',event=>{const items=[...menu.querySelectorAll('button')],index=items.indexOf(document.activeElement);let next=index;if(event.key==='ArrowDown')next=(index+1)%items.length;else if(event.key==='ArrowUp')next=(index-1+items.length)%items.length;else if(event.key==='Home')next=0;else if(event.key==='End')next=items.length-1;else return;event.preventDefault();items[next].focus();});
    }
    button.addEventListener('click',()=>show());button.addEventListener('keydown',event=>{if(['ArrowDown','ArrowUp'].includes(event.key)){event.preventDefault();show(true);}});
  });
  document.querySelectorAll('.sort-direction').forEach(button=>{
    const update=()=>{const ascending=!button.textContent.includes('降序');button.dataset.ascending=String(ascending);const text=ascending?'升序':'降序';if(button.textContent!==text)button.textContent=text;button.setAttribute('aria-label',ascending?'当前升序，切换为降序':'当前降序，切换为升序');};
    new MutationObserver(update).observe(button,{childList:true,characterData:true,subtree:true});update();
  });
  document.addEventListener('pointerdown',event=>{if(opened&&!opened.menu.contains(event.target)&&!opened.button.contains(event.target))close();});
  document.addEventListener('keydown',event=>{if(event.key==='Escape'&&opened){const button=opened.button;close();button.focus();}else if(event.key==='Tab')close();});
  window.addEventListener('resize',close);document.querySelector('.main').addEventListener('scroll',close,{passive:true});document.addEventListener('celeste:navigate',close);
})();
