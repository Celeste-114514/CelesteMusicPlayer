/* 动效核心。
   职责：① 算出生效配置（模式/速度/幅度/是否减少动画）② 给全站一个唯一的动画入口，
   新操作接续当前画面、旧任务被取消，只有最新一代能改 hidden/inert/焦点 ③ 提供
   页面切换、浮层进出、列表 FLIP、弹簧按压这几类现成动作。

   规则：任何模块都不许再写死毫秒数。要动画就调这里的 API，时长和幅度自动跟着
   外观设置与系统偏好走。 */
(() => {
  'use strict';
  const root = document.documentElement;
  const reduceQuery = typeof matchMedia === 'function' ? matchMedia('(prefers-reduced-motion: reduce)') : { matches: false, addEventListener() {} };

  const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
  const readNumber = (name, fallback) => {
    const raw = getComputedStyle(root).getPropertyValue(name).trim();
    const value = Number.parseFloat(raw);
    return Number.isFinite(value) ? value : fallback;
  };

  const state = {
    mode: 'system',        // system | full | lite | off —— 用户选的模式
    effective: 'full',     // 真正生效的模式（system 会看系统偏好）
    systemReduce: false,
    speed: 1,              // 越大越快
    strength: 1,           // 位移/回弹幅度
    bounce: 'soft'         // calm | soft | lively —— 回弹风格
  };

  // 回弹风格：参数取自 BiliPai 的 interactiveSnap 思路（阻尼 .78 / 刚度 420），
  // 这里按"克制 / 柔和 / 灵动"给三档。阻尼越低回弹越明显，但不许低于 .68 —— 再低就不收敛了。
  const bounceStyles = {
    calm: { damping: 0.92, stiffness: 620 },
    soft: { damping: 0.82, stiffness: 420 },
    lively: { damping: 0.70, stiffness: 300 }
  };

  const listeners = new Set();

  function resolve() {
    const systemReduce = !!reduceQuery.matches;
    state.systemReduce = systemReduce;
    let effective;
    if (state.mode === 'off') effective = 'off';
    else if (state.mode === 'lite') effective = systemReduce ? 'off' : 'lite';
    else if (state.mode === 'full') effective = 'full';
    else effective = systemReduce ? 'off' : 'full';
    state.effective = effective;

    state.speed = clamp(readNumber('--motion-speed', 1) || 1, 0.4, 1.6);
    state.strength = clamp(readNumber('--motion-strength', 1), 0, 1.6);

    // 时长倍率 = 1 / 速度。旧版把 --motion-speed 当"时长乘数"用（越大越慢），
    // 语义正好相反；这里统一成"越大越快"，与设置项名字对齐。
    const liteFactor = effective === 'lite' ? 1.3 : 1;
    const scale = effective === 'off' ? 0.001 : (1 / state.speed) * liteFactor;
    const shift = effective === 'off' ? 0 : (effective === 'lite' ? 0 : state.strength);

    root.style.setProperty('--motion-scale', String(Number(scale.toFixed(4))));
    root.style.setProperty('--motion-shift', String(Number(shift.toFixed(4))));
    if (root.dataset.motion !== effective) root.dataset.motion = effective;
    if (effective === 'off') finishAll();
    listeners.forEach(fn => { try { fn(state); } catch { /* 单个订阅者出错不能连累其它 */ } });
    document.dispatchEvent(new CustomEvent('celeste:motion', { detail: { ...state } }));
  }

  // 关掉动效时，把正在跑的动画直接推到终点 —— 不能留一个半透明还挡着点击的层
  function finishAll() {
    try { document.getAnimations().forEach(animation => { try { animation.finish(); } catch { /* 已结束 */ } }); }
    catch { /* 浏览器不支持 getAnimations */ }
  }

  reduceQuery.addEventListener?.('change', resolve);
  document.addEventListener('celeste:theme-applied', resolve);
  document.addEventListener('celeste:theme', resolve);
  document.addEventListener('visibilitychange', () => { if (document.hidden) finishAll(); });

  const easings = {
    continuity: 'cubic-bezier(.20,.90,.22,1)',
    enter: 'cubic-bezier(.22,1,.36,1)',
    exit: 'cubic-bezier(.32,0,.67,0)',
    morph: 'cubic-bezier(.42,0,.58,1)'
  };

  const canFade = () => state.effective !== 'off';
  const canMove = () => state.effective === 'full';

  /** 基准毫秒 → 实际毫秒。幅度只影响位移，不影响时长。 */
  function duration(base) {
    if (state.effective === 'off') return 0;
    const scale = readNumber('--motion-scale', 1);
    return Math.max(1, Math.round(base * scale));
  }
  function shift(base) {
    if (!canMove()) return 0;
    return base * state.strength;
  }

  // —— 每个目标元素一份任务表：新操作接续当前画面，旧任务作废 ——
  const tracks = new WeakMap();

  /**
   * 在元素上跑一段动画。同一元素再次调用会取消上一次，并让 generation 前进。
   * 返回 { generation, finished }；finished 永远不会 reject（取消也算结束）。
   */
  function animate(element, keyframes, options = {}) {
    if (!element || !element.animate) return { generation: 0, finished: Promise.resolve(), animation: null };
    let track = tracks.get(element);
    if (!track) { track = { generation: 0, animation: null }; tracks.set(element, track); }
    const generation = ++track.generation;
    if (track.animation) { try { track.animation.cancel(); } catch { /* 已经结束 */ } }
    if (state.effective === 'off' || !keyframes || keyframes.length < 2) {
      track.animation = null;
      return { generation, finished: Promise.resolve(), animation: null };
    }
    const animation = element.animate(keyframes, {
      duration: options.duration ?? 200,
      easing: options.easing || easings.continuity,
      fill: options.fill || 'both',
      delay: options.delay || 0,
      composite: options.composite || 'replace'
    });
    track.animation = animation;
    const finished = animation.finished.catch(() => {});
    return { generation, finished, animation };
  }

  /** 读出某元素当前的 generation，用来判断"我还是最新的一次操作吗"。 */
  function generationOf(element) {
    const track = tracks.get(element);
    return track ? track.generation : 0;
  }

  /** 判断某次动画的结果还有效：中间没有更新的操作才算数。 */
  function isCurrent(element, generation) {
    return generationOf(element) === generation;
  }

  // —— 现成动作 ——

  /** 页面切换：新页面淡入并轻微上移。旧页面不参与，保证快速切换不闪白。 */
  function page(element, direction = 'forward') {
    if (!element) return Promise.resolve();
    const distance = shift(10) * (direction === 'back' ? -1 : 1);
    if (!canFade()) { element.style.opacity = ''; return Promise.resolve(); }
    const frames = canMove() && distance
      ? [{ opacity: 0, transform: `translateY(${distance}px)` }, { opacity: 1, transform: 'translateY(0)' }]
      : [{ opacity: 0 }, { opacity: 1 }];
    return animate(element, frames, { duration: duration(240), easing: easings.enter }).finished;
  }

  /** 浮层进场：轻微下移 + 缩放。 */
  function popupEnter(element) {
    if (!element || !canFade()) return Promise.resolve();
    const offset = shift(6);
    const frames = canMove() && offset
      ? [{ opacity: 0, transform: `translateY(${offset}px) scale(.985)` }, { opacity: 1, transform: 'translateY(0) scale(1)' }]
      : [{ opacity: 0 }, { opacity: 1 }];
    return animate(element, frames, { duration: duration(180), easing: easings.enter }).finished;
  }

  /** 浮层退场：结束后才真正隐藏/移除。 */
  function popupExit(element, onDone) {
    if (!element) { onDone?.(); return Promise.resolve(); }
    if (!canFade()) { onDone?.(); return Promise.resolve(); }
    const offset = shift(4);
    const frames = canMove() && offset
      ? [{ opacity: 1, transform: 'translateY(0) scale(1)' }, { opacity: 0, transform: `translateY(${-offset}px) scale(.99)` }]
      : [{ opacity: 1 }, { opacity: 0 }];
    const { generation, finished } = animate(element, frames, { duration: duration(140), easing: easings.exit });
    return finished.then(() => {
      // 退场期间用户又打开了同一个元素 —— 旧动画不许把它关掉
      if (isCurrent(element, generation)) onDone?.();
    });
  }

  /** 列表重排：测量前后位置差，反向补偿再放开。 */
  function flip(elements, mutate) {
    const nodes = [...elements].filter(node => node && node.isConnected);
    if (!nodes.length) { mutate?.(); return; }
    const first = new Map();
    nodes.forEach(node => first.set(node, node.getBoundingClientRect()));
    mutate?.();
    if (!canMove() || state.effective === 'off') return;
    nodes.forEach(node => {
      const before = first.get(node);
      const after = node.getBoundingClientRect();
      const dx = before.left - after.left;
      const dy = before.top - after.top;
      if (!dx && !dy) return;
      animate(node, [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'translate(0, 0)' }], { duration: duration(180), easing: easings.enter });
    });
  }

  /**
   * 阻尼弹簧关键帧。按压反馈用：给初速度，释放时接着这一下。
   * 参数取自 BiliPai 的 interactiveSnap 思路（阻尼 .78 / 刚度 420），桌面按帧采样。
   */
  function spring({ from = 0, to = 1, velocity = 0, damping, stiffness, steps = 36 } = {}) {
    const style = bounceStyles[state.bounce] || bounceStyles.soft;
    const d = damping ?? style.damping;
    const k = stiffness ?? style.stiffness;
    const omega = Math.sqrt(k);
    const wd = omega * Math.sqrt(Math.max(1e-6, 1 - d * d));
    const frames = [];
    for (let index = 0; index <= steps; index += 1) {
      const t = index / steps * 0.42;
      const decay = Math.exp(-d * omega * t);
      const position = to + decay * ((from - to) * Math.cos(wd * t) + ((velocity + d * omega * (from - to)) / wd) * Math.sin(wd * t));
      frames.push(position);
    }
    frames[frames.length - 1] = to;
    return frames;
  }

  /** 按压反馈：按下缩小一点，松开弹回来。 */
  function press(element, pressed) {
    if (!element || !canMove()) return;
    const scale = pressed ? 0.97 : 1;
    animate(element, [{ transform: pressed ? 'scale(1)' : 'scale(.97)' }, { transform: `scale(${scale})` }], { duration: duration(pressed ? 90 : 260), easing: pressed ? easings.exit : easings.enter });
  }

  window.CelesteMotion = {
    state,
    easings,
    duration,
    shift,
    canFade,
    canMove,
    animate,
    generationOf,
    isCurrent,
    page,
    popupEnter,
    popupExit,
    flip,
    spring,
    press,
    /** 用户选的模式：system / full / lite / off。 */
    setMode(mode) {
      state.mode = ['system', 'full', 'lite', 'off'].includes(mode) ? mode : 'system';
      resolve();
      return state.mode;
    },
    getMode: () => state.mode,
    /** 回弹风格：calm / soft / lively。 */
    setBounce(style) {
      state.bounce = bounceStyles[style] ? style : 'soft';
      return state.bounce;
    },
    getBounce: () => state.bounce,
    bounceStyles,
    onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    resolve
  };

  resolve();
})();
