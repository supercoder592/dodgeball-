using UnityEngine;

namespace DodgeballUltra
{
    /// <summary>
    /// Physics layers used by the game. Indices are fixed so the game works even if ProjectSettings/TagManager.asset
    /// is regenerated (unnamed layers are still valid). Names match the shipped TagManager.asset.
    /// </summary>
    public static class GameLayers
    {
        public const int Player = 8;          // character capsules (DU_Player)
        public const int Ball = 9;            // dodgeballs (DU_Ball)
        public const int Hittable = 10;       // non-player things balls can hit: clones, turrets, shields (DU_Hittable)
        public const int Court = 11;          // floor, walls, bleachers (DU_Court)
        public const int PlayerBlocker = 12;  // invisible walls that stop players but not balls (centre line, outfield fences)
        public const int Ragdoll = 13;        // ragdoll limb colliders (DU_Ragdoll)
        public const int AbilityVolume = 14;  // trigger volumes: glue puddles, ice trails, magnetic fields
        public const int Visual = 15;         // purely visual colliders-free objects (DU_Visual)

        public static int PlayerMask => 1 << Player;
        public static int BallMask => 1 << Ball;
        public static int HittableMask => 1 << Hittable;
        public static int CourtMask => 1 << Court;
        public static int PlayerBlockerMask => 1 << PlayerBlocker;
        public static int RagdollMask => 1 << Ragdoll;
        public static int AbilityVolumeMask => 1 << AbilityVolume;

        /// <summary>Everything a live ball sweep should consider (players, hittables, court geometry, Default layer props).</summary>
        public static int BallSweepMask => PlayerMask | HittableMask | CourtMask | (1 << 0);

        /// <summary>Surfaces a player can stand on.</summary>
        public static int GroundMask => CourtMask | (1 << 0);

        private static bool s_configured;

        /// <summary>
        /// Configures the layer collision matrix at runtime (idempotent). Called by GameBootstrap before anything spawns.
        /// Balls never physically collide with player capsules: hits/catches are resolved by the ball's own sweep so that
        /// 220 km/h balls cannot tunnel and so catch timing is deterministic.
        /// </summary>
        public static void ConfigureCollisionMatrix()
        {
            if (s_configured) return;
            s_configured = true;

            Physics.IgnoreLayerCollision(Ball, Player, true);
            Physics.IgnoreLayerCollision(Ball, PlayerBlocker, true);
            Physics.IgnoreLayerCollision(Ball, Ragdoll, true);
            Physics.IgnoreLayerCollision(Ball, AbilityVolume, true);
            Physics.IgnoreLayerCollision(Ball, Hittable, true);

            Physics.IgnoreLayerCollision(Ragdoll, Player, true);
            Physics.IgnoreLayerCollision(Ragdoll, PlayerBlocker, true);
            Physics.IgnoreLayerCollision(Ragdoll, AbilityVolume, true);
            Physics.IgnoreLayerCollision(Ragdoll, Hittable, true);

            Physics.IgnoreLayerCollision(AbilityVolume, Court, true);
            Physics.IgnoreLayerCollision(AbilityVolume, PlayerBlocker, true);
            Physics.IgnoreLayerCollision(AbilityVolume, AbilityVolume, true);
            Physics.IgnoreLayerCollision(Hittable, Player, true);
            Physics.IgnoreLayerCollision(Hittable, Hittable, true);

            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(Visual, i, true);
        }

        /// <summary>Sets the layer on a GameObject and all its children.</summary>
        public static void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursively(t.GetChild(i).gameObject, layer);
        }
    }
}
