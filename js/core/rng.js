// Seeded PRNG (mulberry32) so spectate/autoplay runs are reproducible (?seed=N).
export class Rng {
  constructor(seed = 1) { this.state = (seed >>> 0) || 1; }
  next() {
    let t = (this.state += 0x6d2b79f5) >>> 0;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  }
  range(min, max) { return min + (max - min) * this.next(); }
  int(min, maxExclusive) { return Math.floor(this.range(min, maxExclusive)); }
  pick(arr) { return arr[Math.floor(this.next() * arr.length)]; }
  chance(p) { return this.next() < p; }
  /** Approximately normal (Box-Muller). */
  gaussian(mean = 0, sigma = 1) {
    const u = Math.max(1e-9, this.next()), v = this.next();
    return mean + sigma * Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v);
  }
  shuffle(arr) { for (let i = arr.length - 1; i > 0; i--) { const j = this.int(0, i + 1); [arr[i], arr[j]] = [arr[j], arr[i]]; } return arr; }
}
