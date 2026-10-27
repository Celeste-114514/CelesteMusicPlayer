/* 立体声相位仪（李萨如）。X=L、Y=R。
   这个模块的铁律只有一条：**没有真数据就不许画**。
   旧实现用固定抽点连线，会在换曲、丢包、seek 时连出跨块的长折线，看着像信号其实不是。
   这里每一段轨迹都来自同一个采样块，块与块之间断开，暂停就不再更新余辉，
   静音只有在新样本确实证实静音时才显示中心弱点。 */
(() => {
  'use strict';
  const NS = 'http://www.w3.org/2000/svg';
  const el = (tag, attrs) => { const e = document.createElementNS(NS, tag); Object.entries(attrs || {}).forEach(([k, v]) => e.setAttribute(k, v)); return e; };

  // 轨迹最多留几层，以及余辉衰减到多久看不见（毫秒）。
  // 方案给的是"过去 60–100ms 最多留少数层"，取 6 层 / 90ms 折中，够看出运动又不糊成一团。
  const MAX_LAYERS = 6;
  const AFTERGLOW_MS = 90;
  // 自动增益：快收缩（信号突然变大时立刻压住，别冲出框）慢放大（别让底噪抖个不停）
  const ATTACK_MS = 80, RELEASE_MS = 700, MAX_GAIN = 4, SILENCE = 0.003;

  class PhaseScope {
    constructor(svgEl) {
      this.svg = svgEl;
      this.size = 400;                 // viewBox 边长
      this.half = 200;
      this.layers = [];                // { pairs: Float32Array(2n), at: number, peak: number }
      this.gain = 1;
      this.auto = false;
      this.status = '暂无采样';
      this.rho = null;
      this.running = false;
      this.lastGeneration = null;
      this.build();
    }

    build() {
      const { svg, size, half } = this;
      svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
      svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
      svg.replaceChildren();
      const grid = el('g', { class: 'ps-grid' });
      for (let i = 1; i < 4; i++) {
        const p = i * size / 4;
        grid.append(el('line', { x1: p, y1: 0, x2: p, y2: size }));
        grid.append(el('line', { x1: 0, y1: p, x2: size, y2: p }));
      }
      grid.append(el('line', { x1: half, y1: 0, x2: half, y2: size, class: 'ps-axis' }));
      grid.append(el('line', { x1: 0, y1: half, x2: size, y2: half, class: 'ps-axis' }));
      svg.append(grid);
      // 两条对角线：同相（L=R）与反相（L=-R）。它们是坐标标尺，不是"画着好看的装饰"，
      // 没有数据时这两条线也要在 —— 但**不拿它们冒充信号**。
      const marks = el('g', { class: 'ps-diagonals' });
      marks.append(el('line', { x1: 0, y1: size, x2: size, y2: 0 }));
      marks.append(el('line', { x1: 0, y1: 0, x2: size, y2: size }));
      svg.append(marks);
      const label = (x, y, text) => { const t = el('text', { x, y, class: 'ps-note' }); t.textContent = text; svg.append(t); };
      label(size - 6, 16, '同相 L=R');
      label(6, 16, '反相 L=−R');
      this.traceGroup = el('g', { class: 'ps-traces' });
      svg.append(this.traceGroup);
      this.dot = el('circle', { cx: half, cy: half, r: 2, class: 'ps-silence', opacity: 0 });
      svg.append(this.dot);
    }

    /** 换曲、seek、丢包：把余辉清掉，绝不能让两段不同块的样本连成一条线。 */
    clear() {
      this.layers.length = 0;
      this.traceGroup.replaceChildren();
      this.lastGeneration = null;
    }

    setAuto(on) { this.auto = !!on; if (!on) this.gain = 1; }

    /**
     * 推入一组样本。left/right 必须来自同一个采样块、同一处理阶段。
     * @returns {boolean} 是否真的画了东西
     */
    push({ left, right, generation, available }) {
      if (!available || !Array.isArray(left) || !Array.isArray(right) || !left.length) {
        this.status = '暂无采样';
        this.running = false;
        return false;
      }
      // 代次变了 = 换曲或重播，旧轨迹一律作废
      if (generation !== undefined && this.lastGeneration !== null && generation !== this.lastGeneration) this.clear();
      if (generation !== undefined) this.lastGeneration = generation;

      const n = Math.min(left.length, right.length);
      const pairs = new Float32Array(n * 2);
      let peak = 0;
      for (let i = 0; i < n; i++) {
        const l = Number.isFinite(left[i]) ? Math.max(-1, Math.min(1, left[i])) : 0;
        const r = Number.isFinite(right[i]) ? Math.max(-1, Math.min(1, right[i])) : 0;
        pairs[i * 2] = l; pairs[i * 2 + 1] = r;
        peak = Math.max(peak, Math.abs(l), Math.abs(r));
      }

      if (this.auto && peak > SILENCE) {
        // 目标：把当前峰值放到约 0.9 满幅，上限 4 倍；低于静音阈值不继续放大
        const want = Math.min(MAX_GAIN, 0.9 / Math.max(peak, 1e-6));
        // 收缩快、放大慢
        const k = want < this.gain ? ATTACK_MS : RELEASE_MS;
        this.gain += (want - this.gain) * (1 - Math.exp(-16 / k));
      }

      this.layers.push({ pairs, at: performance.now(), peak });
      if (this.layers.length > MAX_LAYERS) this.layers.splice(0, this.layers.length - MAX_LAYERS);
      this.status = peak <= SILENCE ? '静音 · 中心弱点' : 'LIVE';
      this.running = true;
      return true;
    }

    /** 每帧调用。dt 没用到余辉以外的地方 —— 余辉按真实时间戳算，掉帧不会让轨迹多留。 */
    render(now = performance.now()) {
      const group = this.traceGroup;
      group.replaceChildren();
      if (!this.layers.length) {
        this.dot.setAttribute('opacity', '0');
        return;
      }
      const scale = this.half * 0.9 * this.gain;
      for (const layer of this.layers) {
        const age = now - layer.at;
        if (age > AFTERGLOW_MS) continue;
        const alpha = Math.exp(-age / (AFTERGLOW_MS / 3)) * (layer === this.layers[this.layers.length - 1] ? 0.95 : 0.5);
        const { pairs } = layer;
        let d = '';
        for (let i = 0; i < pairs.length; i += 2) {
          const x = this.half + pairs[i] * scale;
          const y = this.half - pairs[i + 1] * scale;   // Y 轴向上为正
          d += `${i ? 'L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`;
        }
        if (d) group.append(el('path', { d, 'stroke-opacity': alpha.toFixed(3) }));
      }
      // 静音：只有在新样本确实证实静音时才显示中心弱点
      const newest = this.layers[this.layers.length - 1];
      this.dot.setAttribute('opacity', newest && newest.peak <= SILENCE ? '1' : '0');
      this.layers = this.layers.filter(layer => now - layer.at <= AFTERGLOW_MS);
    }

    /** 相关系数读数：零能量给 "—"，不编 0 冒充。 */
    correlationText(value) {
      if (value === null || value === undefined || !Number.isFinite(value)) return '—';
      const v = Math.max(-1, Math.min(1, value));
      const hint = v > 0.3 ? '正相关' : v < -0.3 ? '负相关' : '接近正交';
      return `ρ=${v.toFixed(3)} · ${hint}`;
    }
  }

  window.CelestePhaseScope = { PhaseScope };
})();
