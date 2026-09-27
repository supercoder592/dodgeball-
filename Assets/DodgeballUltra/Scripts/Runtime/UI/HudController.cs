using System;
using UnityEngine;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// CONTRACT (kernel) - the in-game HUD, built entirely in code with uGUI (no prefabs): HP bar, ultimate meter,
    /// skill/ultimate cooldowns, throw charge bar, catch timing feedback (PERFECT!), crosshair and target lock marker,
    /// round timer + score + infield pips, kill feed, centre banners, Danger Sense red screen edges, minimap, pause menu.
    /// Reacts to game events; reads the local player every frame.
    /// <para>Owner module: UI.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudController : MonoBehaviour
    {
        public static HudController Instance { get; private set; }

        // IMPLEMENT: UI module
        public void Initialize() => throw new NotImplementedException();
        public void ShowBanner(string text, Color color, float duration = 1.5f) => throw new NotImplementedException();
        public void SetVisible(bool visible) => throw new NotImplementedException();
    }
}
