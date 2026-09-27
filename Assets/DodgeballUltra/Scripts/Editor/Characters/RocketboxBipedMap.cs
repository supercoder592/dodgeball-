using System;
using System.Collections.Generic;
using UnityEngine;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Explicit 3ds Max Biped -> Mecanim Humanoid bone map for Microsoft Rocketbox rigs (no auto-mapping guesswork).
    /// Biped names are given without their root prefix (<c>"Bip01 "</c>); <see cref="Resolve"/> detects the prefix from the
    /// pelvis so <c>Bip001</c> exports work too.
    /// <para>
    /// Biped peculiarities handled by the rest of the pipeline: the thighs are children of <c>Spine</c> (not the pelvis),
    /// the bind pose is an A-pose, and each bone points along its local X axis.
    /// </para>
    /// </summary>
    public static class RocketboxBipedMap
    {
        /// <summary>(Humanoid bone, Biped bone name without prefix). Spine links are resolved separately (variable count).</summary>
        public static readonly KeyValuePair<HumanBodyBones, string>[] Bones =
        {
            Pair(HumanBodyBones.Hips, "Pelvis"),
            Pair(HumanBodyBones.Neck, "Neck"),
            Pair(HumanBodyBones.Head, "Head"),
            Pair(HumanBodyBones.Jaw, "MJaw"),
            Pair(HumanBodyBones.LeftEye, "LEye"),
            Pair(HumanBodyBones.RightEye, "REye"),

            Pair(HumanBodyBones.LeftShoulder, "L Clavicle"),
            Pair(HumanBodyBones.LeftUpperArm, "L UpperArm"),
            Pair(HumanBodyBones.LeftLowerArm, "L Forearm"),
            Pair(HumanBodyBones.LeftHand, "L Hand"),
            Pair(HumanBodyBones.RightShoulder, "R Clavicle"),
            Pair(HumanBodyBones.RightUpperArm, "R UpperArm"),
            Pair(HumanBodyBones.RightLowerArm, "R Forearm"),
            Pair(HumanBodyBones.RightHand, "R Hand"),

            Pair(HumanBodyBones.LeftUpperLeg, "L Thigh"),
            Pair(HumanBodyBones.LeftLowerLeg, "L Calf"),
            Pair(HumanBodyBones.LeftFoot, "L Foot"),
            Pair(HumanBodyBones.LeftToes, "L Toe0"),
            Pair(HumanBodyBones.RightUpperLeg, "R Thigh"),
            Pair(HumanBodyBones.RightLowerLeg, "R Calf"),
            Pair(HumanBodyBones.RightFoot, "R Foot"),
            Pair(HumanBodyBones.RightToes, "R Toe0"),

            // Fingers: Biped FingerN = digit N (0 thumb .. 4 little), FingerN1 / FingerN2 = further phalanges.
            Pair(HumanBodyBones.LeftThumbProximal, "L Finger0"),
            Pair(HumanBodyBones.LeftThumbIntermediate, "L Finger01"),
            Pair(HumanBodyBones.LeftThumbDistal, "L Finger02"),
            Pair(HumanBodyBones.LeftIndexProximal, "L Finger1"),
            Pair(HumanBodyBones.LeftIndexIntermediate, "L Finger11"),
            Pair(HumanBodyBones.LeftIndexDistal, "L Finger12"),
            Pair(HumanBodyBones.LeftMiddleProximal, "L Finger2"),
            Pair(HumanBodyBones.LeftMiddleIntermediate, "L Finger21"),
            Pair(HumanBodyBones.LeftMiddleDistal, "L Finger22"),
            Pair(HumanBodyBones.LeftRingProximal, "L Finger3"),
            Pair(HumanBodyBones.LeftRingIntermediate, "L Finger31"),
            Pair(HumanBodyBones.LeftRingDistal, "L Finger32"),
            Pair(HumanBodyBones.LeftLittleProximal, "L Finger4"),
            Pair(HumanBodyBones.LeftLittleIntermediate, "L Finger41"),
            Pair(HumanBodyBones.LeftLittleDistal, "L Finger42"),

            Pair(HumanBodyBones.RightThumbProximal, "R Finger0"),
            Pair(HumanBodyBones.RightThumbIntermediate, "R Finger01"),
            Pair(HumanBodyBones.RightThumbDistal, "R Finger02"),
            Pair(HumanBodyBones.RightIndexProximal, "R Finger1"),
            Pair(HumanBodyBones.RightIndexIntermediate, "R Finger11"),
            Pair(HumanBodyBones.RightIndexDistal, "R Finger12"),
            Pair(HumanBodyBones.RightMiddleProximal, "R Finger2"),
            Pair(HumanBodyBones.RightMiddleIntermediate, "R Finger21"),
            Pair(HumanBodyBones.RightMiddleDistal, "R Finger22"),
            Pair(HumanBodyBones.RightRingProximal, "R Finger3"),
            Pair(HumanBodyBones.RightRingIntermediate, "R Finger31"),
            Pair(HumanBodyBones.RightRingDistal, "R Finger32"),
            Pair(HumanBodyBones.RightLittleProximal, "R Finger4"),
            Pair(HumanBodyBones.RightLittleIntermediate, "R Finger41"),
            Pair(HumanBodyBones.RightLittleDistal, "R Finger42"),
        };

        /// <summary>Biped spine links in order (a rig has 1 to 4 of them; Rocketbox uses 3).</summary>
        public static readonly string[] SpineLinks = { "Spine", "Spine1", "Spine2", "Spine3" };

        /// <summary>
        /// Resolves the map against a hierarchy. Returns Humanoid bone -> transform for every bone found. The spine chain is
        /// assigned as Spine (first link), Chest (second) and UpperChest (last, when there are three or more links).
        /// </summary>
        public static Dictionary<HumanBodyBones, Transform> Resolve(Transform root, out string bipedPrefix)
        {
            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName.Add(t.name, t);

            bipedPrefix = DetectPrefix(byName.Keys);
            var result = new Dictionary<HumanBodyBones, Transform>();
            if (bipedPrefix == null) return result;

            foreach (KeyValuePair<HumanBodyBones, string> kv in Bones)
                if (byName.TryGetValue(bipedPrefix + kv.Value, out Transform t)) result[kv.Key] = t;

            var spine = new List<Transform>(4);
            foreach (string link in SpineLinks)
                if (byName.TryGetValue(bipedPrefix + link, out Transform t)) spine.Add(t);
            if (spine.Count > 0) result[HumanBodyBones.Spine] = spine[0];
            if (spine.Count > 1) result[HumanBodyBones.Chest] = spine[1];
            if (spine.Count > 2) result[HumanBodyBones.UpperChest] = spine[spine.Count - 1];
            return result;
        }

        /// <summary>The Biped root prefix ("Bip01 ") found from a "... Pelvis" transform, or null when this is no Biped rig.</summary>
        public static string DetectPrefix(IEnumerable<string> names)
        {
            foreach (string n in names)
            {
                if (n.EndsWith(" Pelvis", StringComparison.Ordinal) && n.StartsWith("Bip", StringComparison.OrdinalIgnoreCase))
                    return n.Substring(0, n.Length - "Pelvis".Length);
            }
            return null;
        }

        /// <summary>Mecanim name of a Humanoid bone (<see cref="HumanTrait.BoneName"/> is indexed by <see cref="HumanBodyBones"/>).</summary>
        public static string HumanName(HumanBodyBones bone) => HumanTrait.BoneName[(int)bone];

        private static KeyValuePair<HumanBodyBones, string> Pair(HumanBodyBones b, string n) => new KeyValuePair<HumanBodyBones, string>(b, n);
    }
}
