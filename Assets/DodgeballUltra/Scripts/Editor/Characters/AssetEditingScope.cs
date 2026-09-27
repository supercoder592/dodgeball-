using System;
using UnityEditor;

namespace DodgeballUltra.Editor.Characters
{
    /// <summary>
    /// <c>using (new AssetEditingScope()) { ... }</c> - batches imports between <see cref="AssetDatabase.StartAssetEditing"/>
    /// and <see cref="AssetDatabase.StopAssetEditing"/> and guarantees the Stop call even when the body throws (an
    /// unbalanced Start leaves the AssetDatabase paused until the editor restarts).
    /// <para>
    /// Inside the scope, <c>ImportAsset</c> / <c>SaveAndReimport</c> are queued; everything is imported when the scope ends,
    /// so code must not read the result of an import it queued in the same scope.
    /// </para>
    /// </summary>
    public sealed class AssetEditingScope : IDisposable
    {
        private bool _disposed;

        public AssetEditingScope() => AssetDatabase.StartAssetEditing();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AssetDatabase.StopAssetEditing();
        }
    }
}
