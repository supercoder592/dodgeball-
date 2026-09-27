# Compile check (no Unity licence needed)

Type-checks the game's C# against the real UnityEngine API surface using the .NET SDK.

```bash
Tools/CompileCheck/run.sh                  # everything
CC_TAG=mine Tools/CompileCheck/run.sh Runtime Editor   # selected projects, isolated build folder
dotnet test Tools/CoreTests                # rules unit tests (pure C#)
```

| Project | What it compiles | Defines |
|---|---|---|
| `Core` | `Scripts/Core` (no UnityEngine) | – |
| `Runtime` | `Scripts/Runtime` as the Editor compiles it | `UNITY_EDITOR`, `DU_INPUT_SYSTEM`, `ENABLE_INPUT_SYSTEM`, `ENABLE_LEGACY_INPUT_MANAGER`, `DU_HDRP` |
| `RuntimePlayerInputSystem` | Runtime for a player build with the Input System + HDRP | `DU_INPUT_SYSTEM`, `ENABLE_INPUT_SYSTEM`, `DU_HDRP` |
| `RuntimePlayerLegacy` | Runtime for a player build without optional packages | `ENABLE_LEGACY_INPUT_MANAGER` |
| `RenderingHDRP` | `Scripts/Rendering.HDRP` | `DU_HDRP` |
| `Editor` | `Scripts/Editor` | `UNITY_EDITOR`, `DU_HDRP`, `DU_INPUT_SYSTEM` |
| `EditorHDRP` | `Scripts/Editor.HDRP` | `UNITY_EDITOR`, `DU_HDRP` |
| `TestsEditMode` | `Tests/EditMode` | `UNITY_INCLUDE_TESTS` |

## How it works

* `setup.sh` downloads `UnityEngine.Modules 2021.3.33` (engine reference assemblies) and `Unity3D.SDK 2021.1.14.1`
  (UnityEditor reference) from NuGet, and builds a reference `UnityEngine.UI.dll` from Unity's public uGUI source.
* The Input System and HDRP packages are represented by **API-exact stubs** in `Stubs/` (signatures copied from
  the package sources: Input System 1.x, HDRP/Core RP 17.0.4 for Unity 6000.0).
* Unity 6 renamed a few engine APIs (`Rigidbody.velocity` → `linearVelocity`, `PhysicMaterial` → `PhysicsMaterial`);
  gameplay code goes through `PhysicsCompat`, whose `#if UNITY_6000_0_OR_NEWER` branch is what Unity 6 compiles.

Limits: it proves the code type-checks; it does not run the game or validate serialized assets. Open the project in
Unity 6 for the real thing.
