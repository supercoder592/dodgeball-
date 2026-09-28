# Dodgeball Ultra — Architecture & Engineering Contract

Unity 6 LTS (6000.0) · HDRP 17 (realistic rendering) · C# 9 · 3v3 superpowered dodgeball with realistic,
motion-captured human characters (Microsoft Rocketbox, MIT).

This document is the contract every module follows. Files marked `CONTRACT (kernel)` define public APIs shared
between modules: implement their bodies, **never rename or remove their public members** (adding is fine).

---

## 1. Assemblies

| Assembly | Folder | References | Notes |
|---|---|---|---|
| `DodgeballUltra.Core` | `Scripts/Core` | – | **No UnityEngine.** Rules math (rally boost, catch timing, ballistics, cooldowns, FSM). Unit-tested with plain .NET (`Tools/CoreTests`). |
| `DodgeballUltra.Runtime` | `Scripts/Runtime` | Core, UnityEngine.UI, Unity.InputSystem (optional) | All gameplay. `versionDefines`: `DU_INPUT_SYSTEM`, `DU_HDRP`, `DU_URP`. Must not reference SRP packages. |
| `DodgeballUltra.Rendering.HDRP` | `Scripts/Rendering.HDRP` | Runtime + HDRP | `defineConstraints: DU_HDRP`. Installs `ScreenFx.Driver` / `RuntimeRenderingHooks.Active` from `[RuntimeInitializeOnLoadMethod]`. |
| `DodgeballUltra.Editor` | `Scripts/Editor` | Runtime | Setup Wizard, Rocketbox pipeline, generators, scene builder. Built-in fallback of `IEditorRenderingHooks`. |
| `DodgeballUltra.Editor.HDRP` | `Scripts/Editor.HDRP` | Editor + HDRP editor | `HdrpEditorRenderingHooks` registered via `[InitializeOnLoad]`. |
| `DodgeballUltra.Tests.EditMode` | `Tests/EditMode` | Core, Runtime, NUnit | Unity Test Framework tests. |

Pipeline-specific code is **only** in the two `*.HDRP` assemblies; everything else talks to
`ScreenFx`, `RuntimeRenderingHooks`, `EditorRenderingHooks`, `RenderPipelineUtil`, `MaterialFactory`.

## 2. Namespaces / folders (Runtime)

| Namespace | Folder | Owner module |
|---|---|---|
| `DodgeballUltra` | `Common/` | kernel (TeamId, CourtZone, HeroId, GameLayers, PhysicsCompat) |
| `DodgeballUltra.Events` | `Events/` | kernel (`GameEvents` bus + event structs) |
| `DodgeballUltra.Player` | `Player/` | Player |
| `DodgeballUltra.Combat` | `Combat/` | Combat |
| `DodgeballUltra.Abilities` / `.Heroes` | `Abilities/` | kernel (`AbilityBase`, `AbilityData`, `AbilityController`) + Abilities |
| `DodgeballUltra.Characters` | `Characters/` | Characters (+ `HeroRosterFactory` by Abilities) |
| `DodgeballUltra.Juice` | `Juice/` | Juice |
| `DodgeballUltra.CameraSystem` | `CameraSystem/` | Juice/Camera |
| `DodgeballUltra.Match` | `Match/` | Match |
| `DodgeballUltra.Arena` | `Arena/` | Arena |
| `DodgeballUltra.Rendering` | `Rendering/` | Arena/Rendering |
| `DodgeballUltra.AI` | `AI/` | AI |
| `DodgeballUltra.InputHandling` | `InputHandling/` | Input/UI |
| `DodgeballUltra.UI` | `UI/` | Input/UI |
| `DodgeballUltra.VFX` / `.Audio` | `VFX/`, `Audio/` | VFX/Audio |

## 3. Frame flow

```
DodgeballPlayer.Update:
    Intent = InputLocked ? Neutral : IntentSource.Sample()     (HumanInputSource or BotBrain)
    Status.Tick → StateMachine.Tick (states read Intent, command Motor/Combat)
    → skill/ultimate/pass/pickup intents → Combat.Tick → Abilities.Tick → Health.Tick
DodgeballPlayer.FixedUpdate:
    StateMachine.FixedTick → Motor.FixedTick (Rigidbody)
DodgeBall.FixedUpdate (Live):
    BallManager field effects → payload tick → custom gravity → sphere sweep → catch / hit / bounce
Visuals (LateUpdate / OnAnimatorIK): PlayerAnimatorDriver, CharacterIKController, ProceduralLean, camera rig
```

Sub-components of the player never implement `Update`/`FixedUpdate`; `DodgeballPlayer` ticks them in a fixed order.

## 4. Event-driven decoupling

`GameEvents.Publish(new BallHitPlayerEvent{...})` → observed by `JuiceManager` (hitstop, Perlin shake, squash & stretch,
hit flash, screen pulse), `HudController`, `AudioManager`, `VfxManager`, `MatchManager`, AI and passive abilities.
Subscribe in `OnEnable`, unsubscribe in `OnDisable` (abilities use `Listen<T>()`, auto-removed on unequip).
All event structs are in `Events/GameEventTypes.cs`.

**Every hit and every perfect catch MUST reach the juice pipeline**: Combat publishes `BallHitPlayerEvent` /
`BallCaughtEvent`; `JuiceManager` subscribes to both.

## 5. Core rules (spec)

* Perfect Catch ⇔ `0 ≤ t_input ≤ 0.15 s` before impact (`CatchTiming.Classify(impactTime - inputTime)`).
  Reward: revive one outfield teammate, +15 % ultimate, counter-throw +20 % speed. Bear's Iron Mitts → 0.225 s.
* Rally Boost: `V = V_base × (1 + 0.10 × RallyCount)`, capped at 220 km/h, rally resets when the ball touches the floor.
* Hit: standard hit = 100 damage; HP 100 (Gouki 200). HP ≤ 0 → eliminated → ragdoll → outfield.
* Court: 18 m × 9 m, Home defends −Z, Away +Z; each team's outfield strip lies behind the *opponent's* baseline
  (Taiwanese 內場/外場). Outfield players can throw; an outfield hit returns the thrower to the infield (rule toggle).
* Best of 3 rounds (first to 2). Round ends when a team has no infield players, or at time-out (more infield players,
  then more HP, wins; otherwise draw/replay).

## 6. Realistic humans

* Characters are **real, rigged, motion-captured humans** (Microsoft Rocketbox avatars, MIT) — never procedural
  bodies. `CharacterData.modelPrefab` accepts any Humanoid model (Mixamo, Character Creator, MetaHuman export).
* Editor pipeline: download (Setup Wizard or `Tools/fetch_rocketbox.py`, pinned by `Tools/rocketbox_manifest.json`)
  → Humanoid import with explicit Biped bone map + enforced T-pose → HDRP Lit materials (skin SSS, alpha-clipped hair,
  smoothness from specular maps) → LODGroup from the hi/mid/low/ultra-low meshes → prefab → generated AnimatorController
  from Rocketbox motion-capture clips (idle/walk/run/sprint/crouch/dizzy/cheer).
* Sport actions without clips (throw, catch reach) are **procedural IK** layered on mocap (Mecanim `OnAnimatorIK`):
  LookAt tracks incoming balls, hands reach for the predicted intercept point, throw arm winds up and whips through.
  Authored clips (e.g. Mixamo "Throw") are picked up automatically by name if dropped into
  `Assets/DodgeballUltra/Art/Animations/Custom/`.
* Elimination = runtime-built ragdoll from Humanoid bones with an impulse at the hit point.

## 7. Engineering rules (all modules)

1. One `MonoBehaviour`/`ScriptableObject` per file, file name = class name.
2. Rigidbody velocity/damping and physics materials **only** via `PhysicsCompat` (Unity 6 renamed them).
3. Use `Object.FindFirstObjectByType` / `FindAnyObjectByType` / `FindObjectsByType`, never `FindObjectOfType`.
4. APIs newer than Unity 2021.3 must be wrapped in `#if UNITY_6000_0_OR_NEWER` with a fallback (the compile check uses
   2021.3 reference assemblies). No `UnityEditor` in runtime code outside `#if UNITY_EDITOR`.
5. Gameplay uses scaled time (so hitstop freezes it); camera, juice, UI use unscaled time.
6. No per-frame allocations in hot paths: `Physics.*NonAlloc`, cached lists, no LINQ in Update.
7. Managers may be absent (tests, partial scenes): always null-check `X.Instance` or use the static null-safe helpers.
8. Shaders/materials only through `MaterialFactory` / `RenderPipelineUtil` (runtime) or `EditorRenderingHooks` (editor).
9. Expose tuning with `[Header]`, `[Tooltip]`, `[Range]`, `[Min]`; document with XML docs and inline comments.
10. Layers from `GameLayers` (indices fixed; `ProjectSettings/TagManager.asset` names them).
11. Realistic art direction: physically based values (ball 0.105 m radius, ~0.35 kg foam; lux/lumen lights), no
    cartoon shading, no primitive-shaped humans.

## 8. Compile check (no Unity licence needed)

```
Tools/CompileCheck/run.sh                 # all assemblies, 3 runtime configurations
CC_TAG=me Tools/CompileCheck/run.sh Runtime Editor
```
Compiles against Unity 2021.3 reference assemblies (NuGet), a reference build of uGUI, and API-exact stubs for the
Input System / HDRP (`Tools/CompileCheck/Stubs`, copied from the package sources). Use a unique `CC_TAG` when several
builds run concurrently.

The module reference assemblies are player-flavoured, so editor-only engine members (`Light.lightmapBakeType`,
`LightingSettings` bake settings, `LightProbeGroup.probePositions`) are fenced with `#if !DU_CC_PLAYER_REFS` (a symbol
only the harness defines) and mirrored in `Tools/CompileCheck/EditorOnlyApis`, which is checked against the SDK's
editor `UnityEngine.dll`.
