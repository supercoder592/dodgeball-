# Custom animation clips (optional)

The Setup Wizard builds the animator controllers from Microsoft Rocketbox motion-capture clips
(idle, walk, run, sprint, crouch, dizzy, cheer). Throwing and catching are procedural IK layered on top.

If you have authored sports clips (e.g. from Mixamo), drop Humanoid FBX files here and re-run
**Dodgeball Ultra ▸ Setup Wizard ▸ Build Characters**. Clips are picked up by name:

| Name contains | Used for |
|---|---|
| `throw` | upper-body throw (replaces the procedural throw arc) |
| `catch` | upper-body catch |

Import them with **Rig ▸ Animation Type: Humanoid**. Any Humanoid clip retargets onto the realistic avatars.
