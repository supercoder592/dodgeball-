// ---------------------------------------------------------------------------------------------------------------
// Loading screen: title, progress bar (monotonic - asset totals grow while loading), current asset, rotating
// bilingual tips and credits (Microsoft Rocketbox avatars + motion capture, MIT). Loading.error() shows a
// readable failure with a reload button.
// ---------------------------------------------------------------------------------------------------------------
import { prettyAssetLabel } from './format.js';
import { TIPS } from './pause.js';

const TIP_INTERVAL_MS = 4200;

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
        <div class="ld-sub">Realistic 3v3 superpowered dodgeball · 寫實 3v3 超能力躲避球</div>
        <div class="ld-bar"><div class="ld-fill"></div></div>
        <div class="ld-row"><span class="ld-label">Preparing the arena · 準備場館</span><span class="ld-pct">0%</span></div>
        <div class="ld-tip"></div>
        <div class="ld-error" hidden>
          <b>Could not start the game · 無法啟動遊戲</b>
          <p class="ld-error-msg"></p>
          <p class="ld-error-hint">Dodgeball Ultra needs a browser with WebGL 2 (Chrome, Edge, Firefox, Safari 15+). 需要支援 WebGL 2 的瀏覽器。</p>
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
    root.querySelector('.ld-reload').addEventListener('click', () => location.reload());
  }

  show() {
    this._build();
    clearTimeout(this._hideTimer);
    this._shown = 0;
    this.root.classList.remove('ld-hidden', 'ld-failed');
    this.root.style.display = '';
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

  hide() {
    if (!this.root) return;
    this._setFraction(1);
    clearInterval(this._tipTimer);
    this.root.classList.add('ld-hidden');
    clearTimeout(this._hideTimer);
    this._hideTimer = setTimeout(() => { if (this.root && this.root.classList.contains('ld-hidden')) this.root.style.display = 'none'; }, 600);
  }

  error(msg) {
    this._build();
    clearTimeout(this._hideTimer);
    clearInterval(this._tipTimer);
    this.root.style.display = '';
    this.root.classList.remove('ld-hidden');
    this.root.classList.add('ld-failed');
    const box = this.root.querySelector('.ld-error');
    box.hidden = false;
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
