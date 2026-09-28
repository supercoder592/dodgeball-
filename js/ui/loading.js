// ---------------------------------------------------------------------------------------------------------------
// Loading screen: title, progress bar (monotonic - asset totals grow while loading), current asset, rotating
// bilingual tips and credits (Microsoft Rocketbox avatars + motion capture, MIT). Loading.error() shows a
// readable failure with a reload button (and optionally a back button). Loading.show({ label, matchup }) reuses the
// screen for match loading (main.js: hero models / maps / clips of the picked line-up + shader warm-up) with the
// line-up on it.
// ---------------------------------------------------------------------------------------------------------------
import { prettyAssetLabel } from './format.js';
import { TIPS } from './pause.js';

const TIP_INTERVAL_MS = 4200;

const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

class LoadingScreen {
  constructor() {
    this.root = null;
    this._shown = 0;       // displayed fraction (never goes backwards)
    this._tipIndex = 0;
    this._tipTimer = 0;
    this._hideTimer = 0;
  }

  _build() {
    if (this.root) return;
    const ui = document.getElementById('ui') || document.body;
    const root = document.createElement('div');
    root.id = 'loading';
    root.className = 'ld';
    root.setAttribute('role', 'progressbar');
    root.setAttribute('aria-valuemin', '0');
    root.setAttribute('aria-valuemax', '100');
    root.innerHTML = `
      <div class="ld-court" aria-hidden="true"></div>
      <div class="ld-inner">
        <div class="ld-brand"><b>DODGEBALL</b><span>ULTRA</span></div>
        <div class="ld-sub">Realistic 4v4 superpowered dodgeball · 寫實 4v4 超能力躲避球</div>
        <div class="ld-matchup" hidden></div>
        <div class="ld-bar"><div class="ld-fill"></div></div>
        <div class="ld-row"><span class="ld-label">Preparing the arena · 準備場館</span><span class="ld-pct">0%</span></div>
        <div class="ld-tip"></div>
        <div class="ld-error" hidden>
          <b>Could not start the game · 無法啟動遊戲</b>
          <p class="ld-error-msg"></p>
          <p class="ld-error-hint">Dodgeball Ultra needs a browser with WebGL 2 (Chrome, Edge, Firefox, Safari 15+). 需要支援 WebGL 2 的瀏覽器。</p>
          <button type="button" class="ld-back" hidden>Back · 返回</button>
          <button type="button" class="ld-reload">Reload · 重新載入</button>
        </div>
      </div>
      <footer class="ld-credits">
        Realistic humans &amp; motion capture: <b>Microsoft Rocketbox</b> (MIT License) ·
        Rendering: three.js (MIT) · Ragdolls: cannon-es (MIT)
      </footer>`;
    ui.appendChild(root);
    this.root = root;
    this.fill = root.querySelector('.ld-fill');
    this.label = root.querySelector('.ld-label');
    this.pct = root.querySelector('.ld-pct');
    this.tip = root.querySelector('.ld-tip');
    this.matchup = root.querySelector('.ld-matchup');
    this.errorBox = root.querySelector('.ld-error');
    this.errorHint = root.querySelector('.ld-error-hint');
    this.defaultHint = this.errorHint.textContent;
    this.backBtn = root.querySelector('.ld-back');
    this._onBack = null;
    root.querySelector('.ld-reload').addEventListener('click', () => location.reload());
    this.backBtn.addEventListener('click', () => {
      const fn = this._onBack;
      this._onBack = null;
      if (fn) fn();
    });
  }

  /**
   * @param {{label?:string, matchup?:{home:string[], away:string[]}|null}} [opts] label: initial status line;
   *        matchup: hero names per team (match loading mode, compact layout).
   */
  show(opts = {}) {
    this._build();
    clearTimeout(this._hideTimer);
    this._shown = 0;
    this.root.classList.remove('ld-hidden', 'ld-failed');
    this.root.style.display = '';
    this.errorBox.hidden = true;
    this._onBack = null;
    const m = opts.matchup;
    this.root.classList.toggle('ld-match', !!m);
    this.matchup.hidden = !m;
    if (m) {
      this.matchup.innerHTML = `<span class="ld-team ld-home">${m.home.map(esc).join(' · ')}</span>`
        + '<b>VS</b>'
        + `<span class="ld-team ld-away">${m.away.map(esc).join(' · ')}</span>`;
    }
    this.label.textContent = opts.label || 'Preparing the arena · 準備場館';
    this._setFraction(0);
    this._nextTip();
    clearInterval(this._tipTimer);
    this._tipTimer = setInterval(() => this._nextTip(), TIP_INTERVAL_MS);
  }

  /** @param {number} loaded @param {number} total @param {string} label asset path being loaded */
  progress(loaded, total, label) {
    if (!this.root) this._build();
    const f = total > 0 ? Math.min(1, Math.max(0, loaded / total)) : 0;
    // Totals grow as more loads are queued: never move the bar backwards, creep instead.
    this._shown = Math.max(this._shown, f * 0.98);
    this._setFraction(this._shown);
    if (label) this.label.textContent = `${prettyAssetLabel(label)}  (${loaded}/${total})`;
  }

  /** Replaces the status line (e.g. 'Warming up shaders') without moving the bar. */
  status(text) {
    if (!this.root) this._build();
    this.label.textContent = String(text || '');
  }

  /** True while the screen is up (not hidden / fading out). */
  get visible() { return !!this.root && !this.root.classList.contains('ld-hidden') && this.root.style.display !== 'none'; }

  hide() {
    if (!this.root) return;
    this._setFraction(1);
    clearInterval(this._tipTimer);
    this.root.classList.add('ld-hidden');
    clearTimeout(this._hideTimer);
    this._hideTimer = setTimeout(() => { if (this.root && this.root.classList.contains('ld-hidden')) this.root.style.display = 'none'; }, 600);
  }

  /**
   * Replaces the bar with a failure message and a reload button.
   * @param {string} msg
   * @param {{title?:string, hint?:string, back?:Function}} [opts] title / hint replace the default (WebGL) texts;
   *        back adds a "Back" button that runs it (e.g. return to hero select after a failed match load).
   */
  error(msg, opts = {}) {
    this._build();
    clearTimeout(this._hideTimer);
    clearInterval(this._tipTimer);
    this.root.style.display = '';
    this.root.classList.remove('ld-hidden');
    this.root.classList.add('ld-failed');
    this.errorBox.hidden = false;
    this.errorBox.querySelector('b').textContent = opts.title || 'Could not start the game · 無法啟動遊戲';
    this.errorHint.textContent = opts.hint || this.defaultHint;
    this._onBack = typeof opts.back === 'function' ? opts.back : null;
    this.backBtn.hidden = !this._onBack;
    this.root.querySelector('.ld-error-msg').textContent = String(msg || 'Unknown error');
  }

  _setFraction(f) {
    this.fill.style.transform = `scaleX(${f.toFixed(4)})`;
    const p = Math.round(f * 100);
    this.pct.textContent = `${p}%`;
    this.root.setAttribute('aria-valuenow', String(p));
  }

  _nextTip() {
    const t = TIPS[this._tipIndex % TIPS.length];
    this._tipIndex++;
    this.tip.innerHTML = `<span>TIP · 提示</span> ${t[0]}<small>${t[1]}</small>`;
  }
}

/** Singleton loading screen (main.js: Loading.show / progress / hide / error). */
export const Loading = new LoadingScreen();
