using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// World-to-screen soft-lock marker on <c>Combat.CurrentTarget</c>: four corner brackets sized from the target's real
    /// projected height (camera FOV + distance, so it reads like a camera-tracking box rather than a game icon), a snap-in
    /// animation when the lock changes, and the target's name + distance underneath.
    /// </summary>
    public sealed class HudLockMarker
    {
        private readonly RectTransform _root;
        private readonly CanvasGroup _group;
        private readonly RectTransform[] _corners = new RectTransform[4];
        private readonly Image[] _lines = new Image[8];
        private readonly CachedText _name;
        private readonly CachedText _distance;

        private DodgeballPlayer _target;
        private float _alpha;
        private float _snap;
        private Vector2 _size = new Vector2(80f, 160f);

        /// <summary>Box height limits in reference px.</summary>
        public float minHeight = 70f;
        public float maxHeight = 460f;

        public HudLockMarker(RectTransform parent)
        {
            _root = UiFactory.CreateRect("LockMarker", parent).Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, _size);
            UiFactory.AddSubCanvas(_root.gameObject);
            _group = UiFactory.AddCanvasGroup(_root.gameObject, 0f);

            for (int i = 0; i < 4; i++)
            {
                // 0 TL, 1 TR, 2 BL, 3 BR
                var anchor = new Vector2(i % 2, i < 2 ? 1f : 0f);
                var corner = UiFactory.CreateRect("Corner" + i, _root).Place(anchor, anchor, Vector2.zero, new Vector2(18f, 18f));
                _corners[i] = corner;
                var horizontal = UiFactory.CreateImage(corner, "H", UiFactory.WhiteSprite, Color.white);
                horizontal.rectTransform.Place(anchor, anchor, Vector2.zero, new Vector2(18f, 3f));
                var vertical = UiFactory.CreateImage(corner, "V", UiFactory.WhiteSprite, Color.white);
                vertical.rectTransform.Place(anchor, anchor, Vector2.zero, new Vector2(3f, 18f));
                UiFactory.AddShadow(horizontal, new Color(0f, 0f, 0f, 0.45f), new Vector2(1f, -1f));
                UiFactory.AddShadow(vertical, new Color(0f, 0f, 0f, 0.45f), new Vector2(1f, -1f));
                _lines[i * 2] = horizontal;
                _lines[i * 2 + 1] = vertical;
            }

            var label = UiFactory.CreateRect("Label", _root).Place(UiAnchor.Bottom, UiAnchor.Top, new Vector2(0f, -8f), new Vector2(260f, 44f));
            var name = UiFactory.CreateText(label, "Name", string.Empty, 18, UiTheme.TextPrimary, TextAnchor.UpperCenter);
            name.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, Vector2.zero, new Vector2(260f, 22f));
            _name = new CachedText(name);
            var distance = UiFactory.CreateText(label, "Distance", string.Empty, 15, UiTheme.TextSecondary, TextAnchor.UpperCenter, FontStyle.Normal);
            distance.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, new Vector2(0f, -22f), new Vector2(260f, 20f));
            _distance = new CachedText(distance);
        }

        public void SetVisible(bool visible)
        {
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// <param name="local">Local player.</param>
        /// <param name="target">Soft-locked enemy or null.</param>
        /// <param name="cam">Gameplay camera.</param>
        /// <param name="canvasRect">Root rect of the HUD canvas.</param>
        /// <param name="canvasScale">Canvas.scaleFactor (screen px per reference px).</param>
        public void Tick(DodgeballPlayer local, DodgeballPlayer target, Camera cam, RectTransform canvasRect, float canvasScale, float dt)
        {
            bool valid = target != null && cam != null && target.IsTargetable;
            Vector3 screen = Vector3.zero;
            float targetHeight = 1.8f;
            if (valid)
            {
                // Frame the whole body: centre the box at mid-height of the collision capsule.
                targetHeight = target.Capsule != null ? target.Capsule.height : 1.8f;
                screen = cam.WorldToScreenPoint(target.Position + Vector3.up * (targetHeight * 0.5f));
                valid = screen.z > 0.1f;
            }

            if (valid && !ReferenceEquals(target, _target))
            {
                // New lock: snap-in animation and new identity.
                _target = target;
                _snap = 1f;
                var data = target.Character;
                _name.Set(data != null && !string.IsNullOrEmpty(data.displayName) ? data.displayName.ToUpperInvariant() : target.Hero.ToString().ToUpperInvariant());
                var color = UiTheme.TeamColor(target.Team);
                for (int i = 0; i < _lines.Length; i++) _lines[i].color = Color.Lerp(color, Color.white, 0.35f);
                _name.SetColor(Color.Lerp(color, Color.white, 0.6f));
            }

            _alpha = HudAnim.Damp(_alpha, valid ? 1f : 0f, 18f, dt);
            _group.alpha = _alpha;
            if (!valid)
            {
                if (_alpha < 0.02f) _target = null;
                return;
            }

            // ---------------------------------------------------------------- position (screen -> canvas)
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screen, null, out Vector2 local2D))
                _root.anchoredPosition = local2D;

            // ---------------------------------------------------------------- size from real projected height
            float distance = screen.z;
            float pixelsPerMeter = Screen.height / (2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float height = Mathf.Clamp(targetHeight * 1.05f * pixelsPerMeter / Mathf.Max(0.01f, canvasScale), minHeight, maxHeight);
            var desired = new Vector2(Mathf.Max(56f, height * 0.5f), height);
            _size = Vector2.Lerp(_size, desired, 1f - Mathf.Exp(-20f * dt));

            _snap = Mathf.Max(0f, _snap - dt * 5f);
            float snapScale = 1f + 0.45f * HudAnim.EaseOutCubic(_snap);
            _root.sizeDelta = _size * snapScale;

            if (local != null)
            {
                int meters = Mathf.RoundToInt(Vector3.Distance(local.Position, target.Position));
                _distance.Set(UiStrings.Meters(meters));
            }
        }
    }
}
