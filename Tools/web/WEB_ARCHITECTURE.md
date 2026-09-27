# Dodgeball Ultra — Web build contract (docs/, GitHub Pages)

Browser version of the Unity game: **three.js 0.186 (WebGL2) + cannon-es**, native ES modules, **no bundler**
(`docs/index.html` import map: `three` → `vendor/three/build/three.module.js`, `three/addons/` →
`vendor/three/examples/jsm/`, `cannon-es` → `vendor/cannon-es/cannon-es.js`). The site root is `docs/`.

Realistic humans: Microsoft Rocketbox avatars + motion capture (MIT), converted by `Tools/web/build_assets.py` into
`docs/assets/` (see §6). Never draw human bodies procedurally.

## 1. Rules for every module

* Only edit files you own. Kernel files (below) may only gain *additive* members if your brief says so.
* ES modules, `import * as THREE from 'three'`. No npm imports other than the import-map names. No build step.
* All cross-module access goes through the `game` context (`import { game, ORDER } from '../game.js'`).
* **Scaled vs real time**: gameplay uses `dt` / `game.time.now` (freezes during hitstop & pause); camera, juice, UI,
  audio use `realDt` / `game.time.realNow`.
* Events: `game.events.on(EV.X, fn)` / `emit(EV.X, payload)`; names and payloads in `js/core/events.js`. Unsubscribe in
  `dispose()` (abilities use `this.listen()`).
* No per-frame allocations in hot paths: reuse `THREE.Vector3` temporaries at module scope.
* Everything must degrade gracefully when an optional system is missing (`game.vfx?.play(...)`).
* Units: metres, seconds, radians (UI shows km/h). World up = +Y. Yaw 0 faces +Z. Court floor at y = 0.
* Realistic, broadcast-sports look. No cartoon shading.
* Document with JSDoc + inline comments; expose tuning as named constants / `static defaults`.
* Verify: `cd Tools/web && node smoke.mjs --tag <you> --seconds 20` (AI vs AI spectate) must show **no page errors
  and no console errors** once all modules exist; view the screenshots in `Tools/web/screenshots/`.
  `npm test` runs `docs/js/**/*.test.js` (pure logic). Syntax-check a file quickly with `node --check file.js`.

## 2. Kernel (already written — read them)

| File | What |
|---|---|
| `js/game.js` | `game` context, `ORDER`, `GameTime` (hitstop/pause), fixed 60 Hz loop, `addSystem`, player helpers (`game.players`, `localPlayer`, `nearestEnemy`, `nearestTeammate`, `areEnemies`, `areTeammates`, `playersInSphere`), `game.hittables` |
| `js/core/constants.js` | spec numbers, `TEAM`, `ZONE`, `opponent()`, `COURT`, `TEAM_COLORS`, `HERO_IDS` |
| `js/core/rules.js` | `RallyMath`, `CatchTiming`, `CatchQuality` (`'miss'|'normal'|'perfect'`), `Ballistics` |
| `js/core/events.js` | `EV` names + payload docs, `EventBus` |
| `js/core/fsm.js`, `timers.js`, `rng.js` | `StateMachine`, `Cooldown`, `Meter`, seeded `Rng` (`game.rng`) |
| `js/engine/assets.js` | `Assets`: `manifest`, `gltf(path)`, `texture(path, srgb)`, `clip(gender, key)` |
| `js/world/court.js` | `Court` geometry: bounds `{minX,maxX,minZ,maxZ}`, `confinement(team, zone)`, spawn points, `Court.clamp/contains` |
| `js/gameplay/history.js` | `RewindHistory(capture, seconds, rate)`: `record(now)`, `sample(now, secondsAgo)`, `clear()` |
| `js/abilities/abilityBase.js` | `AbilityBase` (lifecycle + hooks `onCast/onTick/onInterrupt/onCooldown`...), `SLOT`, `PHASE`, `FAIL`, `INTERRUPT`, `AI_HINT`, `registerAbility(id, cls)` |
| `js/abilities/abilityController.js` | `AbilityController` (passive/skill/ultimate + ult meter) |
| `js/abilities/abilityUtil.js` | `throwAbilityBall(thrower, opts)`, `groundPoint`, `clampToPlayerZone` |
| `js/abilities/roster.js` | `HEROES` (all spec numbers, bilingual text, avatar casting, `hiddenParts`), `heroById`, `heroMovement`, `heroCombat` |
| `js/main.js`, `index.html` | boot sequence (constructs every system below with the exact names/signatures listed here) |

## 3. Module APIs (implement exactly these names; add more if useful)

Every system class: `constructor()` with no args, optional `async init()`, optional `update(dt, realDt)`,
`fixedUpdate(dt)`, `lateUpdate(dt, realDt)`, `dispose()`. `main.js` registers them with `game.addSystem(sys, ORDER.X)`.

### 3.1 Player — `js/gameplay/` (owner: **player**)
`player.js` → `export class Player`
```
constructor({ id, name, team, hero /* roster def */, isHuman, isLocal, intentSource /* {sample(player, dt)->intent} */ })
id name team hero isHuman isLocal zone('infield'|'outfield') inputLocked
root: THREE.Group (added to game.scene)       position: Vector3 (feet, alias of root.position)   velocity: Vector3
yaw (radians)  forward (getter, planar unit)  radius height  chestPosition (getter)  headPosition (getter)
motor: Motor  fsm: PlayerStateMachine  combat: Combat  health: Health  status: Status  abilities: AbilityController
avatar: Avatar  history: RewindHistory  intent (last sampled)  intentSource
isTargetable (infield, alive, not mid-elimination)   canAct (not stunned/incapacitated, not inputLocked)
canReceive (can receive a pass)   isGrounded
async init()   // builds avatar (await Avatar.load), components, registers in game.players, emits PlayerSpawned
update(dt, realDt)  fixedUpdate(dt)   // called by Match for every player (players are NOT game systems)
setZone(zone)  teleport(pos, yaw)  resetForRound(pos, yaw)  dispose()
```
Intent object (produced by Input/Bot): `{ move: Vector3(planar, len<=1), aimDir: Vector3, aimPoint: Vector3,
target: Player|null, sprint, jump, slide, throwPressed, throwHeld, throwReleased, catchPressed, pass, pickup, skill,
ultimate, cycleTarget }` (booleans). `neutralIntent(player)` helper exported from `player.js`.
Update order inside `Player.update`: sample intent → status → fsm → (skill/ult/pass/pickup intents) → combat →
abilities → health → avatar. `fixedUpdate`: fsm → motor → history.record.

`motor.js` → `Motor(player, profile)`: custom kinematic character physics (no rigid body): gravity × gravityMul,
ground at court floor, AABB colliders from `game.arena.colliders` (array of `THREE.Box3`), confinement bounds.
`setMove(dir, mag)`, `setMode(mode)` (`'walk'|'sprint'|'slide'|'air'|'charging'|'catching'|'locked'`),
`setFacing(dir, instant)`, `tryJump()`, `trySlide(dir)`, `endSlide()`, `addImpulse(v)`, `setPlanarVelocity(v)`,
`teleport(pos, yaw)`, `setFrozen(b)`, `setSpeedModifier(src, mul)`, `removeSpeedModifier(src)`,
`setTractionModifier(src, t)`, `removeTractionModifier(src)`, `setConfinement(bounds|null)`, `fixedUpdate(dt)`.
Readouts: `planarSpeed`, `isGrounded`, `yawRate` (rad/s signed, + = turning right), `planarAccel` (Vector3),
`isSliding`, `canJump`, `canSlide`, `maxSpeed`, `speedMul`, `traction`. Events via callbacks `onLand(speed)`, `onJump()`.
Smooth acceleration curve, slide momentum preservation + friction loss, knockback, traction (ice).

`states.js` → `PlayerStateMachine(player)` over `core/fsm.js`; `STATE = { GROUNDED:'grounded', AIRBORNE:'airborne',
SPRINTING:'sprinting', SLIDING:'sliding', CHARGING:'chargingThrow', CATCHING:'catching', STUNNED:'stunned',
INCAPACITATED:'incapacitated' }`; `current`, `is(id)`, `canAct`, `incapReason`, `stun(d)`, `incapacitate(reason, d)`
(`'eliminated'|'frozen'|'grabbed'|'teleporting'|'channeling'|'rewinding'|'round'`), `release(reason)`,
`resetToGrounded()`, `update(dt)`, `fixedUpdate(dt)`. Emits `EV.PlayerState`.

`health.js` → `Health(player, maxHp)`: `maxHp hp isAlive isEliminated pending`; `addHitFilter({priority, filter(hit)})`,
`addInterceptor({priority, intercept(health, ctx) -> 'eliminated'|'delayed'|'prevented'})` (+ remove*),
`receiveHit(hit) -> outcome` where hit = `{ ball, attacker, victim, point, normal, velocity, damage, unblockable,
forceEliminate, knockback, isAbility, cancelled }` and outcome ∈ `'ignored'|'negated'|'damaged'|'eliminated'|'delayed'|'prevented'`.
Frozen victim + hit ⇒ forceEliminate (Elsa). `applyDamage(amount, source, cause)`, `eliminate(cause, attacker, impulse,
point, bypass=false)`, `commitPending(ctx)`, `setPending(b)`, `revive(frac=1)`, `setHpSilently(hp)`, `resetForRound()`.
Eliminate ⇒ `fsm.incapacitate('eliminated')`, `avatar.enableRagdoll(impulse, point)`, emit `EV.PlayerEliminated`.

`status.js` → `Status(player)`: `apply(type, duration, magnitude=1, source)` (duration<=0 permanent), `remove(type, source)`,
`removeAll()`, `has(type)`, `magnitude(type)`, `remaining(type)`, `moveSpeedMul`, `update(dt)`. Types:
`slow haste frozen stunned invulnerable cloaked revealed silenced rooted slippery dodgeDisabled silentFootsteps obscured
magnetized`. Side effects: frozen → `fsm.incapacitate('frozen')`, `combat.catchingBlocked`, `avatar.setFrozen`;
cloaked → `avatar.setCloaked`; slippery → motor traction; emits `EV.Status`.

### 3.2 Combat — `js/combat/` (owner: **combat**)
`ball.js` → `Ball(id, isAbilityBall)`: `state ('free'|'held'|'live'|'stasis'|'despawned') style holder lastThrower
rallyCount isAbilityBall isPass unblockable pierce gravityScale payload lockedTarget launchTime position velocity radius
root (THREE.Group) visual (inner Object3D for squash & stretch) history speedKmh`;
`launch(params, velocity)`, `attachTo(player)`, `makeFree(velocity, resetRally)`, `setVelocity(v)`, `teleport(pos, vel)`,
`enterStasis(d)`, `despawn(d, respawnPos)`, `resetTo(pos)`, `setStyle(style)`, `canBePickedUpBy(p)`,
`setRallyCount(n)`, `fixedUpdate(dt)`, `update(dt, realDt)`. Styles: `standard meteor beam glue freeze turret`.
Live flight: field effects → payload.onTick → gravity × gravityScale → **swept sphere** vs hittables, player capsules
(catch → `victim.combat.tryResolveCatch(ball, point, impactTime)`; else `HitResolver.resolveHit`), arena colliders +
floor (bounce, `EV.BallBounced`; floor resets rally; any surface ends live). Free balls: rolling/bouncing physics,
ball–ball collisions. Realistic red rubber dodgeball look (pebbled normal map from a canvas, PBR).
`balls.js` → `BallManager` (system): `matchBalls`, `active`, `setupMatchBalls(positions)`, `resetForRound(positions)`,
`spawnAbilityBall(pos, style)`, `recycle(ball)`, `findNearest(pos, filter, maxDist)`, `incomingLive(player, out[])`,
`registerField(effect /* {apply(ball, dt)} */)`, `unregisterField`, `applyFields(ball, dt)`; out-of-arena respawn.
`combat.js` → `Combat(player, profile)`: `hasBall heldBall isCharging chargeSeconds charge (0..1) catchArmed
lastCatchInput perfectWindow perfectWindowMul catchingBlocked currentTarget counterBoostUntil passHandler profile`;
`addThrowModifier({order, modify(params), committed?(params, ball)})`, `removeThrowModifier`, `tryPickup(ball)`,
`tryPickupNearest()`, `giveBall(ball)`, `beginCharge()`, `releaseThrow()`, `cancelCharge()`,
`throwNow(charge, target)`, `buildThrowParams(chargeSeconds, target, isAbility)`, `launchBall(ball, params)`,
`tryStartCatch()`, `cancelCatch()`, `tryResolveCatch(ball, point, impactTime) -> CatchQuality`, `inCatchCone(pos)`,
`tryPass()`, `receivePass(ball, from)`, `dropBall(velocity)`, `getThrowOrigin()`, `grantCounterBoost()`,
`resetForRound()`, `update(dt)`.
ThrowParams: `{ thrower, target, origin, aimDir, aimPoint, baseSpeed, charge, chargeSeconds, speedMul, radiusMul,
rallyCount, gravityScale, unblockable, pierce, isAbility, isPass, isCounter, reveals, style, payload }`.
`throwSolver.js` → `finalSpeed(params)` (rally formula + 220 km/h cap), `ThrowSolver.solve(params) -> {velocity,
flightTime}`, `ThrowSolver.intercept(origin, target, speed, gravity)`, `Trajectory.positionAt(p, v, g, t, out)`,
`Trajectory.timeToReach(ball, point, radius, maxTime) -> {t, point}|null`, `Trajectory.predictImpact(ball, player,
maxTime) -> {t, point}|null`, `Targeting.findBest(thrower, origin, dir, maxAngleDeg, maxDist)`,
`Targeting.cycle(thrower, current, origin, dir)`, `HitResolver.resolveHit(ball, victim, point, normal) -> outcome`
(emits `EV.BallHitPlayer` — feeds the juice pipeline), `HitResolver.resolveAbilityHit(attacker, victim, damage, point,
knockback)`.
Payload interface (any subset): `onLaunched(ball) onTick(ball, dt) onHitPlayer(ball, hit)->bool
onAfterHitPlayer(ball, hit, outcome) onHitSurface(ball, point, normal, isFloor) onCaught(ball, catcher, quality)
onEnded(ball)`. Perfect catch rewards: `game.match.reviveOneOutfield(team, 'perfectCatch', catcher)`, +0.15 ult,
`grantCounterBoost()` (+20% next throw); caught ball `rallyCount+1`.

### 3.3 Characters — `js/characters/` (owner: **characters**)
`avatar.js` → `Avatar(player)`; `async load()` (hero from `player.hero`, gender from manifest); `root` (added under
`player.root`), `model`, `mixer`, `bones` (map of Humanoid names → THREE.Bone: hips spine chest neck head
lUpperArm lForearm lHand rUpperArm rForearm rHand lThigh lCalf lFoot rThigh rCalf rFoot), `rightHandSocket`,
`leftHandSocket` (Object3D in the palm), `update(dt, realDt)` (locomotion blend from `player.motor.planarSpeed` & state:
idle/walk/run/sprint/crouch/stunned + airborne pose, cheer/defeat/wave one-shots; strip root motion), procedural IK
after the mixer: head LookAt (incoming ball → target → aim), catch reach (two-bone IK to the predicted intercept),
throw wind-up/whip, procedural lean into turns (roll from yawRate×speed, pitch from acceleration);
`playThrow(dir)`, `playCatch()`, `playHit(dir)`, `cheer()`, `defeat()`, `flash(color, duration)`,
`setTint(color, amount)`, `setCloaked(bool)` (enemies of the local player: near-invisible shimmer; others:
translucent), `setFrozen(bool)`, `spawnAfterimage(lifetime, color)`, `createClone() -> { root, update(dt), dispose() }`
(animated duplicate mirroring this avatar's actions), `enableRagdoll(impulse, point)`, `recoverFromRagdoll()`,
`resetVisual()`, `setVisible(b)`, `dispose()`. Materials: `MeshPhysicalMaterial` from `manifest.heroes[id].materials`
(skin sheen, hair alpha, gear), hide `hero.hiddenParts`. Team ground ring. Shadows.
`ragdoll.js` → `Physics` (system: cannon-es `World`, fixed step) + `Ragdoll(avatar)` built from bones (capsule/box
bodies + cone-twist constraints), impulse-driven transition, pose written back to bones.

### 3.4 Render & world — `js/render/`, `js/world/` (owner: **render**)
`render/renderer.js` → `Renderer`: `constructor(container, quality)`; `three` (WebGLRenderer), `scene`, `camera`
(PerspectiveCamera 60°), `quality` (`'low'|'medium'|'high'`), ACES tone mapping, sRGB output, PCFSoft shadows,
`EffectComposer` (RenderPass → GTAO/SAO (medium+) → UnrealBloom (subtle) → custom grade pass (vignette, chromatic
aberration, saturation, exposure pulses) → SMAA/FXAA → OutputPass), `render(realDt)`, `resize()`,
`pulse(type, intensity, duration)` with types `'hit' 'heavyHit' 'perfectCatch' 'ultimate' 'freeze' 'rewind' 'danger'`,
`setSustained(type, amount)`, environment map (PMREM from RoomEnvironment tuned for an arena).
`world/arena.js` → `Arena`: `async build(scene)` realistic indoor arena (hardwood court with painted lines,
outfield strips, run-off vinyl, padded walls, bleachers with a subtle crowd, floodlight rig with real shadows);
procedural PBR canvas textures (`world/textures.js`); `court` (Court instance, also set as `game.court` by main),
`colliders` (THREE.Box3[] for walls/bleachers), `lights`.

### 3.5 Juice & camera (owner: **juice**)
`juice/juice.js` → `Juice` (system): subscribes `BallHitPlayer` / `BallCaught` / `PlayerEliminated`; `playHit(req)`,
`playCatch(req)`, `hitstop(duration, scale)` → `game.time.hitstop` (dynamic 0.03–0.1 s), `shake(amplitude, frequency,
duration, sourcePos)`, `addTrauma(t)`, `squash(object3D, normalWorld, intensity, duration)` (volume-preserving),
`flash(player, color, duration=0.05)` → `player.avatar.flash`, screen pulses via `game.renderer.pulse`. Tuning in a
`JUICE` constant object. Shake is Perlin/simplex noise, trauma² model, unscaled time; exposes `shakeOffset` (Vector3)
and `shakeRotation` (Euler) read by the camera rig.
`camera/cameraRig.js` → `CameraRig` (system, lateUpdate): over-the-shoulder follow of `target` (local player),
`addLook(dxDeg, dyDeg)`, `setTarget(player, snap)`, `setCinematic(point|null)`, `addFovKick(deg, dur)`, collision vs
`game.arena.colliders`, `planarForward`, `planarRight`, `aimRay` (THREE.Ray), `aimPoint`, applies juice shake, follows
ragdoll hips, spectate orbit when no local player.

### 3.6 Match & boot (owner: **match**)
`match/match.js` → `Match` (system): `phase` (`'idle'|'select'|'preRound'|'countdown'|'playing'|'roundEnd'|'matchEnd'`),
`isPlaying`, `round`, `timeLeft`, `scores [home, away]`, `rules` (object: playersPerTeam 3, roundsToWin 2, roundTime 150,
preRound 2.5, countdown 3, roundEnd 4, ballCount 6, outfieldHitRevives true, catchEliminatesThrower false,
ragdollTime 2, reviveHp 1, ult gains...), `players`, `local`; `async startMatch(setup)` where setup =
`{ localHero, localTeam, homeHeroes[3], awayHeroes[3], difficulty, spectate }`, `endMatch()`, `sendToOutfield(p)`,
`reviveFromOutfield(p, cause, reviver)`, `reviveOneOutfield(team, cause, reviver)`, `countInfield(team)`; updates and
fixed-updates every Player; outfield-hit revive rule; time-up rules; emits all match events.

### 3.7 AI — `js/ai/bot.js` (owner: **ai**)
`Bot(difficulty, seed)` with `sample(player, dt) -> intent` (same intent shape as humans, goes through the same state
machine) and exported `buildAbilityContext(player)` → `{ self, nearestEnemy, nearestEnemyDistance, incomingBall,
incomingTime, teammatesOutfield, enemiesInfield, alliesInfield, holdingBall, freeBallsNearby, ultCharge, timeLeft }`.
Difficulties `'easy'|'normal'|'hard'|'pro'`.

### 3.8 Input & UI — `js/input/`, `js/ui/`, `css/game.css` (owner: **ui**)
`input/input.js` → `Input` (system, first): keyboard/mouse (pointer lock), Gamepad API, touch controls (virtual stick +
buttons on mobile); `HumanController` with `sample(player, dt) -> intent` (camera-relative using
`game.cameraRig.planarForward/Right`, aim from `cameraRig.aimRay/aimPoint`), sends look deltas to the rig,
`pausePressed`. Controls: WASD, mouse, Shift sprint, Space jump, C/Ctrl slide, LMB hold-throw, RMB catch, Q pass,
E pickup, F skill, R ultimate, Tab cycle target, Esc pause.
`ui/hud.js` → `Hud` (system): DOM overlay (HP, ult meter, skill/ult cooldown radials, charge bar, catch feedback
(PERFECT!), crosshair + lock marker, scoreboard/timer/infield pips, kill feed, banners, Danger Sense red edges, minimap
hiding cloaked/silent enemies, last throw km/h), `banner(text, color, duration)`, `show(b)`.
`ui/heroSelect.js` → `HeroSelect.show(roster, onConfirm(setup))`, `HeroSelect.hide()` (10 hero cards with portraits
`assets/heroes/<id>/portrait.webp`, bilingual names, abilities, team, difficulty, Play).
`ui/loading.js` → `Loading.show()`, `Loading.progress(loaded, total, label)`, `Loading.hide()`, `Loading.error(msg)`.
`ui/pause.js` → pause menu (resume / restart / hero select / controls). Styles in `css/game.css`.

### 3.9 VFX & audio (owner: **fx**)
`vfx/vfx.js` → `Vfx` (system): pooled GPU-friendly particles (InstancedMesh or Points with soft textures):
`play(id, position, { scale, color, direction })`, `attach(id, object3D, { duration, color }) -> handle`,
`stop(handle)`; ids: `hit catch perfectCatch floorDust slideDust shockwave fireTrail beamTrail iceBurst iceTrail frozenMist
glueSplat teleport swapFlash vanishSmoke cloneSpawn cloneDissolve magnetField shieldImpact tackleDust earthquake turretMuzzle
stasisBubble rewindTrail temporalZone reviveBeam ultReady elimination`; also reacts to game events.
`audio/audio.js` → `Audio` (system): Web Audio with procedurally synthesised realistic SFX (rubber ball thumps, gym
floor bounces, hand-slap catches, whooshes, sneaker squeaks, referee whistle, crowd ambience/cheers, ability sounds),
3D panning relative to the camera, `play(id, position, volume, pitch)`, `play2D(id, volume, pitch)`, `unlock()` on first
gesture, event-driven; mute/volume settings.

## 4. Abilities — `js/abilities/heroes/<hero>.js` (owners: **abil-*)
One file per hero exporting three classes `extends AbilityBase` with `static defaults` and registering them:
`registerAbility('rayne.supersonic_meteor', RayneSupersonicMeteor)`. Ids/params/cooldowns are in `roster.js`.
Shared world objects in `js/abilities/shared/<hero>*.js`. `js/abilities/heroes/index.js` imports all ten files.

## 5. Boot (`js/main.js`, kernel)
Renderer → Assets (manifest) → Physics → Arena (`game.court = arena.court`) → systems (Input, CameraRig, Juice, Vfx,
Audio, BallManager, Match, Hud) → preload avatars + clips with the loading screen → `game.start()` → hero select (or
`?autoplay=1`, `?spectate=1`). URL params: `seed`, `quality`, `hero`, `team`, `difficulty`, `debug`.
`window.__DU = { ready, game, stats() }` is used by the smoke test.

## 6. Assets (`docs/assets/`)
`manifest.json`: `heroes[<Hero>] = { avatar, gender ('male'|'female'), folder ('heroes/rayne/'), model ('model.glb'),
portrait, materials: { '<materialName>': { kind: 'body'|'skin'|'hair'|'gear', map, normalMap, ormMap (R=AO 1,
G=roughness, B=metal 0), alphaTest } } }`, `clips.m|f[<key>] = { file, loop }` with keys
`idle breathe lookaround walk run sprint crouch stunned cheer wave defeat`. Bones are Biped:
`Bip01`, `Bip01_Pelvis`, `Bip01_Spine`, `Bip01_Spine1`, `Bip01_Spine2`, `Bip01_Neck`, `Bip01_Head`,
`Bip01_L_Clavicle`, `Bip01_L_UpperArm`, `Bip01_L_Forearm`, `Bip01_L_Hand`, `Bip01_L_Thigh`, `Bip01_L_Calf`,
`Bip01_L_Foot`, (+ R), fingers `Bip01_R_Finger1`… (three.js sanitises spaces to underscores). Clips contain only
bone rotations + `Bip01` translation; remove horizontal root motion at load (keep Y). Textures use glTF UVs (`flipY=false`).
