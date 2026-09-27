// ---------------------------------------------------------------------------------------------------------------
// Hero roster (data-driven, kernel). Every number from the design document lives here. Ability behaviour classes
// are registered by id in abilities/heroes/*.js; `params` override each class's static defaults.
// Avatars: Microsoft Rocketbox realistic humans (MIT), converted by Tools/web/build_assets.py.
// ---------------------------------------------------------------------------------------------------------------
import { SLOT, AI_HINT } from './abilityBase.js';
import { DEFAULT_MAX_HP, THICK_HIDE_MAX_HP, IRON_MITTS_MULTIPLIER } from '../core/constants.js';

/** Baseline movement tuning (m, s). Heroes override a few fields. */
export const BASE_MOVEMENT = Object.freeze({
  walkSpeed: 4.6, sprintSpeed: 7.4, chargingSpeedMul: 0.6, catchingSpeedMul: 0.45,
  acceleration: 34, deceleration: 40, airControl: 0.35, turnSpeed: 720, jumpHeight: 1.05, gravityMul: 1.8,
  slideBoost: 1.2, slideFriction: 6.5, slideDuration: 0.8, slideCooldown: 0.5,
});

/** Baseline throw/catch tuning. */
export const BASE_COMBAT = Object.freeze({
  throwSpeedKmh: 88, minChargeMul: 0.72, fullChargeTime: 0.75, maxChargeTime: 2.5, thrownGravityScale: 0.65,
  throwCooldown: 0.3, passSpeedKmh: 45, aimAssistAngle: 16, aimAssistRange: 32,
  perfectCatchWindow: 0.15, perfectWindowMul: 1, catchWindow: 0.4, whiffRecovery: 0.5, catchConeAngle: 75, catchRadius: 0.85,
  counterBoostDuration: 3, autoPickupRadius: 0.9, manualPickupRadius: 1.6,
});

const ab = (id, slot, name, nameZh, desc, descZh, extra = {}) => ({
  id, slot, name, nameZh, desc, descZh,
  cooldown: 0, castTime: 0, duration: 0, ultCost: 1, requiresBall: false, usableFromOutfield: false, interruptible: true,
  aiHint: AI_HINT.ANYTIME, aiWeight: 0.5, params: {}, ...extra,
});

export const HEROES = [
  {
    id: 'Rayne', title: 'Speedball', titleZh: '速球', role: 'Attacker', roleZh: '攻擊', color: 0xff6a1a,
    avatar: 'Sports_Male_02', height: 1.8, maxHp: DEFAULT_MAX_HP,
    movement: {}, combat: { throwSpeedKmh: 94, maxChargeTime: 2.5 },
    bio: 'A striker who turns every charge into raw velocity.', bioZh: '把每一次蓄力都化為純粹速度的前鋒。',
    abilities: {
      passive: ab('rayne.overcharge', SLOT.PASSIVE, 'Overcharge', '過載',
        'Charging a throw adds up to +50% ball velocity and +20% ball size over 2 s.', '蓄力 2 秒內，球速最多 +50%、球體 +20%。',
        { params: { maxSpeedBonus: 0.5, maxRadiusBonus: 0.2, fullTime: 2 } }),
      skill: ab('rayne.supersonic_meteor', SLOT.SKILL, 'Supersonic Meteor', '超音速隕石',
        'Hurls a fire-infused fastball; on impact a 3 m shockwave knocks nearby enemies back.', '投出火焰快速球，命中時產生 3 公尺衝擊波擊退周圍敵人。',
        { cooldown: 10, aiHint: AI_HINT.ENEMY_IN_RANGE, aiWeight: 0.8, params: { speedMul: 1.6, gravityScale: 0.3, shockwaveRadius: 3, knockback: 7, coreStun: 0.35 } }),
      ultimate: ab('rayne.hyperbeam_transpierce', SLOT.ULTIMATE, 'Hyperbeam Transpierce', '極光貫穿',
        'An unblockable beam-ball that pierces every enemy in its path.', '無法阻擋的光束球，貫穿路徑上所有敵人。',
        { aiHint: AI_HINT.ENEMY_IN_RANGE, aiWeight: 1, params: { speedMul: 2.2, radiusMul: 1.3 } }),
    },
  },
  {
    id: 'Shadow', title: 'Clones', titleZh: '分身', role: 'Attacker', roleZh: '攻擊', color: 0x7a5cff,
    avatar: 'Security_Male_01', height: 1.82, maxHp: DEFAULT_MAX_HP,
    movement: {}, combat: {},
    bio: 'Nobody is sure which one is real - including his teammates.', bioZh: '沒人知道哪一個才是本尊，連隊友也不確定。',
    abilities: {
      passive: ab('shadow.decoy_dash', SLOT.PASSIVE, 'Decoy Dash', '誘餌衝刺',
        'Sprinting leaves a 1 s fading illusion clone.', '衝刺時留下持續 1 秒、逐漸消散的幻影分身。', { params: { interval: 0.9, lifetime: 1 } }),
      skill: ab('shadow.night_parade', SLOT.SKILL, 'Night Parade', '百鬼夜行',
        'Summons 2 clones that mirror your throws and catches; they vanish when hit.', '召喚 2 個同步投接球動作的分身，被球擊中即消失。',
        { cooldown: 12, duration: 6, aiHint: AI_HINT.ENEMY_IN_RANGE, aiWeight: 0.6, params: { count: 2, spacing: 1.4 } }),
      ultimate: ab('shadow.mirage_formation', SLOT.ULTIMATE, 'Mirage Formation', '海市蜃樓陣',
        'Clones every living teammate for 5 s, obscuring the real targets.', '5 秒內為所有存活隊友製造分身，混淆真正的目標。',
        { duration: 5, aiHint: AI_HINT.LOSING, aiWeight: 0.9, params: { clonesPerPlayer: 2, shuffleInterval: 1.2 } }),
    },
  },
  {
    id: 'Gale', title: 'Stealth / Assassin', titleZh: '潛行刺客', role: 'Attacker', roleZh: '攻擊', color: 0x2ad1c9,
    avatar: 'Sports_Female_02', height: 1.7, maxHp: DEFAULT_MAX_HP,
    movement: { walkSpeed: 4.8, sprintSpeed: 7.7 }, combat: {},
    bio: 'You never hear her coming. You only hear the ball.', bioZh: '你永遠聽不到她靠近，只聽得到球聲。',
    abilities: {
      passive: ab('gale.silent_footsteps', SLOT.PASSIVE, 'Silent Footsteps', '無聲步伐',
        'Silent movement and no mini-map ping for enemies.', '移動無聲，且不會出現在敵方小地圖上。', {}),
      skill: ab('gale.optical_camouflage', SLOT.SKILL, 'Optical Camouflage', '光學迷彩',
        'Cloak for 4 s (+20% speed). A throw from stealth gains +30% speed and reveals you.', '隱身 4 秒（移速 +20%），隱身中投球球速 +30% 並現形。',
        { cooldown: 14, duration: 4, aiHint: AI_HINT.HOLDING_BALL, aiWeight: 0.7, params: { haste: 0.2, throwBonus: 0.3 } }),
      ultimate: ab('gale.shadow_strike', SLOT.ULTIMATE, 'Shadow Strike', '暗影突襲',
        'Teleport directly behind the nearest unheld ball and pick it up.', '瞬間移動到最近一顆無人持有的球後方並撿起。',
        { aiHint: AI_HINT.BALLS_LOOSE, aiWeight: 0.8 }),
    },
  },
  {
    id: 'Bear', title: 'Guardian', titleZh: '守護者', role: 'Defender', roleZh: '坦克', color: 0xf2b21b,
    avatar: 'Fire_Male_02', height: 1.9, maxHp: DEFAULT_MAX_HP, hiddenParts: ['helmet'],
    movement: { walkSpeed: 4.3, sprintSpeed: 6.9 }, combat: { perfectWindowMul: IRON_MITTS_MULTIPLIER },
    bio: 'Twenty years of catching burning debris. A dodgeball is nothing.', bioZh: '接了二十年燃燒的碎片，躲避球不算什麼。',
    abilities: {
      passive: ab('bear.iron_mitts', SLOT.PASSIVE, 'Iron Mitts', '鐵手套',
        'Perfect Catch window +50% (0.225 s instead of 0.15 s).', '完美接球判定時間 +50%（0.225 秒）。', { params: { multiplier: IRON_MITTS_MULTIPLIER } }),
      skill: ab('bear.magnetic_pull', SLOT.SKILL, 'Magnetic Pull', '磁力牽引',
        'A 5 m magnetic field pulls any flying ball into your hands for 1.5 s.', '1.5 秒內，5 公尺磁場把飛行中的球吸到手中。',
        { cooldown: 15, duration: 1.5, aiHint: AI_HINT.THREATENED, aiWeight: 0.9, params: { radius: 5, pull: 60 } }),
      ultimate: ab('bear.aegis_barrier', SLOT.ULTIMATE, 'Aegis Barrier', '神盾屏障',
        'Deploys a large energy shield at center court blocking all enemy throws for 6 s.', '在中場展開巨型能量護盾，6 秒內擋下所有敵方投球。',
        { duration: 6, aiHint: AI_HINT.LOSING, aiWeight: 0.9, params: { height: 3.2 } }),
    },
  },
  {
    id: 'Gouki', title: 'Brawler', titleZh: '格鬥家', role: 'Defender', roleZh: '坦克', color: 0xd13b2a,
    avatar: 'Military_Male_01', height: 1.9, maxHp: THICK_HIDE_MAX_HP, hiddenParts: ['helmet', 'combat_knife'],
    movement: { walkSpeed: 4.2, sprintSpeed: 6.8 }, combat: { throwSpeedKmh: 90 },
    bio: 'Takes two hits to put down. Takes one tackle to put you down.', bioZh: '要兩球才打得倒他，他一次衝撞就能撂倒你。',
    abilities: {
      passive: ab('gouki.thick_hide', SLOT.PASSIVE, 'Thick Hide', '厚皮',
        '200 HP: survives two standard hits. Half knockback.', '200 生命值，可承受兩次標準命中；擊退減半。', { params: { knockbackMul: 0.5 } }),
      skill: ab('gouki.tackle_intercept', SLOT.SKILL, 'Tackle Intercept', '擒抱攔截',
        'Charge forward, deflect balls in your path and throw any enemy you hit into the outfield.', '向前衝撞，彈開路徑上的球，撞到的敵人直接被丟進外場。',
        { cooldown: 11, duration: 0.55, interruptible: false, aiHint: AI_HINT.THREATENED, aiWeight: 0.6, params: { speed: 11, deflectRadius: 1.3, grabRadius: 1, crossLine: 2 } }),
      ultimate: ab('gouki.earthquake_slam', SLOT.ULTIMATE, 'Earthquake Slam', '震地猛擊',
        'Slam the ground: every grounded enemy is forced to jump and drops held balls.', '重擊地面，所有著地的敵人被迫跳起並掉落手中的球。',
        { aiHint: AI_HINT.ANYTIME, aiWeight: 0.8, params: { jumpSpeed: 5 } }),
    },
  },
  {
    id: 'Screws', title: 'Engineer', titleZh: '工程師', role: 'Support', roleZh: '輔助', color: 0x55c22b,
    avatar: 'Construction_Male_01', height: 1.8, maxHp: DEFAULT_MAX_HP, hiddenParts: ['helmet'],
    movement: {}, combat: {},
    bio: 'If it moves, he can build a machine to throw it.', bioZh: '只要會動的東西，他都能造台機器把它丟出去。',
    abilities: {
      passive: ab('screws.magnetic_recycle', SLOT.PASSIVE, 'Magnetic Recycle', '磁力回收',
        'Balls dropped on an elimination slowly roll back toward your territory.', '有人被淘汰時掉落的球會慢慢滾回己方場地。', { params: { radius: 3, force: 1.5, duration: 4 } }),
      skill: ab('screws.glue_trap_ball', SLOT.SKILL, 'Glue Trap Ball', '黏膠陷阱球',
        'A viscous ball that leaves a 4 s puddle slowing enemies by 60%.', '黏稠的球，命中或落地產生持續 4 秒、減速 60% 的黏膠。',
        { cooldown: 10, aiHint: AI_HINT.ENEMY_IN_RANGE, aiWeight: 0.6, params: { puddleRadius: 2, puddleDuration: 4, slow: 0.6, speedMul: 0.9 } }),
      ultimate: ab('screws.auto_turret', SLOT.ULTIMATE, 'Auto-Turret', '自動砲台',
        'Deploys a turret for 8 s that collects balls within 4 m and fires at the nearest enemy every 1.5 s.', '部署 8 秒自動砲台，收集 4 公尺內的球，每 1.5 秒射擊最近的敵人。',
        { duration: 8, aiHint: AI_HINT.BALLS_LOOSE, aiWeight: 0.8, params: { collectRadius: 4, fireInterval: 1.5, capacity: 3, shotSpeedKmh: 95 } }),
    },
  },
  {
    id: 'Houdini', title: 'Trickster', titleZh: '魔術師', role: 'Support', roleZh: '輔助', color: 0xc02bd1,
    avatar: 'Business_Male_01', height: 1.8, maxHp: DEFAULT_MAX_HP,
    movement: {}, combat: {},
    bio: 'The ball was in his hand. Then it was not. Then you were out.', bioZh: '球本來在他手上，接著不見了，然後你就出局了。',
    abilities: {
      passive: ab('houdini.hat_trick', SLOT.PASSIVE, 'Hat Trick', '帽子戲法',
        'Pass teleports the held ball straight into the nearest teammate\'s hands.', '傳球時，球直接瞬移到最近隊友手中。', {}),
      skill: ab('houdini.swap_places', SLOT.SKILL, 'Swap Places', '乾坤大挪移',
        'Target an enemy and swap positions after a 0.5 s channel.', '鎖定敵人，引導 0.5 秒後交換位置。',
        { cooldown: 16, castTime: 0.5, aiHint: AI_HINT.ENEMY_IN_RANGE, aiWeight: 0.4, params: { range: 25, stun: 0.3 } }),
      ultimate: ab('houdini.grand_vanish', SLOT.ULTIMATE, 'Grand Vanish', '大消失術',
        'Enemy-held balls vanish for 4 s; every free ball teleports to your team\'s feet.', '敵人手上的球消失 4 秒，場上所有自由球傳送到隊友腳邊。',
        { aiHint: AI_HINT.LOSING, aiWeight: 0.9, params: { vanishTime: 4 } }),
    },
  },
  {
    id: 'Elsa', title: 'Ice Control', titleZh: '冰霜控制', role: 'Support', roleZh: '輔助', color: 0x8fd8ff,
    avatar: 'Pilot_Female_01', height: 1.72, maxHp: DEFAULT_MAX_HP,
    movement: {}, combat: {},
    bio: 'Keeps her cool. Keeps yours too, permanently.', bioZh: '她總是很冷靜，也會讓你永遠冷靜下來。',
    abilities: {
      passive: ab('elsa.frost_trail', SLOT.PASSIVE, 'Frost Trail', '霜之軌跡',
        'Thrown balls leave ice trails that give allies +20% movement speed.', '投出的球留下冰徑，隊友踩上移速 +20%。', { params: { haste: 0.2, segmentLife: 3, spacing: 0.9, width: 0.9 } }),
      skill: ab('elsa.glacier_freeze', SLOT.SKILL, 'Glacier Freeze', '冰河凍結',
        'A freezing ball: enemies hit are frozen for 2.5 s (no moving or catching); a second hit eliminates.', '冰凍球：被擊中者冰凍 2.5 秒（無法移動或接球），再被擊中即淘汰。',
        { cooldown: 13, aiHint: AI_HINT.ENEMY_IN_RANGE, aiWeight: 0.8, params: { freezeTime: 2.5, speedMul: 1.15 } }),
      ultimate: ab('elsa.absolute_zero', SLOT.ULTIMATE, 'Absolute Zero', '絕對零度',
        'Freezes the entire enemy court for 6 s: heavy sliding inertia and no dodges.', '冰封整個敵方場地 6 秒：強烈滑動慣性且無法閃避。',
        { duration: 6, aiHint: AI_HINT.ANYTIME, aiWeight: 0.8, params: { slippery: 0.85 } }),
    },
  },
  {
    id: 'Specter', title: 'Evasion', titleZh: '閃避', role: 'Agility', roleZh: '敏捷', color: 0xe8e8e8,
    avatar: 'Sports_Male_04', height: 1.8, maxHp: DEFAULT_MAX_HP,
    movement: { walkSpeed: 4.9, sprintSpeed: 7.9, slideCooldown: 0.35 }, combat: {},
    bio: 'He moves a split second before the ball does.', bioZh: '他總在球出手前的一瞬間就動了。',
    abilities: {
      passive: ab('specter.danger_sense', SLOT.PASSIVE, 'Danger Sense', '危機感知',
        'Screen edges flash red when a high-speed ball is locked on to you.', '高速球鎖定你時，畫面邊緣閃紅光。', { params: { minSpeedKmh: 90 } }),
      skill: ab('specter.precognition_dodge', SLOT.SKILL, 'Precognition Dodge', '預知閃避',
        '0.8 s invincible sliding dodge that auto-evades every incoming ball.', '0.8 秒無敵滑步，自動閃避所有來球。',
        { cooldown: 8, duration: 0.8, aiHint: AI_HINT.THREATENED, aiWeight: 1 }),
      ultimate: ab('specter.time_reversal', SLOT.ULTIMATE, 'Time Reversal', '時光倒流',
        'If eliminated within 3 s of casting, rewind position and HP to the cast moment.', '施放後 3 秒內若被淘汰，倒轉回施放時的位置與生命值。',
        { duration: 3, aiHint: AI_HINT.THREATENED, aiWeight: 0.9 }),
    },
  },
  {
    id: 'Chrono', title: 'Rewind', titleZh: '時間回溯', role: 'Agility', roleZh: '敏捷', color: 0x3be08a,
    avatar: 'Military_Female_01', height: 1.72, maxHp: DEFAULT_MAX_HP, hiddenParts: ['helmet'],
    movement: {}, combat: {},
    bio: 'Time is just another ball. She catches it too.', bioZh: '時間只是另一顆球，她也接得住。',
    abilities: {
      passive: ab('chrono.delayed_impact', SLOT.PASSIVE, 'Delayed Impact', '延遲衝擊',
        'Elimination is delayed 2 s; if a teammate catches a ball in that window, it is cancelled.', '被淘汰延後 2 秒，期間隊友接到球即取消淘汰。', { params: { delay: 2 } }),
      skill: ab('chrono.stasis_field', SLOT.SKILL, 'Stasis Field', '靜止力場',
        'Freezes a flying enemy ball mid-air for 2 s so allies can snatch it.', '把飛行中的敵球凍結在半空 2 秒，讓隊友搶走。',
        { cooldown: 12, aiHint: AI_HINT.THREATENED, aiWeight: 0.9, params: { duration: 2, range: 18 } }),
      ultimate: ab('chrono.temporal_reset', SLOT.ULTIMATE, 'Temporal Reset', '時間重置',
        'Rewinds a 5x5 m zone\'s ball trajectories and player positions by 3 s.', '把 5x5 公尺區域內的球軌跡與球員位置倒轉 3 秒。',
        { aiHint: AI_HINT.THREATENED, aiWeight: 0.7, params: { size: 5, seconds: 3, range: 25 } }),
    },
  },
];

/** Hero definition by id. */
export const heroById = (id) => HEROES.find((h) => h.id === id) || null;

/** Merged movement/combat profiles for a hero. */
export function heroMovement(hero) { return { ...BASE_MOVEMENT, ...(hero.movement || {}) }; }
export function heroCombat(hero) { return { ...BASE_COMBAT, ...(hero.combat || {}) }; }
