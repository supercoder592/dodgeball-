using System;
using System.Collections.Generic;
using DodgeballUltra.Abilities;
using DodgeballUltra.Abilities.Heroes;
using DodgeballUltra.Combat;
using DodgeballUltra.Core;
using DodgeballUltra.Player;
using UnityEngine;

namespace DodgeballUltra.Characters
{
    /// <summary>
    /// CONTRACT (kernel) - builds the ten launch heroes (CharacterData + their three AbilityData each) with the design-doc
    /// values, fully in memory. Used by:
    /// <list type="bullet">
    /// <item>the editor's GameDataGenerator (which saves them as assets and links the realistic model prefabs), and</item>
    /// <item>GameBootstrap as a fallback when no GameConfig asset is assigned (the game still runs).</item>
    /// </list>
    /// Hero -> Rocketbox avatar casting: Rayne=Sports_Male_02, Shadow=Security_Male_01, Gale=Sports_Female_02,
    /// Bear=Fire_Male_02, Gouki=Military_Male_01, Screws=Construction_Male_01, Houdini=Business_Male_01,
    /// Elsa=Pilot_Female_01, Specter=Sports_Male_04, Chrono=Military_Female_01.
    /// <para>Owner module: Abilities (because it instantiates every ability class).</para>
    /// <para>
    /// Balancing philosophy: every hero shares the realistic baseline (foam ball ~88 km/h full-charge throw, 4.6 m/s jog,
    /// 7.4 m/s sprint, 0.15 s perfect-catch window); per-hero differences are small (a few percent) and physically
    /// motivated by body mass and height - heavy guardians accelerate and turn slower, agile heroes are lighter and
    /// quicker, Rayne throws harder. Hero identity comes from the abilities, not from stat inflation.
    /// </para>
    /// </summary>
    public static class HeroRosterFactory
    {
        /// <summary>All heroes in hero-select order.</summary>
        private static readonly HeroId[] s_order =
        {
            HeroId.Rayne, HeroId.Shadow, HeroId.Gale, HeroId.Bear, HeroId.Gouki,
            HeroId.Screws, HeroId.Houdini, HeroId.Elsa, HeroId.Specter, HeroId.Chrono,
        };

        /// <summary>Creates all ten heroes (new ScriptableObject instances, not saved).</summary>
        public static List<CharacterData> CreateDefaultRoster()
        {
            var roster = new List<CharacterData>(s_order.Length);
            for (int i = 0; i < s_order.Length; i++) roster.Add(CreateHero(s_order[i]));
            return roster;
        }

        /// <summary>Creates one hero.</summary>
        public static CharacterData CreateHero(HeroId hero)
        {
            var c = ScriptableObject.CreateInstance<CharacterData>();
            c.heroId = hero;
            c.name = "Hero_" + hero;
            c.rocketboxAvatar = GetDefaultAvatar(hero);
            c.bodyType = GetBodyType(hero);
            c.modelScale = 1f;
            c.maxHp = GameConstants.DefaultMaxHp;
            c.movement = new MotorProfile();
            c.combat = new CombatProfile();
            c.voicePitch = c.bodyType == BodyType.Female ? 1.15f : 1f;
            c.radius = c.bodyType == BodyType.Female ? 0.29f : 0.32f;

            switch (hero)
            {
                case HeroId.Rayne: BuildRayne(c); break;
                case HeroId.Shadow: BuildShadow(c); break;
                case HeroId.Gale: BuildGale(c); break;
                case HeroId.Bear: BuildBear(c); break;
                case HeroId.Gouki: BuildGouki(c); break;
                case HeroId.Screws: BuildScrews(c); break;
                case HeroId.Houdini: BuildHoudini(c); break;
                case HeroId.Elsa: BuildElsa(c); break;
                case HeroId.Specter: BuildSpecter(c); break;
                case HeroId.Chrono: BuildChrono(c); break;
                default: throw new ArgumentOutOfRangeException(nameof(hero), hero, "Unknown hero.");
            }

            // Every perfect-catch window starts at the spec value; Bear's Iron Mitts scales it at runtime (x1.5).
            c.combat.perfectCatchWindow = GameConstants.PerfectCatchWindow;
            return c;
        }

        /// <summary>Rocketbox avatar folder name for a hero (see class docs).</summary>
        public static string GetDefaultAvatar(HeroId hero)
        {
            switch (hero)
            {
                case HeroId.Rayne: return "Sports_Male_02";
                case HeroId.Shadow: return "Security_Male_01";
                case HeroId.Gale: return "Sports_Female_02";
                case HeroId.Bear: return "Fire_Male_02";
                case HeroId.Gouki: return "Military_Male_01";
                case HeroId.Screws: return "Construction_Male_01";
                case HeroId.Houdini: return "Business_Male_01";
                case HeroId.Elsa: return "Pilot_Female_01";
                case HeroId.Specter: return "Sports_Male_04";
                case HeroId.Chrono: return "Military_Female_01";
                default: return "Sports_Male_02";
            }
        }

        public static BodyType GetBodyType(HeroId hero)
        {
            switch (hero)
            {
                case HeroId.Gale:
                case HeroId.Elsa:
                case HeroId.Chrono:
                    return BodyType.Female;
                default:
                    return BodyType.Male;
            }
        }

        // =====================================================================================================
        // Heroes
        // =====================================================================================================

        private static void BuildRayne(CharacterData c)
        {
            SetIdentity(c, "Rayne", "Speedball", HeroRole.Attacker, new Color(1f, 0.45f, 0.12f),
                "A former college pitcher with a cannon for an arm. Rayne charges throws past their limit and turns every " +
                "rally into a speed contest. (前大學投手，臂力驚人。蓄力投球突破極限，讓每一次回擊都變成速度對決。)");
            SetBody(c, 1.82f, 0.32f, 1f);

            var m = c.movement;
            m.mass = 78f;

            var k = c.combat;
            k.baseThrowSpeedKmh = 94f;       // the hardest natural throw in the roster
            k.minChargeMultiplier = 0.74f;
            k.fullChargeTime = 0.75f;
            k.thrownGravityScale = 0.6f;     // flatter fastballs

            c.passive = Ability("rayne.overcharge", "Overcharge", AbilitySlot.Passive, new RayneOvercharge(), 0f,
                "Charging a throw keeps building power: up to +50% ball velocity and +20% ball radius after 2 s of charge. " +
                "(蓄力投球最多 2 秒：球速最高 +50%、球體 +20%。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("rayne.supersonic_meteor", "Supersonic Meteor", AbilitySlot.Skill, new RayneSupersonicMeteor(), 10f,
                "Hurls a fire-infused fastball. On impact it releases a 3 m shockwave that knocks back nearby enemies. " +
                "(投出火焰快速球，命中時產生 3 公尺衝擊波擊退周圍敵人。)",
                AbilityAIHint.WhenEnemyInRange, 0.8f, usableFromOutfield: true);

            c.ultimate = Ultimate("rayne.hyperbeam_transpierce", "Hyperbeam Transpierce", new RayneHyperbeamTranspierce(), 0f,
                "Fires an unblockable beam-ball that penetrates every enemy in its path. " +
                "(發射無法阻擋的光束球，貫穿路徑上的所有敵人。)",
                AbilityAIHint.WhenEnemyInRange, 0.9f, usableFromOutfield: true);
        }

        private static void BuildShadow(CharacterData c)
        {
            SetIdentity(c, "Shadow", "Clones", HeroRole.Attacker, new Color(0.42f, 0.36f, 0.62f),
                "A covert security operative who never fights alone. Shadow floods the court with illusions until the " +
                "opponents throw at ghosts. (隱密保安特務，從不單打獨鬥。以幻影淹沒球場，讓對手只能對著殘影出手。)");
            SetBody(c, 1.80f, 0.32f, 0.97f);

            var m = c.movement;
            m.walkSpeed = 4.7f;
            m.sprintSpeed = 7.6f;
            m.mass = 74f;

            c.combat.baseThrowSpeedKmh = 89f;

            c.passive = Ability("shadow.decoy_dash", "Decoy Dash", AbilitySlot.Passive, new ShadowDecoyDash(), 0f,
                "Sprinting leaves a fading illusion clone behind for 1 s. (衝刺時留下持續 1 秒、逐漸消散的幻影分身。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("shadow.night_parade", "Night Parade", AbilitySlot.Skill, new ShadowNightParade(), 12f,
                "Summons 2 active clones that mirror your throw and catch animations. Clones vanish when struck by a ball. " +
                "(召喚 2 個同步模仿投球與接球動作的分身，被球擊中即消失。)",
                AbilityAIHint.WhenEnemyInRange, 0.7f, duration: 6f, interruptible: false);

            c.ultimate = Ultimate("shadow.mirage_formation", "Mirage Formation", new ShadowMirageFormation(), 5f,
                "Clones every living teammate at once for 5 s, hiding the real targets among the illusions. " +
                "(同時為所有存活隊友製造分身 5 秒，讓敵人分不清真身。)",
                AbilityAIHint.WhenEnemyInRange, 0.8f, interruptible: false);
        }

        private static void BuildGale(CharacterData c)
        {
            SetIdentity(c, "Gale", "Stealth / Assassin", HeroRole.Attacker, new Color(0.28f, 0.74f, 0.64f),
                "A track sprinter turned assassin. Gale moves without a sound, vanishes from sight and strikes from angles " +
                "nobody is watching. (田徑短跑選手出身的刺客。無聲移動、隱去身形，從無人注意的角度出手。)");
            SetBody(c, 1.68f, 0.29f, 1.18f);

            var m = c.movement;
            m.walkSpeed = 4.8f;
            m.sprintSpeed = 7.8f;
            m.acceleration = 36f;
            m.jumpHeight = 1.1f;
            m.mass = 58f;

            var k = c.combat;
            k.baseThrowSpeedKmh = 88f;
            k.throwCooldown = 0.28f;

            c.passive = Ability("gale.silent_footsteps", "Silent Footsteps", AbilitySlot.Passive, new GaleSilentFootsteps(), 0f,
                "Your movement makes no sound and you never appear on the enemy mini-map. (移動完全無聲，且不會出現在敵方小地圖上。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("gale.optical_camouflage", "Optical Camouflage", AbilitySlot.Skill, new GaleOpticalCamouflage(), 14f,
                "Cloak for 4 s with +20% movement speed. Throwing from stealth adds +30% ball speed but reveals your location. " +
                "(光學迷彩隱身 4 秒並提升 20% 移速；隱身中投球球速 +30%，但會暴露位置。)",
                AbilityAIHint.WhenHoldingBall, 0.75f, duration: 4f);

            c.ultimate = Ultimate("gale.shadow_strike", "Shadow Strike", new GaleShadowStrike(), 0f,
                "Instantly teleports directly behind the nearest unheld ball and picks it up. " +
                "(瞬間移動到最近一顆無人持有的球後方並將其撿起。)",
                AbilityAIHint.WhenBallsLoose, 0.9f);
        }

        private static void BuildBear(CharacterData c)
        {
            SetIdentity(c, "Bear", "Guardian", HeroRole.Defender, new Color(0.2f, 0.45f, 0.9f),
                "A veteran firefighter with hands like oven mitts. Bear plants himself in front of his team and swallows " +
                "every throw that comes his way. (資深消防員，一雙手大如隔熱手套。站在隊友前方，吞下每一顆來球。)");
            SetBody(c, 1.92f, 0.38f, 0.85f);

            var m = c.movement;
            m.walkSpeed = 4.25f;
            m.sprintSpeed = 6.8f;
            m.acceleration = 28f;
            m.deceleration = 34f;
            m.turnSpeed = 600f;
            m.jumpHeight = 0.9f;
            m.slideFriction = 7.5f;
            m.mass = 105f;

            var k = c.combat;
            k.baseThrowSpeedKmh = 85f;
            k.catchRadius = 0.95f;           // long arms, big hands
            k.catchConeAngle = 80f;

            c.passive = Ability("bear.iron_mitts", "Iron Mitts", AbilitySlot.Passive, new BearIronMitts(), 0f,
                "Perfect-catch window +50% (0.225 s instead of 0.15 s). (完美接球判定時間 +50%：0.225 秒取代 0.15 秒。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("bear.magnetic_pull", "Magnetic Pull", AbilitySlot.Skill, new BearMagneticPull(), 15f,
                "Projects a 5 m magnetic field for 1.5 s that pulls any flying ball into your hands. " +
                "(展開 5 公尺磁力場 1.5 秒，將飛行中的球吸入手中。)",
                AbilityAIHint.WhenThreatened, 0.85f, duration: 1.5f);

            c.ultimate = Ultimate("bear.aegis_barrier", "Aegis Barrier", new BearAegisBarrier(), 6f,
                "Raises a large energy shield at center court that blocks every incoming opponent throw for 6 s. " +
                "(在中線升起巨大能量護盾 6 秒，擋下所有對手投來的球。)",
                AbilityAIHint.WhenLosing, 0.8f, interruptible: false);
        }

        private static void BuildGouki(CharacterData c)
        {
            SetIdentity(c, "Gouki", "Brawler", HeroRole.Defender, new Color(0.78f, 0.16f, 0.12f),
                "An ex-soldier built like a wall. Gouki shrugs off a hit that would send anyone else to the outfield and " +
                "answers by tackling through the enemy line. (退役軍人，體格如牆。挨一球照樣屹立，隨即衝撞穿越敵陣。)");
            SetBody(c, 1.90f, 0.40f, 0.82f);
            c.maxHp = GameConstants.ThickHideMaxHp; // Thick Hide: 200 HP, survives two standard hits

            var m = c.movement;
            m.walkSpeed = 4.15f;
            m.sprintSpeed = 6.7f;
            m.acceleration = 27f;
            m.deceleration = 32f;
            m.turnSpeed = 580f;
            m.jumpHeight = 0.88f;
            m.slideFriction = 7.8f;
            m.mass = 115f;

            var k = c.combat;
            k.baseThrowSpeedKmh = 91f;       // raw power, slower wind-up
            k.fullChargeTime = 0.82f;
            k.catchRadius = 0.9f;

            c.passive = Ability("gouki.thick_hide", "Thick Hide", AbilitySlot.Passive, new GoukiThickHide(), 0f,
                "200 HP: survives two standard hits. (200 點生命值，可承受兩次標準命中。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("gouki.tackle_intercept", "Tackle Intercept", AbilitySlot.Skill, new GoukiTackleIntercept(), 11f,
                "Charges forward, deflecting flying balls in your path; any enemy you collide with is grabbed and thrown into " +
                "the outfield. (向前衝撞，彈開路徑上的飛球；撞到的敵人會被抓起並摔進外場。)",
                AbilityAIHint.WhenThreatened, 0.7f);

            c.ultimate = Ultimate("gouki.earthquake_slam", "Earthquake Slam", new GoukiEarthquakeSlam(), 0f,
                "Slams the ground, forcing every grounded enemy to jump and drop any ball they hold. " +
                "(重擊地面，迫使所有站在地上的敵人跳起並掉落手中的球。)",
                AbilityAIHint.Anytime, 0.7f);
        }

        private static void BuildScrews(CharacterData c)
        {
            SetIdentity(c, "Screws", "Engineer", HeroRole.Support, new Color(0.96f, 0.74f, 0.16f),
                "A site foreman who brought his toolbox to the court. Screws recycles every loose ball, glues the enemy in " +
                "place and lets his turret do the throwing. (把工具箱帶上球場的工地領班。回收散落的球、用黏膠困住敵人，讓砲台替他出手。)");
            SetBody(c, 1.78f, 0.34f, 0.95f);

            var m = c.movement;
            m.walkSpeed = 4.5f;
            m.sprintSpeed = 7.2f;
            m.mass = 84f;

            c.combat.baseThrowSpeedKmh = 86f;

            c.passive = Ability("screws.magnetic_recycle", "Magnetic Recycle", AbilitySlot.Passive, new ScrewsMagneticRecycle(), 0f,
                "Balls dropped when you are eliminated slowly roll back toward friendly territory. " +
                "(被淘汰時掉落的球會緩緩滾回己方場地。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("screws.glue_trap_ball", "Glue Trap Ball", AbilitySlot.Skill, new ScrewsGlueTrapBall(), 10f,
                "Throws a viscous ball that leaves a 4 s puddle on impact or ground contact, slowing enemies by 60%. " +
                "(投出黏膠球，命中或落地時留下持續 4 秒的黏液，使敵人減速 60%。)",
                AbilityAIHint.WhenEnemyInRange, 0.7f, usableFromOutfield: true);

            c.ultimate = Ultimate("screws.auto_turret", "Auto-Turret", new ScrewsAutoTurret(), 8f,
                "Deploys a turret for 8 s that automatically picks up balls within 4 m and fires at the nearest enemy every " +
                "1.5 s. (部署自動砲台 8 秒：自動撿取 4 公尺內的球，每 1.5 秒朝最近的敵人發射。)",
                AbilityAIHint.Anytime, 0.8f, interruptible: false);
        }

        private static void BuildHoudini(CharacterData c)
        {
            SetIdentity(c, "Houdini", "Trickster", HeroRole.Support, new Color(0.62f, 0.2f, 0.72f),
                "A corporate magician who treats the court like a stage. Houdini makes balls - and people - appear exactly " +
                "where he wants them. (把球場當舞台的商務魔術師。讓球與人都出現在他想要的位置。)");
            SetBody(c, 1.83f, 0.32f, 1.02f);

            var m = c.movement;
            m.mass = 76f;

            var k = c.combat;
            k.baseThrowSpeedKmh = 86f;
            k.passSpeedKmh = 55f;

            c.passive = Ability("houdini.hat_trick", "Hat Trick", AbilitySlot.Passive, new HoudiniHatTrick(), 0f,
                "Pressing Pass teleports the held ball directly into the nearest teammate's hands. " +
                "(按下傳球時，手上的球直接瞬移到最近隊友手中。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("houdini.swap_places", "Swap Places", AbilitySlot.Skill, new HoudiniSwapPlaces(), 16f,
                "Target an enemy and swap positions with them after a 0.5 s channel. (鎖定一名敵人，引導 0.5 秒後與其交換位置。)",
                AbilityAIHint.WhenEnemyInRange, 0.55f, castTime: 0.5f);

            c.ultimate = Ultimate("houdini.grand_vanish", "Grand Vanish", new HoudiniGrandVanish(), 0f,
                "Despawns every enemy-held ball for 4 s and teleports all free balls to your team's feet. " +
                "(讓敵方手中的球全部消失 4 秒，並把所有無人持有的球瞬移到隊友腳邊。)",
                AbilityAIHint.WhenLosing, 0.85f);
        }

        private static void BuildElsa(CharacterData c)
        {
            SetIdentity(c, "Elsa", "Ice Control", HeroRole.Support, new Color(0.55f, 0.84f, 1f),
                "A calm airline pilot who keeps her cool at 10,000 metres - and on court. Elsa freezes opponents solid and " +
                "turns their half into an ice rink. (在萬米高空也冷靜自若的機長。將對手冰封，把敵方半場變成溜冰場。)");
            SetBody(c, 1.70f, 0.29f, 1.2f);

            var m = c.movement;
            m.walkSpeed = 4.6f;
            m.sprintSpeed = 7.3f;
            m.mass = 60f;

            c.combat.baseThrowSpeedKmh = 86f;

            c.passive = Ability("elsa.frost_trail", "Frost Trail", AbilitySlot.Passive, new ElsaFrostTrail(), 0f,
                "Your thrown balls leave ice trails that increase ally movement speed by 20%. (投出的球留下冰徑，隊友移速 +20%。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("elsa.glacier_freeze", "Glacier Freeze", AbilitySlot.Skill, new ElsaGlacierFreeze(), 13f,
                "Throws a freezing ball: a hit freezes the enemy for 2.5 s (cannot move or catch, cannot be thawed by allies); " +
                "a second hit while frozen eliminates. (投出冰凍球：命中使敵人冰凍 2.5 秒，無法移動與接球且隊友無法解凍；冰凍中再被命中即淘汰。)",
                AbilityAIHint.WhenEnemyInRange, 0.8f, usableFromOutfield: true);

            c.ultimate = Ultimate("elsa.absolute_zero", "Absolute Zero", new ElsaAbsoluteZero(), 6f,
                "Freezes the entire enemy court for 6 s: heavy inertia and sliding, and dodges are disabled. " +
                "(冰封整個敵方半場 6 秒：移動變得沉重打滑，且無法閃避。)",
                AbilityAIHint.Anytime, 0.8f, interruptible: false);
        }

        private static void BuildSpecter(CharacterData c)
        {
            SetIdentity(c, "Specter", "Evasion", HeroRole.Agility, new Color(0.8f, 0.85f, 0.96f),
                "A parkour athlete with an uncanny sense for danger. Specter reads every throw before it leaves the hand " +
                "and is gone before it arrives. (擁有超常危機感的跑酷運動員。在球離手前就讀懂來勢，在球抵達前早已消失。)");
            SetBody(c, 1.79f, 0.31f, 1.05f);

            var m = c.movement;
            m.walkSpeed = 4.9f;
            m.sprintSpeed = 8.0f;
            m.acceleration = 38f;
            m.turnSpeed = 780f;
            m.jumpHeight = 1.12f;
            m.slideBoost = 1.3f;
            m.slideCooldown = 0.4f;
            m.mass = 70f;

            var k = c.combat;
            k.baseThrowSpeedKmh = 86f;
            k.catchRadius = 0.82f;

            c.passive = Ability("specter.danger_sense", "Danger Sense", AbilitySlot.Passive, new SpecterDangerSense(), 0f,
                "The screen edges flash red when a high-speed ball is locked on to you. (高速球鎖定你時，畫面邊緣閃爍紅光。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("specter.precognition_dodge", "Precognition Dodge", AbilitySlot.Skill, new SpecterPrecognitionDodge(), 8f,
                "A 0.8 s invincible sliding dodge that automatically evades every incoming ball. " +
                "(0.8 秒無敵滑步閃避，自動躲開所有來球。)",
                AbilityAIHint.WhenThreatened, 0.95f, duration: 0.8f, interruptible: false);

            c.ultimate = Ultimate("specter.time_reversal", "Time Reversal", new SpecterTimeReversal(), 3f,
                "If you are eliminated within 3 s of casting, time rewinds your position and HP back to the moment of casting. " +
                "(施放後 3 秒內若被淘汰，位置與生命值將倒轉回施放瞬間。)",
                AbilityAIHint.WhenThreatened, 0.85f, interruptible: false);
        }

        private static void BuildChrono(CharacterData c)
        {
            SetIdentity(c, "Chrono", "Rewind", HeroRole.Agility, new Color(0.95f, 0.78f, 0.38f),
                "A military strategist who plays the match three seconds ahead. Chrono stops balls mid-flight, bends fate " +
                "for her teammates and rewinds the court itself. (領先比賽三秒的軍事戰略家。讓球懸停半空、為隊友扭轉命運，甚至倒轉球場本身。)");
            SetBody(c, 1.72f, 0.29f, 1.12f);

            var m = c.movement;
            m.walkSpeed = 4.8f;
            m.sprintSpeed = 7.7f;
            m.acceleration = 36f;
            m.turnSpeed = 760f;
            m.jumpHeight = 1.08f;
            m.mass = 62f;

            c.combat.baseThrowSpeedKmh = 86f;

            c.passive = Ability("chrono.delayed_impact", "Delayed Impact", AbilitySlot.Passive, new ChronoDelayedImpact(), 0f,
                "Your elimination is delayed 2 s after being hit; if a teammate catches a ball during this window, the " +
                "elimination is cancelled. (被命中後淘汰延遲 2 秒；期間若隊友接住任何一球，淘汰即取消。)",
                AbilityAIHint.Never, 0f);

            c.skill = Ability("chrono.stasis_field", "Stasis Field", AbilitySlot.Skill, new ChronoStasisField(), 12f,
                "Freezes a flying enemy ball mid-air for 2 s so your allies can snatch it. (將飛行中的敵球凍結於半空 2 秒，讓隊友搶下。)",
                AbilityAIHint.WhenThreatened, 0.85f);

            c.ultimate = Ultimate("chrono.temporal_reset", "Temporal Reset", new ChronoTemporalReset(), 0f,
                "Rewinds a targeted 5 x 5 m zone: ball trajectories and player positions inside it return to where they were " +
                "3 s ago. (將指定 5×5 公尺區域內的球軌跡與球員位置倒轉回 3 秒前。)",
                AbilityAIHint.WhenThreatened, 0.8f);
        }

        // =====================================================================================================
        // Builders
        // =====================================================================================================

        private static void SetIdentity(CharacterData c, string displayName, string title, HeroRole role, Color theme, string description)
        {
            c.displayName = displayName;
            c.title = title;
            c.role = role;
            c.themeColor = theme;
            c.description = description;
        }

        /// <param name="height">Physical height (m) for the collision capsule.</param>
        /// <param name="radius">Capsule radius (m) - broader for the heavy guardians.</param>
        /// <param name="voicePitch">Vocal effort pitch multiplier.</param>
        private static void SetBody(CharacterData c, float height, float radius, float voicePitch)
        {
            c.height = Mathf.Clamp(height, 1.4f, 2.2f);
            c.radius = Mathf.Clamp(radius, 0.2f, 0.6f);
            c.voicePitch = Mathf.Clamp(voicePitch, 0.6f, 1.6f);
        }

        /// <summary>Passive or skill AbilityData.</summary>
        private static AbilityData Ability(string id, string displayName, AbilitySlot slot, AbilityBase logic, float cooldown,
            string description, AbilityAIHint aiHint, float aiWeight, float castTime = 0f, float duration = 0f,
            bool usableFromOutfield = false, bool interruptible = true)
        {
            var data = AbilityData.Create(id, displayName, slot, logic, slot == AbilitySlot.Passive ? 0f : cooldown,
                castTime, duration, description);
            data.requiresBall = false; // special balls are conjured by the abilities themselves
            data.usableFromOutfield = usableFromOutfield;
            data.interruptible = interruptible;
            data.aiHint = aiHint;
            data.aiWeight = Mathf.Clamp01(aiWeight);
            data.ultimateCost = slot == AbilitySlot.Ultimate ? 1f : 0f;
            return data;
        }

        /// <summary>Ultimate AbilityData: no cooldown, gated by a full meter (ultimateCost 1).</summary>
        private static AbilityData Ultimate(string id, string displayName, AbilityBase logic, float duration, string description,
            AbilityAIHint aiHint, float aiWeight, bool usableFromOutfield = false, bool interruptible = true)
        {
            return Ability(id, displayName, AbilitySlot.Ultimate, logic, 0f, description, aiHint, aiWeight, 0f, duration,
                usableFromOutfield, interruptible);
        }
    }
}
