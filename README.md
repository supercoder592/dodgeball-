# Dodgeball Ultra 🏐 — 3v3 超能力躲避球

**Unity 6 LTS · HDRP 寫實渲染 · 真人動作捕捉角色 · 10 名英雄 · 3v3（你 + 5 個 AI）**

> 寫實風格的 3v3 超能力躲避球。角色使用 **Microsoft Rocketbox 真人模型**（完整骨架綁定、寫實貼圖、MIT 授權）與
> **真人動作捕捉動畫**，不是程式生成的人體。投球、接球等運動動作以 IK 疊加在動捕動畫上；被淘汰時切換為物理布娃娃（Ragdoll）。

*English version below.*

---

## 快速開始（繁體中文）

1. 安裝 **Unity 6 LTS（6000.0.x）**，用 Unity Hub「Add project from disk」開啟此資料夾。
   首次開啟會自動安裝 HDRP、Input System 等套件（需數分鐘）。若 Input System 詢問是否啟用新輸入系統，選 **Yes** 並重新啟動編輯器。
2. 開啟選單 **Dodgeball Ultra ▸ Setup Wizard**（首次開啟專案會自動跳出），按 **Run All**，或依序執行：
   1. **Configure Project** – 建立並指定 HDRP 管線（次表面散射皮膚、SSR、SSAO、接觸陰影、體積霧）。
   2. **Generate Game Data** – 產生 10 名英雄、30 個技能的 ScriptableObject 資料。
   3. **Download Realistic Avatars** – 從 Microsoft Rocketbox（固定版本、MIT）下載 10 名英雄的真人模型、貼圖與動作捕捉動畫
      （每名角色約 90 MB，存放於 `Assets/ThirdParty/Rocketbox/`，不會提交到 git）。
   4. **Build Characters** – 自動設定 Humanoid 骨架（強制 T-Pose）、HDRP 材質（皮膚 SSS、頭髮 Alpha Clip）、LOD、動畫控制器與角色 Prefab。
   5. **Build Arena Scene** – 產生寫實室內體育館場景（木地板球場、看台、泛光燈、HDRP 燈光與後製）。
3. 開啟 `Assets/DodgeballUltra/Scenes/Arena.unity` 按 Play。
   （`Scenes/QuickPlay.unity` 可在任何時候直接 Play：場館與系統會在執行期自動建立。）

> 也可用命令列下載模型：`python3 Tools/fetch_rocketbox.py --avatars all-heroes --animations`

### 操作

| 動作 | 鍵盤滑鼠 | 手把 |
|---|---|---|
| 移動 / 視角 | WASD / 滑鼠 | 左搖桿 / 右搖桿 |
| 衝刺 / 跳躍 / 滑行 | Shift / Space / C 或 Ctrl | L3 / A / B |
| 投球（按住蓄力） | 左鍵 | RT |
| 接球（抓時機！） | 右鍵 | LT |
| 傳球 / 撿球 | Q / E | Y / X |
| 技能 / 大絕 | F / R | RB / LB |
| 切換目標 / 暫停 | Tab / Esc | 十字鍵 / Start |

### 核心規則

* **完美接球**：`0 ≤ t_input ≤ 0.15 秒（撞擊前）` 按下接球 → 復活一名外場隊友、大絕 +15%、反擊球速 +20%。Bear 的 Iron Mitts 放寬到 0.225 秒。
* **連續回擊加速**：`V = V_base × (1 + 0.10 × RallyCount)`，上限 220 km/h；球一落地即歸零。
* **內場／外場**：被擊中淘汰後進入對方底線後方的外場，仍可投球；外場擊中對手可回到內場。三戰兩勝。

### 英雄

| 定位 | 英雄 | 被動 | 技能 | 大絕 |
|---|---|---|---|---|
| 攻擊 | **Rayne** 速球 | Overcharge 蓄力增速 +50%、球體 +20% | Supersonic Meteor 火焰快速球＋3m 衝擊波 | Hyperbeam Transpierce 無法阻擋的貫穿光束球 |
| 攻擊 | **Shadow** 分身 | Decoy Dash 衝刺殘影 | Night Parade 兩個同步分身 | Mirage Formation 全隊分身 5 秒 |
| 攻擊 | **Gale** 刺客 | Silent Footsteps 無聲、小地圖隱形 | Optical Camouflage 隱身 4 秒 | Shadow Strike 瞬移到最近的球後方並撿起 |
| 坦克 | **Bear** 守護者 | Iron Mitts 完美接球窗口 +50% | Magnetic Pull 5m 磁力吸球 | Aegis Barrier 中場能量護盾 6 秒 |
| 坦克 | **Gouki** 格鬥家 | Thick Hide 200 HP | Tackle Intercept 衝撞攔截 | Earthquake Slam 地震震落敵球 |
| 輔助 | **Screws** 工程師 | Magnetic Recycle 球滾回己方 | Glue Trap Ball 黏膠減速 60% | Auto-Turret 自動砲台 8 秒 |
| 輔助 | **Houdini** 魔術師 | Hat Trick 傳球瞬移 | Swap Places 交換位置 | Grand Vanish 敵球消失、球傳送到隊友腳下 |
| 輔助 | **Elsa** 冰控 | Frost Trail 冰徑加速 +20% | Glacier Freeze 冰凍 2.5 秒 | Absolute Zero 敵方全場冰封 6 秒 |
| 敏捷 | **Specter** 閃避 | Danger Sense 危險感知 | Precognition Dodge 0.8 秒無敵滑步 | Time Reversal 時間倒轉 |
| 敏捷 | **Chrono** 時間 | Delayed Impact 延遲淘汰 | Stasis Field 凍結飛行中的球 | Temporal Reset 5×5m 區域倒轉 3 秒 |

---

## Quick start (English)

1. Open the folder with **Unity 6 LTS (6000.0.x)**. HDRP and the Input System install automatically (accept the Input
   System's "enable new backend" prompt and restart).
2. **Dodgeball Ultra ▸ Setup Wizard ▸ Run All**: configures HDRP, generates hero/ability data, downloads the realistic
   Microsoft Rocketbox avatars + motion-capture clips (MIT, pinned commit, ~90 MB per hero, git-ignored), builds
   Humanoid prefabs/materials/animator controllers, and builds the realistic arena scene.
3. Play `Assets/DodgeballUltra/Scenes/Arena.unity` (or `QuickPlay.unity`, which builds everything at runtime).

## Architecture

See **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**. Highlights:

* **Event-driven** – `GameEvents` typed, allocation-free pub/sub; combat publishes facts, and Juice/UI/Audio/VFX/AI observe.
* **PlayerStateMachine** – Grounded, Airborne, Sprinting, Sliding, ChargingThrow, Catching, Stunned, Incapacitated
  (on a pure-C# `FiniteStateMachine`).
* **Data-driven abilities** – `AbilityData` (ScriptableObject) holds a `[SerializeReference]` `AbilityBase` subclass with
  hooks `OnCast / OnTick / OnInterrupt / OnCooldown`; `CharacterData` holds the model, stats and three abilities.
  Reference implementation: `RayneSupersonicMeteor.cs`.
* **Juice** – `JuiceManager`: dynamic hitstop (0.03–0.1 s), Perlin trauma camera shake, volume-preserving squash &
  stretch along the impact normal, 0.05 s material hit-flash, HDRP screen pulses.
* **Motion** – Rigidbody motor with acceleration curves and slide friction, procedural lean, Mecanim LookAt/catch/throw IK,
  runtime-built ragdoll with impulse transition.
* **Rules core** – `DodgeballUltra.Core` is engine-free and unit-tested (`dotnet test Tools/CoreTests`).
* **Compile check without Unity** – `Tools/CompileCheck/run.sh` type-checks every assembly against Unity reference
  assemblies (also runs in GitHub Actions).

## Repository layout

```
Assets/DodgeballUltra/
  Scripts/Core             pure C# rules (rally, catch timing, ballistics, FSM)
  Scripts/Runtime          gameplay: Player, Combat, Abilities/Heroes, Characters, Juice, Match, AI, UI, VFX, Audio, Arena
  Scripts/Rendering.HDRP   HDRP adapters (screen FX, lights, volumes)
  Scripts/Editor           Setup Wizard, Rocketbox pipeline, data/scene generators
  Scripts/Editor.HDRP      HDRP project/material/lighting setup
  Tests/EditMode           Unity Test Runner tests
  Scenes/                  QuickPlay (runtime bootstrap); Arena is generated by the wizard
Tools/
  rocketbox_manifest.json  pinned list of avatar/animation files
  fetch_rocketbox.py       command-line downloader
  CompileCheck/            .NET compile check against Unity reference assemblies
  CoreTests/               rules unit tests
```

## Licences

Game code: this repository. Characters & animations: Microsoft Rocketbox (MIT) — downloaded, not committed.
See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
