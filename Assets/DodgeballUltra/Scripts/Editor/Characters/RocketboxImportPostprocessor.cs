using UnityEditor;
using UnityEngine;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// Import rules for everything under <c>Assets/ThirdParty/Rocketbox/</c> and the textures generated for it.
    /// <list type="bullet">
    /// <item><b>Textures</b> - <see cref="CharacterTextureRules"/>: *_normal -> Normal Map, *_specular / generated masks ->
    /// linear, colour -> sRGB, hair opacity -> sRGB + alpha with coverage-preserving mips, portrait -> Sprite, max 2048.</item>
    /// <item><b>Models / clips</b> - <see cref="HumanoidAvatarBuilder.ApplyBaseModelSettings"/>: no blend shapes, no mesh
    /// compression, transforms kept (IK sockets, ragdoll), MikkTSpace tangents. The first import of a file is Generic;
    /// once imported, <see cref="HumanoidAvatarBuilder"/> switches it to Humanoid with an explicit Biped bone map and an
    /// enforced T-pose (a userData marker guards against re-import loops).</item>
    /// </list>
    /// </summary>
    internal sealed class RocketboxImportPostprocessor : AssetPostprocessor
    {
        /// <summary>Bump when the rules change so Unity re-imports the affected assets.</summary>
        public override uint GetVersion() => 1;

        // Run after Unity's / HDRP's own material-description preprocessors.
        public override int GetPostprocessOrder() => 100;

        private void OnPreprocessTexture()
        {
            if (!CharacterTextureRules.IsManaged(assetPath)) return;
            if (!(assetImporter is TextureImporter importer)) return;
            CharacterTextureRules.Apply(importer, assetPath, importer.importSettingsMissing);
        }

        private void OnPreprocessModel()
        {
            if (!RocketboxAssetSet.IsRocketboxPath(assetPath)) return;
            if (!(assetImporter is ModelImporter importer)) return;
            HumanoidAvatarBuilder.ApplyBaseModelSettings(importer, assetPath);
        }

        private void OnPostprocessModel(GameObject root)
        {
            if (!RocketboxAssetSet.IsRocketboxPath(assetPath)) return;
            if (!(assetImporter is ModelImporter importer)) return;
            HumanoidAvatarBuilder.NotifyImported(importer, assetPath);
        }
    }
}
