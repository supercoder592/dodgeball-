// ---------------------------------------------------------------------------------------------------------------
// Service worker (deployed site only): makes repeat visits near-instant and never pins players to an old build.
// Registered by index.html (never on localhost unless ?sw=1; ?sw=0 unregisters it and deletes its caches).
//   * Tools/web/deploy_pages.sh replaces the three placeholders below: VERSION (commit + content hash), HASHES
//     (published path -> content hash of every cacheable file) and SHELL (paths precached on install).
//   * Cache-first for every path in HASHES (bundle/<hash>/, assets/, css/, js/, vendor/): one cache per deploy,
//     'du-<VERSION>'. On install, entries whose content hash did not change are copied over from the previous
//     deploy's cache (a new deploy only downloads what changed), then SHELL is fetched; activate deletes every other
//     'du-*' cache and claims open pages. skipWaiting + clients.claim: index.html reloads an already-controlled page
//     once when the new worker takes over, but only while hero select is open (never during a match).
//   * Network-first (cache as offline fallback) for navigations, index.html, version.json and anything else.
//   * Mixed builds are impossible: build_site.mjs stamps VERSION into index.html (<html data-du-build>). A navigation
//     whose fresh page names another build (a new deploy while this older worker still controls the scope) marks that
//     page as foreign: its requests bypass this worker's cache (revalidated network, nothing stored) until the new
//     worker takes over. The page asks the worker that claims it for its VERSION ('du:version' message) and reloads
//     only when that differs from its own build.
//   * Unreplaced placeholders (docs/ served by the dev server with ?sw=1) = dev mode: network-first for everything.
// ---------------------------------------------------------------------------------------------------------------
/* eslint-env serviceworker */
const VERSION = "a599360-7b875a59";
/** @type {Record<string,string>} published path (relative to the scope) -> content hash */
const HASHES = {"assets/LICENSE-Rocketbox.txt":"F0dOOG4Lnhpw","assets/anims/f/breathe.glb":"zq02cC8cRJXA","assets/anims/f/cheer.glb":"CgCAl6wgy2p2","assets/anims/f/crouch.glb":"RSSv0S8kY93M","assets/anims/f/defeat.glb":"Sqpch9sdp-j3","assets/anims/f/idle.glb":"5x_tBslBQYy0","assets/anims/f/lookaround.glb":"M7Nv4GzcB-Ae","assets/anims/f/run.glb":"824BS02PQ9eb","assets/anims/f/sprint.glb":"Rf1Ny6W4mKHk","assets/anims/f/stunned.glb":"SXJWzcxkvdEy","assets/anims/f/walk.glb":"NhBLX2C6eJCm","assets/anims/f/wave.glb":"eVpSuCbQQJnK","assets/anims/m/breathe.glb":"fN8HCns0ywVz","assets/anims/m/cheer.glb":"WOK7gVYYvpY_","assets/anims/m/crouch.glb":"qJsJvEU0KFmi","assets/anims/m/defeat.glb":"CnXVu7koBpdC","assets/anims/m/idle.glb":"M9E5Oy8ykCvt","assets/anims/m/lookaround.glb":"wNWvy9PcMoKN","assets/anims/m/run.glb":"ZDwxM4uYSe2G","assets/anims/m/sprint.glb":"YMBfPqKHaAG1","assets/anims/m/stunned.glb":"VFwdmdy3vy4M","assets/anims/m/walk.glb":"wCWs7bSE1bBu","assets/anims/m/wave.glb":"m1LllxwVJ18I","assets/crowd/atlas-128.webp":"t8EfjuF7bh9R","assets/crowd/atlas-64.webp":"wFBMHOVmxfKC","assets/crowd/atlas-96.webp":"VHCoj1fOZ9EB","assets/crowd/atlas.json":"QtOOb3enO1zG","assets/heroes/bear/body_color.webp":"m-OObLWmiswx","assets/heroes/bear/body_normal.webp":"y6_Sf7Rmm8vd","assets/heroes/bear/body_orm.webp":"1BhwNI1rmUwd","assets/heroes/bear/head_color.webp":"0c-jOnzV1KRm","assets/heroes/bear/head_normal.webp":"nnj4VLolZdWG","assets/heroes/bear/head_orm.webp":"5YgQrgi7Jy30","assets/heroes/bear/helmet_color.webp":"EYIYZVSLKyWu","assets/heroes/bear/helmet_normal.webp":"Kd9WB0ViB4FV","assets/heroes/bear/helmet_orm.webp":"ZkffWFnyUavP","assets/heroes/bear/model.glb":"02lgCeW4uChI","assets/heroes/bear/opacity_color.webp":"Le9I_N_1QZ6g","assets/heroes/bear/opacity_normal.webp":"vfNsxQ2i3Lyi","assets/heroes/bear/opacity_orm.webp":"9Nc_848a116V","assets/heroes/bear/portrait.webp":"YDrVeqI8J8gz","assets/heroes/chrono/body_color.webp":"1ejjlrWfP1Hv","assets/heroes/chrono/body_normal.webp":"p9gsjcWVxY1z","assets/heroes/chrono/body_orm.webp":"503LpVuPVgag","assets/heroes/chrono/equipment_color.webp":"_z3QX186Qfuh","assets/heroes/chrono/equipment_normal.webp":"OyJSAk5Zc-7R","assets/heroes/chrono/equipment_orm.webp":"ujqY3-nGwf-5","assets/heroes/chrono/head_color.webp":"Ow9VZFEAU7o0","assets/heroes/chrono/head_normal.webp":"TTek6Vq_1h-I","assets/heroes/chrono/head_orm.webp":"ogAdlukn8CiG","assets/heroes/chrono/helmet_color.webp":"G1IvaYO3qIfi","assets/heroes/chrono/helmet_normal.webp":"sYX1E8VnZJxy","assets/heroes/chrono/helmet_orm.webp":"iA06nLMLUuoq","assets/heroes/chrono/model.glb":"aeeHUWJPvCd4","assets/heroes/chrono/portrait.webp":"I0QmCODfHNKY","assets/heroes/elsa/body_color.webp":"n6pPS0QNMcOB","assets/heroes/elsa/body_normal.webp":"2nCelbtzBir-","assets/heroes/elsa/body_orm.webp":"pyRDrqSx6pYd","assets/heroes/elsa/hat_color.webp":"zKTd7bFEnrnQ","assets/heroes/elsa/hat_normal.webp":"zw4B1BGjfL8J","assets/heroes/elsa/hat_orm.webp":"2sAle44eyhHO","assets/heroes/elsa/head_color.webp":"JTyZmG0gClYg","assets/heroes/elsa/head_normal.webp":"lTcxcP43M6Qo","assets/heroes/elsa/head_orm.webp":"qimUB4zBKWNI","assets/heroes/elsa/model.glb":"Jrk9pjyULiuJ","assets/heroes/elsa/portrait.webp":"TFw0F3ZQE8Yl","assets/heroes/gale/body_color.webp":"XGlqJDSj6knC","assets/heroes/gale/body_normal.webp":"nS71nDL5CzIj","assets/heroes/gale/body_orm.webp":"GtYvZJAHDaeH","assets/heroes/gale/head_color.webp":"qezdUFomIMjt","assets/heroes/gale/head_normal.webp":"XaQ-dwMBtIv3","assets/heroes/gale/head_orm.webp":"-lwVccMR7H1p","assets/heroes/gale/model.glb":"KYch1rZVQFdR","assets/heroes/gale/opacity_color.webp":"VPyq3KlEBBEG","assets/heroes/gale/portrait.webp":"3FTdIRw0sx83","assets/heroes/gouki/body_color.webp":"HhaKUFRxc4mu","assets/heroes/gouki/body_normal.webp":"3dYC_iRQPb0k","assets/heroes/gouki/body_orm.webp":"nY0NY_BslA4r","assets/heroes/gouki/combat_knife_color.webp":"dwMKZUDzW74b","assets/heroes/gouki/combat_knife_normal.webp":"kMGyvp7hfcxf","assets/heroes/gouki/combat_knife_orm.webp":"Thlx5Lo5THZg","assets/heroes/gouki/equipment_color.webp":"fO54t__JwiMc","assets/heroes/gouki/equipment_normal.webp":"ynjMznPJAMZ4","assets/heroes/gouki/equipment_orm.webp":"esdfhTzazwIo","assets/heroes/gouki/head_color.webp":"7hSw-asbGrkN","assets/heroes/gouki/head_normal.webp":"LUhuiRw39BtN","assets/heroes/gouki/head_orm.webp":"SXWYYnOp71M8","assets/heroes/gouki/helmet_color.webp":"iKQB2QYVTZyl","assets/heroes/gouki/helmet_normal.webp":"9T1phZCMeu69","assets/heroes/gouki/helmet_orm.webp":"X9e2cO1-It1G","assets/heroes/gouki/model.glb":"h8Djux8tsrHK","assets/heroes/gouki/portrait.webp":"yg3LIk0ZGwHd","assets/heroes/houdini/body_color.webp":"NENiX45zyS3S","assets/heroes/houdini/body_normal.webp":"xAqv9m7pA21G","assets/heroes/houdini/body_orm.webp":"hNzC9I8VGb7n","assets/heroes/houdini/head_color.webp":"1T4Cg2aBwChz","assets/heroes/houdini/head_normal.webp":"jNMmhb1E3TDv","assets/heroes/houdini/head_orm.webp":"8M_5KMdLcaVE","assets/heroes/houdini/model.glb":"Tv4eRyjUrCjI","assets/heroes/houdini/opacity_color.webp":"A36SxVCxXu9R","assets/heroes/houdini/portrait.webp":"ukCDIjvLkwN0","assets/heroes/rayne/body_color.webp":"9wQpLuQ9wF3d","assets/heroes/rayne/body_normal.webp":"_8gumXGOUU7_","assets/heroes/rayne/body_orm.webp":"04TqOeO_pxOb","assets/heroes/rayne/head_color.webp":"P7fsTsLozfha","assets/heroes/rayne/head_normal.webp":"9FfaEFqgydEp","assets/heroes/rayne/head_orm.webp":"jteuM6VOERHA","assets/heroes/rayne/model.glb":"YSoy-G5Mc4m8","assets/heroes/rayne/portrait.webp":"sRhY5tmTLJKj","assets/heroes/screws/body_color.webp":"eehNjL7vs2Ot","assets/heroes/screws/body_normal.webp":"DiueZKB_FrQ-","assets/heroes/screws/body_orm.webp":"_chJbZFEHJFz","assets/heroes/screws/head_color.webp":"hw2T5SHdZch_","assets/heroes/screws/head_normal.webp":"CKXY3Co1qej8","assets/heroes/screws/head_orm.webp":"pFKH3d59isE9","assets/heroes/screws/helmet_color.webp":"5hRNrNFSZ-RN","assets/heroes/screws/helmet_normal.webp":"KzlySY6sdsPY","assets/heroes/screws/helmet_orm.webp":"yWkwmusD0j_m","assets/heroes/screws/model.glb":"PQWQhVjIS86U","assets/heroes/screws/opacity_color.webp":"6ywaqXN5GrL5","assets/heroes/screws/opacity_normal.webp":"8DIBVbniUp0R","assets/heroes/screws/opacity_orm.webp":"tfPKVYg3DvVK","assets/heroes/screws/portrait.webp":"CZKzfbOKXsjY","assets/heroes/screws/tools_color.webp":"TIqQ8CxpbXcy","assets/heroes/screws/tools_normal.webp":"aQ9-wedBBJTU","assets/heroes/screws/tools_orm.webp":"D1TVg5vBMwZB","assets/heroes/shadow/body_color.webp":"AAADw4rC3s1j","assets/heroes/shadow/body_normal.webp":"_4ZRAFTRJydG","assets/heroes/shadow/body_orm.webp":"Xp34QpZpzWZa","assets/heroes/shadow/equipment_color.webp":"xqwLoxcZG1sn","assets/heroes/shadow/equipment_normal.webp":"DXk1OYSfwK4c","assets/heroes/shadow/equipment_orm.webp":"EAWjNv--RdXF","assets/heroes/shadow/head_color.webp":"fewwixdycr9E","assets/heroes/shadow/head_normal.webp":"mXqn_dtGQpsa","assets/heroes/shadow/head_orm.webp":"iIm6VDyTM1ss","assets/heroes/shadow/model.glb":"YgnqrGdnmxsI","assets/heroes/shadow/portrait.webp":"A1121IcHRbIu","assets/heroes/specter/body_color.webp":"iYxFFqqVJNeQ","assets/heroes/specter/body_normal.webp":"NVABT8Nqzvsx","assets/heroes/specter/body_orm.webp":"EgJPzz66yIdS","assets/heroes/specter/head_color.webp":"eHz5rv5w_SoV","assets/heroes/specter/head_normal.webp":"rxPA4I41ySac","assets/heroes/specter/head_orm.webp":"bPf-GPxZR94V","assets/heroes/specter/model.glb":"2IGJbRJ5KHSQ","assets/heroes/specter/portrait.webp":"dCow6rlGhn_1","assets/manifest.json":"amQa4JUwQvWt","bundle/e00bd8b8d6/audio.synthWorker.js":"OpvqEgpYUje2","bundle/e00bd8b8d6/main.js":"h1LuTr-mELs6","css/game.css":"5Fm-2dIP1iaA","js/abilities/abilityBase.js":"gIJtRIdMdYd6","js/abilities/abilityController.js":"yzo5fBVra5oY","js/abilities/abilityUtil.js":"TN7Fe-auLkuc","js/abilities/heroes/bear.js":"i6WF_rV2AgoP","js/abilities/heroes/chrono.js":"wQnPzI5olXZG","js/abilities/heroes/elsa.js":"1Nq31UzY812J","js/abilities/heroes/gale.js":"uaefE9r9e1iT","js/abilities/heroes/gouki.js":"0aW8Ty4N1roF","js/abilities/heroes/houdini.js":"faxvfvEmXXjI","js/abilities/heroes/index.js":"vvUhmNyeK6G6","js/abilities/heroes/rayne.js":"H5TjE_oD-Wqi","js/abilities/heroes/screws.js":"abX4xvITZLtd","js/abilities/heroes/shadow.js":"-0Ee1P5_716W","js/abilities/heroes/specter.js":"Iqe1K86oGfXI","js/abilities/roster.js":"_eOGNQZI3c5Y","js/abilities/shared/bearAegisWall.js":"TP_XtDbR0bK0","js/abilities/shared/bearMagneticField.js":"QN-KoEnCK-Gx","js/abilities/shared/chronoMath.js":"lIClHU_bSSIw","js/abilities/shared/chronoTime.js":"y13IVyDhs22o","js/abilities/shared/elsaFrost.js":"7MNlk0BK2qow","js/abilities/shared/elsaFrostMath.js":"8LXKOQp59nau","js/abilities/shared/empowerMath.js":"8JBTzdosawo_","js/abilities/shared/empowerThrow.js":"MEIL2JVl1MVs","js/abilities/shared/houdiniMath.js":"LV3uXtWn3Hl5","js/abilities/shared/rayneMath.js":"kVRi08W_M9zK","js/abilities/shared/raynePayloads.js":"Brw-6qAoDMcA","js/abilities/shared/screwsAutoTurret.js":"Mn3awIk2Rtwb","js/abilities/shared/screwsGadgetKit.js":"GDktmSmztPHm","js/abilities/shared/screwsGadgetMath.js":"tq82wZ1Ip2aW","js/abilities/shared/screwsGluePuddle.js":"eKLukNhLOgSj","js/abilities/shared/shadowClones.js":"-L8aPhYDuyO1","js/abilities/shared/shadowMath.js":"Tq2UCpqsO5st","js/abilities/shared/specterMath.js":"mkQESHXbCXlz","js/abilities/shared/specterThreat.js":"vPb23qUTgZn2","js/ai/aiMath.js":"FB_nl1pxKXzC","js/ai/bot.js":"10PydIJK4Dsu","js/ai/botWorld.js":"7CR3WUhsVOnf","js/ai/difficulty.js":"eyYYv0o8OM-L","js/ai/perception.js":"krT1w5nWh2_3","js/audio/audio.js":"w5MG1lp4oEuV","js/audio/bank.js":"3gH6-jUsulAL","js/audio/dsp.js":"4JMDnd6HBkoy","js/audio/synthWorker.js":"x-_a2G-7SGi4","js/camera/cameraRig.js":"orKmRtl5N56c","js/characters/avatar.js":"e0jhgKqg-x3T","js/characters/avatarMath.js":"_H8TciRZ3ZT7","js/characters/ragdoll.js":"tUJlk6Z_Ne32","js/combat/ball.js":"mGm_vERfcgYa","js/combat/ballTextures.js":"3tb8nOllUIam","js/combat/ballTrail.js":"0hqYEGLj2Ank","js/combat/balls.js":"MJyn50oTuIU4","js/combat/combat.js":"4naTVFA6WHBq","js/combat/sweep.js":"aoHsHBXfqSsD","js/combat/throwMath.js":"opu7w6NlqPUu","js/combat/throwSolver.js":"LGSJ41rMdumR","js/core/constants.js":"1y69doCuiDA7","js/core/events.js":"UqDs4tsEHjK9","js/core/fsm.js":"hwAFNBapI30d","js/core/rng.js":"0UIHitrY11Rw","js/core/rules.js":"6mWlJlAPSzsX","js/core/timers.js":"G0sFfaCdZNa1","js/engine/assets.js":"KCUImwJfwXqF","js/game.js":"BjFr2Fi9zSBJ","js/gameplay/health.js":"TlwV3B1zB5Nc","js/gameplay/history.js":"wQdey7X1SdXh","js/gameplay/motor.js":"7muRYkTAFg0t","js/gameplay/player.js":"-7K8_kI6NCg_","js/gameplay/player_math.js":"CU9caZ50Uc5K","js/gameplay/states.js":"-fAiIrV2EjAb","js/gameplay/status.js":"JW-r66BvRJgP","js/input/input.js":"dg1TzFRvdVOm","js/input/inputMath.js":"MQO111v2Pkrl","js/input/settings.js":"KUFWYzey8Nix","js/input/touch.js":"9jsD2qoUDOul","js/juice/juice.js":"GkrcWP0n8m7R","js/juice/juiceMath.js":"BH3EQO5fU3ZJ","js/juice/noise.js":"clNXHKSsBjzd","js/main.js":"a8HHTpvP8vE1","js/match/match.js":"LNe07B4jJvNV","js/match/outcome.js":"LdzFSkCfh7RG","js/package.json":"DOAOY1x7CY72","js/render/gradeShader.js":"5OpfsEXOFBsI","js/render/pulses.js":"m0UuANeksQPd","js/render/quality.js":"vEBu8SaVNH7R","js/render/renderer.js":"IFNstHuEElye","js/ui/flow.js":"QG0u6xMGZyl_","js/ui/format.js":"aJTEu7Jk_-A3","js/ui/heroSelect.js":"P9Zc43t8QzOI","js/ui/hud.js":"tMnm3NaiBOrP","js/ui/loading.js":"sFr2zAQDaUGv","js/ui/minimap.js":"DN5SKGo1Gk_j","js/ui/pause.js":"DwXDpjFvQHs_","js/vfx/effects.js":"2zbvDlgHxmHf","js/vfx/particleSim.js":"QVx657G0X-MF","js/vfx/particles.js":"hixUsXo3jyQm","js/vfx/shapes.js":"nplZoXMWlzpl","js/vfx/textures.js":"6JrfB0S6qjq_","js/vfx/vfx.js":"YMn4O6P4lMaM","js/world/arena.js":"-pGWpXC52QTM","js/world/court.js":"-F2ZcFXerq9M","js/world/courtMath.js":"RxTEanVuhzfA","js/world/crowd.js":"MxzA6ZpEBA36","js/world/scoreboard.js":"l4doDeD7zH8e","js/world/textures.js":"-E7se2H5X-jn","vendor/VERSIONS.txt":"AwYb-WphYcZw","vendor/cannon-es/LICENSE":"RxTQJy9QyyH-","vendor/cannon-es/cannon-es.js":"8HAMvTpIKVSU","vendor/three/LICENSE":"izeOvmDi_lAB","vendor/three/build/three.core.js":"nt3gArBmqaBW","vendor/three/build/three.module.js":"kFIELWdssP3B","vendor/three/examples/jsm/animation/AnimationClipCreator.js":"CvtMGxR0IOcQ","vendor/three/examples/jsm/animation/CCDIKSolver.js":"Y96ZvDG6m08T","vendor/three/examples/jsm/csm/CSM.js":"rTNk9ekgEIPT","vendor/three/examples/jsm/csm/CSMFrustum.js":"OoblI5IaaRWc","vendor/three/examples/jsm/csm/CSMHelper.js":"d93HHGfSrcM0","vendor/three/examples/jsm/csm/CSMShader.js":"nf6bBS91YHkZ","vendor/three/examples/jsm/csm/CSMShadowNode.js":"_le1uTiyGKBP","vendor/three/examples/jsm/curves/CurveExtras.js":"Typ_5bxZzOpw","vendor/three/examples/jsm/curves/NURBSCurve.js":"vvJgdhind4RV","vendor/three/examples/jsm/curves/NURBSSurface.js":"p_8wOt3XZdqM","vendor/three/examples/jsm/curves/NURBSUtils.js":"xr18QTfVhQmJ","vendor/three/examples/jsm/curves/NURBSVolume.js":"xR22myPbM5Ed","vendor/three/examples/jsm/effects/AnaglyphEffect.js":"X14r8Etd1Bwk","vendor/three/examples/jsm/effects/AsciiEffect.js":"GS_T13s1eZlz","vendor/three/examples/jsm/effects/OutlineEffect.js":"3mPG-5dNuBye","vendor/three/examples/jsm/effects/ParallaxBarrierEffect.js":"qlKoxrFGRUj3","vendor/three/examples/jsm/effects/StereoEffect.js":"pKUQ16jDUTBU","vendor/three/examples/jsm/environments/ColorEnvironment.js":"8XqYJNPerdGZ","vendor/three/examples/jsm/environments/DebugEnvironment.js":"4xHOIzL8kkCH","vendor/three/examples/jsm/environments/RoomEnvironment.js":"VfRmGSzIQph1","vendor/three/examples/jsm/geometries/BoxLineGeometry.js":"X0vZlif4Th_o","vendor/three/examples/jsm/geometries/ConvexGeometry.js":"9S9MXwzELq7y","vendor/three/examples/jsm/geometries/DecalGeometry.js":"ay2m6oQJJt-w","vendor/three/examples/jsm/geometries/LoftGeometry.js":"5JsD3JsFf-S6","vendor/three/examples/jsm/geometries/ParametricFunctions.js":"fVM4Y-3aZRc1","vendor/three/examples/jsm/geometries/ParametricGeometry.js":"8ltp2filp_ji","vendor/three/examples/jsm/geometries/RoundedBoxGeometry.js":"xf6rlhI4WO2I","vendor/three/examples/jsm/geometries/TeapotGeometry.js":"Bd_aTiTbC0ij","vendor/three/examples/jsm/geometries/TextGeometry.js":"7facHNYIfITQ","vendor/three/examples/jsm/helpers/AnimationPathHelper.js":"yIE0HmIx5B-W","vendor/three/examples/jsm/helpers/LightProbeGridHelper.js":"5NGPV9UzzBwK","vendor/three/examples/jsm/helpers/LightProbeGridHelperWebGL.js":"f3t0gIuCbkFS","vendor/three/examples/jsm/helpers/LightProbeHelper.js":"S0cx-eXTULuA","vendor/three/examples/jsm/helpers/LightProbeHelperGPU.js":"RmHQjM8PFM9x","vendor/three/examples/jsm/helpers/OctreeHelper.js":"Mo8pioOtdyCb","vendor/three/examples/jsm/helpers/PositionalAudioHelper.js":"AnUx9yCL6mxj","vendor/three/examples/jsm/helpers/RapierHelper.js":"26SQNrG0AZ5U","vendor/three/examples/jsm/helpers/RectAreaLightHelper.js":"crB5Z7zCQiD8","vendor/three/examples/jsm/helpers/TextureHelper.js":"I2dTeALLNTuy","vendor/three/examples/jsm/helpers/TextureHelperGPU.js":"OaIzEMifkhln","vendor/three/examples/jsm/helpers/VertexNormalsHelper.js":"ZmoSGdlQEOTV","vendor/three/examples/jsm/helpers/VertexTangentsHelper.js":"sNONNq-R649a","vendor/three/examples/jsm/helpers/ViewHelper.js":"NXJoUkPg5TLA","vendor/three/examples/jsm/libs/fflate.module.js":"IJpEEutIzmCe","vendor/three/examples/jsm/libs/meshopt_decoder.module.js":"1CjnOgAAV8bJ","vendor/three/examples/jsm/lights/LightProbeGenerator.js":"5gljDR85nHnc","vendor/three/examples/jsm/lights/RectAreaLightTexturesLib.js":"bdQEO7BSWUNX","vendor/three/examples/jsm/lights/RectAreaLightUniformsLib.js":"SU_vLXMf8WiQ","vendor/three/examples/jsm/lights/SunLight.js":"taxU6BbD6CMc","vendor/three/examples/jsm/lights/SunLightNode.js":"mgZVhbCJHyk-","vendor/three/examples/jsm/lights/SunLightShadow.js":"_T7NHn1CaBNg","vendor/three/examples/jsm/lights/SunShadowNode.js":"Pl5_NHxJ9ZuU","vendor/three/examples/jsm/lines/Line2.js":"ad0hINTfFCCH","vendor/three/examples/jsm/lines/LineGeometry.js":"t-xrABHjsJ3H","vendor/three/examples/jsm/lines/LineMaterial.js":"1FFwAfnXs-iF","vendor/three/examples/jsm/lines/LineSegments2.js":"_Lwg9Xbog0PO","vendor/three/examples/jsm/lines/LineSegmentsGeometry.js":"Rx8KlUoMnFnT","vendor/three/examples/jsm/lines/Wireframe.js":"D4uEIV6Ff4Po","vendor/three/examples/jsm/lines/WireframeGeometry2.js":"tUDKFSs43mGX","vendor/three/examples/jsm/lines/webgpu/Line2.js":"MWnLjcPV24dv","vendor/three/examples/jsm/lines/webgpu/LineSegments2.js":"ufhOksl8I-vW","vendor/three/examples/jsm/lines/webgpu/Wireframe.js":"rirDqqp8wA5o","vendor/three/examples/jsm/loaders/EXRLoader.js":"E1WzKi7hImv_","vendor/three/examples/jsm/loaders/GLTFLoader.js":"ExwPeMAdGTaK","vendor/three/examples/jsm/loaders/HDRLoader.js":"CYa1ePms0Uzs","vendor/three/examples/jsm/loaders/RGBELoader.js":"bAj5xEEECmGh","vendor/three/examples/jsm/math/Capsule.js":"vlTDxYSDp_aw","vendor/three/examples/jsm/math/ColorConverter.js":"MWklq_CfMyq4","vendor/three/examples/jsm/math/ColorSpaces.js":"zDXAHHk80XzN","vendor/three/examples/jsm/math/ConvexHull.js":"e31T7UUzVW9V","vendor/three/examples/jsm/math/ImprovedNoise.js":"04ISQeJb8Mi_","vendor/three/examples/jsm/math/Lut.js":"BNBH2W8XMC8t","vendor/three/examples/jsm/math/MeshSurfaceSampler.js":"pwq3qPKLls1R","vendor/three/examples/jsm/math/OBB.js":"Mpfbeb3wozUl","vendor/three/examples/jsm/math/Octree.js":"svODCsNAk4dx","vendor/three/examples/jsm/math/SimplexNoise.js":"SxRVYXE15rR3","vendor/three/examples/jsm/misc/ConvexObjectBreaker.js":"LvLl3TLaTxr2","vendor/three/examples/jsm/misc/GPUComputationRenderer.js":"GlgK3YOY6Wmm","vendor/three/examples/jsm/misc/Gyroscope.js":"KLn9SjTzaYYM","vendor/three/examples/jsm/misc/MD2Character.js":"T5XTS4hSQTIz","vendor/three/examples/jsm/misc/MD2CharacterComplex.js":"HOXwHurXPP-9","vendor/three/examples/jsm/misc/MorphAnimMesh.js":"-uUWkkfLJkzX","vendor/three/examples/jsm/misc/MorphBlendMesh.js":"b8lXIO00GUMo","vendor/three/examples/jsm/misc/ProgressiveLightMap.js":"PH8MDCAv6K8K","vendor/three/examples/jsm/misc/ProgressiveLightMapGPU.js":"BLJauaBRY-Nf","vendor/three/examples/jsm/misc/RollerCoaster.js":"iDt_Qr5rQoGF","vendor/three/examples/jsm/misc/SculptGL.LICENSE.txt":"cLdEY_0J0TMr","vendor/three/examples/jsm/misc/Sculptor.js":"CAFi_ZOBm0Lx","vendor/three/examples/jsm/misc/SculptorMesh.js":"0zCqV23KZWbX","vendor/three/examples/jsm/misc/SculptorTools.js":"44jntbnz5QSy","vendor/three/examples/jsm/misc/SculptorUtils.js":"W2JDhul0_NtP","vendor/three/examples/jsm/misc/TileCreasedNormalsPlugin.js":"JUU-kCWWQSkj","vendor/three/examples/jsm/misc/TubePainter.js":"RWeIZuabrodb","vendor/three/examples/jsm/misc/Volume.js":"btzF36ayo3f9","vendor/three/examples/jsm/misc/VolumeSlice.js":"JJDZA-TDDLhK","vendor/three/examples/jsm/modifiers/CurveModifier.js":"yKdzudeCLokV","vendor/three/examples/jsm/modifiers/CurveModifierGPU.js":"Uf0U5CPvrrQz","vendor/three/examples/jsm/modifiers/EdgeSplitModifier.js":"0KH73147tj5Y","vendor/three/examples/jsm/modifiers/SimplifyModifier.js":"0eFIJXZmfg_n","vendor/three/examples/jsm/modifiers/TessellateModifier.js":"pz6uP_ZWKz0l","vendor/three/examples/jsm/objects/GaussianSplat.js":"6j8UjTlBPvMX","vendor/three/examples/jsm/objects/GroundedSkybox.js":"huEwUeBctb7t","vendor/three/examples/jsm/objects/Lensflare.js":"8xPH14PdJbKR","vendor/three/examples/jsm/objects/LensflareMesh.js":"PldnuaEeSunU","vendor/three/examples/jsm/objects/MarchingCubes.js":"_pxqziwHm_VA","vendor/three/examples/jsm/objects/Reflector.js":"V4Nt-XbwKkdL","vendor/three/examples/jsm/objects/ReflectorForSSRPass.js":"MZjdD5ZneYN6","vendor/three/examples/jsm/objects/Refractor.js":"USxOwFa4rzx6","vendor/three/examples/jsm/objects/ShadowMesh.js":"4ow0QKPf2Td6","vendor/three/examples/jsm/objects/Sky.js":"NBIbYKO9mtwA","vendor/three/examples/jsm/objects/SkyMesh.js":"RTIVACMp-oqE","vendor/three/examples/jsm/objects/Water.js":"M4ZnC6hgA6Mv","vendor/three/examples/jsm/objects/Water2.js":"gBgM4LAxFpiD","vendor/three/examples/jsm/objects/Water2Mesh.js":"xC3st58qc9jN","vendor/three/examples/jsm/objects/WaterMesh.js":"4vXQpQrACxwY","vendor/three/examples/jsm/postprocessing/AfterimagePass.js":"jMDIckpnSmSd","vendor/three/examples/jsm/postprocessing/BloomPass.js":"XcwAXoeCADEk","vendor/three/examples/jsm/postprocessing/BokehPass.js":"W5sLxc7m4ksk","vendor/three/examples/jsm/postprocessing/ClearPass.js":"acUl6OO-KJmU","vendor/three/examples/jsm/postprocessing/CubeTexturePass.js":"PMM5FRrY3rac","vendor/three/examples/jsm/postprocessing/DotScreenPass.js":"74_IH2aXp2q_","vendor/three/examples/jsm/postprocessing/EffectComposer.js":"TgeaWIYVLX5S","vendor/three/examples/jsm/postprocessing/FXAAPass.js":"qk6Hoo9X5VCE","vendor/three/examples/jsm/postprocessing/FilmPass.js":"cJpuZAtc7SRJ","vendor/three/examples/jsm/postprocessing/GTAOPass.js":"onf4SNEDXHUc","vendor/three/examples/jsm/postprocessing/GlitchPass.js":"gynQZWzlldxw","vendor/three/examples/jsm/postprocessing/HalftonePass.js":"r3sam4KZLBlN","vendor/three/examples/jsm/postprocessing/LUTPass.js":"rvZNMeJSjSW8","vendor/three/examples/jsm/postprocessing/MaskPass.js":"fNCO7p1db1V4","vendor/three/examples/jsm/postprocessing/OutlinePass.js":"jrSGsUNkMs9v","vendor/three/examples/jsm/postprocessing/OutputPass.js":"AuSiYa803nEz","vendor/three/examples/jsm/postprocessing/Pass.js":"REtAnCNerZho","vendor/three/examples/jsm/postprocessing/RenderPass.js":"gX9sPNzQ_UFR","vendor/three/examples/jsm/postprocessing/RenderPixelatedPass.js":"faDAIiUaI_7M","vendor/three/examples/jsm/postprocessing/RenderTransitionPass.js":"IVxxFX-VllqC","vendor/three/examples/jsm/postprocessing/SAOPass.js":"_Gb4Or6ybWts","vendor/three/examples/jsm/postprocessing/SMAAPass.js":"GpaAS4yzO6dq","vendor/three/examples/jsm/postprocessing/SSAARenderPass.js":"zSuOg7MugGCR","vendor/three/examples/jsm/postprocessing/SSAOPass.js":"L5wCpT4JkX0D","vendor/three/examples/jsm/postprocessing/SSRPass.js":"fQI4a35U_SRe","vendor/three/examples/jsm/postprocessing/SavePass.js":"QI8jdc4JeXSZ","vendor/three/examples/jsm/postprocessing/ShaderPass.js":"4lAKWROya79R","vendor/three/examples/jsm/postprocessing/TAARenderPass.js":"Rj29pdHw9AGz","vendor/three/examples/jsm/postprocessing/TexturePass.js":"QCz83ajmnbXw","vendor/three/examples/jsm/postprocessing/UnrealBloomPass.js":"uo8vyt-mWIOE","vendor/three/examples/jsm/shaders/ACESFilmicToneMappingShader.js":"R7IRTIt25-Gw","vendor/three/examples/jsm/shaders/AfterimageShader.js":"-4Qq5lc3mm7j","vendor/three/examples/jsm/shaders/BasicShader.js":"3zZeSRRx5QbT","vendor/three/examples/jsm/shaders/BleachBypassShader.js":"CTCeEUZbSI_t","vendor/three/examples/jsm/shaders/BlendShader.js":"akN6vzrDFP7x","vendor/three/examples/jsm/shaders/BokehShader.js":"vYUlFY41dKgD","vendor/three/examples/jsm/shaders/BokehShader2.js":"KqKe6-R7_3-1","vendor/three/examples/jsm/shaders/BrightnessContrastShader.js":"nHLYbjnJQ20p","vendor/three/examples/jsm/shaders/ColorCorrectionShader.js":"9A7nINPL_YUm","vendor/three/examples/jsm/shaders/ColorifyShader.js":"c5k6NPK1A2xh","vendor/three/examples/jsm/shaders/ConvolutionShader.js":"utce0CNRrLcB","vendor/three/examples/jsm/shaders/CopyShader.js":"ozBX1ayRxDME","vendor/three/examples/jsm/shaders/DOFMipMapShader.js":"g1yufTxErJsy","vendor/three/examples/jsm/shaders/DepthLimitedBlurShader.js":"wVlHJCEvaEL7","vendor/three/examples/jsm/shaders/DigitalGlitch.js":"hzubORFP8XTK","vendor/three/examples/jsm/shaders/DotScreenShader.js":"lZouDqPUqJ1n","vendor/three/examples/jsm/shaders/ExposureShader.js":"gqYR47WZIvdd","vendor/three/examples/jsm/shaders/FXAAShader.js":"uDA9F5-dXUCg","vendor/three/examples/jsm/shaders/FilmShader.js":"dExhnHD5Sa9d","vendor/three/examples/jsm/shaders/FocusShader.js":"IXFXDMwOzzl3","vendor/three/examples/jsm/shaders/FreiChenShader.js":"WxfWBNyYYfik","vendor/three/examples/jsm/shaders/GTAOShader.js":"zep75gRS9hEF","vendor/three/examples/jsm/shaders/GammaCorrectionShader.js":"5zyaslsCYsl4","vendor/three/examples/jsm/shaders/HalftoneShader.js":"w8dRc_sLU5x9","vendor/three/examples/jsm/shaders/HorizontalBlurShader.js":"oVVxXKO_5P0f","vendor/three/examples/jsm/shaders/HorizontalTiltShiftShader.js":"dy8IZelcTs1-","vendor/three/examples/jsm/shaders/HueSaturationShader.js":"Wy8e6c24R8b4","vendor/three/examples/jsm/shaders/KaleidoShader.js":"38FDHi5rMoxy","vendor/three/examples/jsm/shaders/LuminosityHighPassShader.js":"UET3gLbmz4Y5","vendor/three/examples/jsm/shaders/LuminosityShader.js":"MvFktysVkKm-","vendor/three/examples/jsm/shaders/MirrorShader.js":"TYACyASzTRNa","vendor/three/examples/jsm/shaders/NormalMapShader.js":"Zic94YK-FZRx","vendor/three/examples/jsm/shaders/OutputShader.js":"NTR593qNfiYp","vendor/three/examples/jsm/shaders/PoissonDenoiseShader.js":"YdoHSprYshOd","vendor/three/examples/jsm/shaders/RGBShiftShader.js":"aeeHL0cZUzAT","vendor/three/examples/jsm/shaders/SAOShader.js":"ykPkqH2y2Fb_","vendor/three/examples/jsm/shaders/SMAAShader.js":"SM4yCmv1wzgG","vendor/three/examples/jsm/shaders/SSAOShader.js":"A02tmJJ-2PJn","vendor/three/examples/jsm/shaders/SSRShader.js":"Wdz4e5CvUZze","vendor/three/examples/jsm/shaders/SepiaShader.js":"t5lPuY5vN78W","vendor/three/examples/jsm/shaders/SobelOperatorShader.js":"VTq9B7ibZO6s","vendor/three/examples/jsm/shaders/SubsurfaceScatteringShader.js":"YNFLZMtG39Uc","vendor/three/examples/jsm/shaders/TechnicolorShader.js":"dgaWOQgqOIad","vendor/three/examples/jsm/shaders/ToonShader.js":"ZQzwDnMJ6HIS","vendor/three/examples/jsm/shaders/TriangleBlurShader.js":"wM5pm_HVoQpa","vendor/three/examples/jsm/shaders/UnpackDepthRGBAShader.js":"kwcwGj1ejEs6","vendor/three/examples/jsm/shaders/VelocityShader.js":"lRHEO9hYcn-C","vendor/three/examples/jsm/shaders/VerticalBlurShader.js":"Z3kqvfCLojBf","vendor/three/examples/jsm/shaders/VerticalTiltShiftShader.js":"lr7T-v3qg3DR","vendor/three/examples/jsm/shaders/VignetteShader.js":"WxIpkO-rVao5","vendor/three/examples/jsm/shaders/VolumeShader.js":"sb0VlHzPi1xf","vendor/three/examples/jsm/shaders/WaterRefractionShader.js":"ytZ-1HnREteH","vendor/three/examples/jsm/utils/BufferGeometryUtils.js":"n7Y0J85mQfoU","vendor/three/examples/jsm/utils/CameraUtils.js":"nT_Y49vJT1Ck","vendor/three/examples/jsm/utils/ColorUtils.js":"AWyVVvtIbLjd","vendor/three/examples/jsm/utils/GaussianSplatUtils.js":"S5sBAUmz4adn","vendor/three/examples/jsm/utils/GeometryCompressionUtils.js":"C7FjBaDtdaWC","vendor/three/examples/jsm/utils/GeometryUtils.js":"ZJ8xXZSPFr8y","vendor/three/examples/jsm/utils/LDrawUtils.js":"qvihGZTC46Lk","vendor/three/examples/jsm/utils/SceneOptimizer.js":"nKLodcrdSK9G","vendor/three/examples/jsm/utils/SceneUtils.js":"0tFnPYDNuRkj","vendor/three/examples/jsm/utils/ShadowMapViewer.js":"2ArT0X7Nx90u","vendor/three/examples/jsm/utils/ShadowMapViewerGPU.js":"jMi0YsAF6VOe","vendor/three/examples/jsm/utils/SkeletonUtils.js":"sWMqcDIGw9gw","vendor/three/examples/jsm/utils/SortUtils.js":"ppASrQK329_K","vendor/three/examples/jsm/utils/UVsDebug.js":"Z4abXDL4_Ecp","vendor/three/examples/jsm/utils/WebGLTextureUtils.js":"hDRQjqVJbklY","vendor/three/examples/jsm/utils/WebGPUTextureUtils.js":"3cuAUdyUblMW","vendor/three/examples/jsm/utils/WorkerPool.js":"WscJX9VmvJrk"};
/** @type {string[]} paths fetched on install */
const SHELL = ["bundle/e00bd8b8d6/main.js","bundle/e00bd8b8d6/audio.synthWorker.js","css/game.css","assets/manifest.json","assets/heroes/bear/portrait.webp","assets/heroes/chrono/portrait.webp","assets/heroes/elsa/portrait.webp","assets/heroes/gale/portrait.webp","assets/heroes/gouki/portrait.webp","assets/heroes/houdini/portrait.webp","assets/heroes/rayne/portrait.webp","assets/heroes/screws/portrait.webp","assets/heroes/shadow/portrait.webp","assets/heroes/specter/portrait.webp"];

const DEV = VERSION.startsWith('__');
const PREFIX = 'du-';
const CACHE = PREFIX + (DEV ? 'dev' : VERSION);
/** Pseudo entry in each cache holding that deploy's HASHES (lets the next deploy reuse unchanged files). */
const META = '__du_hashes.json';
const SCOPE = new URL(self.registration.scope);

/** @param {string} rel @returns {string} absolute cache key (no query string) */
const keyOf = (rel) => new URL(rel, SCOPE).href;

/** @param {URL} url @returns {string|null} path relative to the scope ('' -> 'index.html'), null outside it */
function relOf(url) {
  if (url.origin !== SCOPE.origin || !url.pathname.startsWith(SCOPE.pathname)) return null;
  const rel = decodeURIComponent(url.pathname.slice(SCOPE.pathname.length));
  return rel === '' || rel.endsWith('/') ? rel + 'index.html' : rel;
}

/** Fetch that revalidates with the server (skips a possibly stale HTTP cache entry; a 304 is cheap). */
const fresh = (rel) => fetch(keyOf(rel), { cache: 'no-cache', credentials: 'same-origin' });

/**
 * Client ids of pages running another deploy's build (see header). Mirrored into this deploy's cache (FOREIGN entry)
 * because the browser may stop and restart an idle worker while such a page is still open.
 */
const foreign = new Set();
const FOREIGN = '__du_foreign.json';
const FOREIGN_MAX = 16;
let foreignLoaded = null;
/** @returns {Promise<void>} resolves once the persisted foreign ids are in `foreign` (read once per worker start) */
function loadForeign() {
  if (!foreignLoaded) {
    foreignLoaded = caches.open(CACHE).then((c) => c.match(keyOf(FOREIGN)))
      .then((r) => (r ? r.json() : []))
      .then((ids) => { for (const id of ids) foreign.add(id); }, () => undefined);
  }
  return foreignLoaded;
}
/** @param {string} id @param {boolean} on */
async function markForeign(id, on) {
  await loadForeign();
  if (on === foreign.has(id)) return;
  if (on) foreign.add(id); else foreign.delete(id);
  const ids = [...foreign].slice(-FOREIGN_MAX);
  const c = await caches.open(CACHE);
  await c.put(keyOf(FOREIGN), new Response(JSON.stringify(ids), { headers: { 'content-type': 'application/json' } }));
}
/** @param {string} html @returns {boolean} true when the page is stamped with a build other than this worker's */
const otherBuild = (html) => {
  const m = /data-du-build="([^"]*)"/.exec(html);
  return !!m && m[1] !== VERSION;
};

self.addEventListener('message', (event) => {
  if (event.data === 'du:version' && event.ports && event.ports[0]) event.ports[0].postMessage(DEV ? 'dev' : VERSION);
});

self.addEventListener('install', (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE);
    if (!DEV) {
      await cache.put(keyOf(META), new Response(JSON.stringify(HASHES), { headers: { 'content-type': 'application/json' } }));
      await carryOver(cache).catch(() => undefined);
      // Precache the shell (bundle, css, manifest, portraits). Failures are ignored: a missing entry is fetched on use.
      await Promise.all(SHELL.map(async (rel) => {
        if (await cache.match(keyOf(rel))) return;
        try {
          const res = await fresh(rel);
          if (res.ok) await cache.put(keyOf(rel), res);
        } catch (e) { /* offline or 404: fetched on first use instead */ }
      }));
    }
    await self.skipWaiting();
  })());
});

/** Copies unchanged files (same content hash) from older du-* caches into the new one. @param {Cache} cache */
async function carryOver(cache) {
  for (const name of await caches.keys()) {
    if (!name.startsWith(PREFIX) || name === CACHE) continue;
    const old = await caches.open(name);
    const metaRes = await old.match(keyOf(META));
    if (!metaRes) continue;
    const oldHashes = await metaRes.json();
    for (const req of await old.keys()) {
      const rel = relOf(new URL(req.url));
      if (!rel || rel === META || !HASHES[rel] || oldHashes[rel] !== HASHES[rel]) continue;
      if (await cache.match(req)) continue;
      const res = await old.match(req);
      if (res) await cache.put(req, res);
    }
  }
}

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name.startsWith(PREFIX) && name !== CACHE) await caches.delete(name);
    }
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET' || req.headers.has('range')) return;
  const url = new URL(req.url);
  const rel = relOf(url);
  if (rel === null) return; // other origins: browser default
  if (!DEV && req.mode === 'navigate') {
    event.respondWith(navigate(req, rel, event.resultingClientId));
    return;
  }
  if (rel === 'sw.js') return;
  event.respondWith(route(req, rel, event.clientId));
});

/**
 * Subresources: another build's page goes to the network (revalidated, nothing stored), hashed files are cache-first,
 * everything else network-first.
 * @param {Request} req @param {string} rel @param {string} [clientId] @returns {Promise<Response>}
 */
async function route(req, rel, clientId) {
  if (!DEV && clientId) {
    await loadForeign();
    if (foreign.has(clientId)) return fetch(req, { cache: 'no-cache' });
  }
  if (!DEV && Object.prototype.hasOwnProperty.call(HASHES, rel)) return cacheFirst(rel);
  return networkFirst(req, rel);
}

/** @param {string} rel @returns {Promise<Response>} */
async function cacheFirst(rel) {
  const cache = await caches.open(CACHE);
  const hit = await cache.match(keyOf(rel));
  if (hit) return hit;
  const res = await fresh(rel);
  if (res.ok && res.status === 200) cache.put(keyOf(rel), res.clone()).catch(() => undefined);
  return res;
}

/**
 * Navigation: network-first, and a page stamped with another build is marked foreign (see header).
 * @param {Request} req @param {string} rel @param {string} [clientId] resultingClientId of the navigation
 * @returns {Promise<Response>}
 */
async function navigate(req, rel, clientId) {
  let res;
  try {
    res = await fetch(req.url, { cache: 'no-cache', credentials: 'same-origin' });
  } catch (e) {
    const hit = await (await caches.open(CACHE)).match(keyOf(rel));
    if (hit) return hit;
    throw e;
  }
  if (res.redirected) return Response.redirect(res.url, 302);
  if (!(res.ok && res.status === 200 && res.type === 'basic')) return res;
  let other = false;
  if ((res.headers.get('content-type') || '').includes('text/html')) {
    try { other = otherBuild(await res.clone().text()); } catch (e) { /* unreadable body: treat as this build */ }
  }
  if (clientId) await markForeign(clientId, other).catch(() => undefined);
  // Another build's page is never stored here: the offline fallback must name files this cache holds.
  if (!other) (await caches.open(CACHE)).put(keyOf(rel), res.clone()).catch(() => undefined);
  return res;
}

/** @param {Request} req @param {string} rel @returns {Promise<Response>} */
async function networkFirst(req, rel) {
  const cache = await caches.open(CACHE);
  try {
    // Always revalidate: a page from the HTTP cache (GitHub Pages: max-age=600) could name a bundle that is gone.
    const res = req.mode === 'navigate' ? await fetch(req.url, { cache: 'no-cache', credentials: 'same-origin' }) : await fetch(req, { cache: 'no-cache' });
    if (res.redirected && req.mode === 'navigate') return Response.redirect(res.url, 302);
    if (res.ok && res.status === 200 && res.type === 'basic') cache.put(keyOf(rel), res.clone()).catch(() => undefined);
    return res;
  } catch (e) {
    const hit = await cache.match(keyOf(rel));
    if (hit) return hit;
    throw e;
  }
}
