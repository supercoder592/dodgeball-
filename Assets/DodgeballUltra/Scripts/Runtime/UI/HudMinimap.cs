using System.Collections.Generic;
using DodgeballUltra.Combat;
using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Top-left tactical minimap: court outline (both infield halves, centre line, the two outfield strips tinted in the
    /// colour of the team that uses them), players as dots in team colours (outfield players dimmed), the local player as
    /// a facing arrow, and match balls (live balls brighter). Oriented so the local team always attacks "up".
    /// <para>
    /// Stealth rules: enemies with <see cref="StatusEffectType.SilentFootsteps"/> (Gale's passive) or
    /// <see cref="StatusEffectType.Cloaked"/> are never drawn.
    /// </para>
    /// </summary>
    public sealed class HudMinimap
    {
        private const int MaxPlayers = 12;
        private const int MaxBalls = 16;

        private readonly RectTransform _root;
        private readonly RectTransform _area;
        private readonly Image _homeHalf;
        private readonly Image _awayHalf;
        private readonly Image _homeOutfield; // behind the Away baseline, used by Home
        private readonly Image _awayOutfield; // behind the Home baseline, used by Away
        private readonly Image[] _outline = new Image[4];
        private readonly Image _centerLine;
        private readonly Image[] _players = new Image[MaxPlayers];
        private readonly Image[] _balls = new Image[MaxBalls];
        private readonly RectTransform _local;
        private readonly Image _localArrow;

        private float _scale = 10f;
        private bool _flip;
        private Vector3 _center;
        private float _length = -1f;
        private float _width = -1f;
        private float _outfieldDepth = -1f;
        private bool _geometryFlip;

        public HudMinimap(RectTransform parent, float margin, Vector2 size)
        {
            _root = UiFactory.CreateRect("Minimap", parent).Place(UiAnchor.TopLeft, UiAnchor.TopLeft, new Vector2(margin, -margin), size);
            UiFactory.AddSubCanvas(_root.gameObject);

            var panel = UiFactory.CreatePanel(_root, "Panel", UiTheme.Panel);
            panel.rectTransform.Stretch();

            _area = UiFactory.CreateRect("Area", _root).Stretch(14f, 14f, 14f, 14f);

            // Order = draw order: zones, lines, balls, players, local marker.
            _awayOutfield = Rect("AwayOutfield", UiTheme.WithAlpha(UiTheme.AwayColor, 0.2f));
            _homeOutfield = Rect("HomeOutfield", UiTheme.WithAlpha(UiTheme.HomeColor, 0.2f));
            _homeHalf = Rect("HomeHalf", UiTheme.WithAlpha(UiTheme.HomeColor, 0.07f));
            _awayHalf = Rect("AwayHalf", UiTheme.WithAlpha(UiTheme.AwayColor, 0.07f));
            for (int i = 0; i < 4; i++) _outline[i] = Rect("Outline" + i, UiTheme.WithAlpha(Color.white, 0.6f));
            _centerLine = Rect("CenterLine", UiTheme.WithAlpha(Color.white, 0.6f));

            for (int i = 0; i < MaxBalls; i++)
            {
                _balls[i] = UiFactory.CreateImage(_area, "Ball" + i, UiFactory.CircleSprite, Color.white);
                _balls[i].rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(7f, 7f));
                _balls[i].gameObject.SetActive(false);
            }
            for (int i = 0; i < MaxPlayers; i++)
            {
                _players[i] = UiFactory.CreateImage(_area, "Player" + i, UiFactory.CircleSprite, Color.white);
                _players[i].rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(13f, 13f));
                _players[i].gameObject.SetActive(false);
            }

            _local = UiFactory.CreateRect("Local", _area).Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(22f, 22f));
            var ring = UiFactory.CreateImage(_local, "Ring", UiFactory.CircleSprite, Color.white);
            ring.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, new Vector2(15f, 15f));
            _localArrow = UiFactory.CreateImage(_local, "Arrow", UiFactory.TriangleSprite, Color.white);
            _localArrow.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, new Vector2(0f, 10f), new Vector2(12f, 10f));
            _local.gameObject.SetActive(false);
        }

        private Image Rect(string name, Color color)
        {
            var image = UiFactory.CreateImage(_area, name, UiFactory.WhiteSprite, color);
            image.rectTransform.Place(UiAnchor.Center, UiAnchor.Center, Vector2.zero, Vector2.zero);
            return image;
        }

        public void SetVisible(bool visible)
        {
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        public void Tick(DodgeballPlayer local)
        {
            var court = Court.Instance;
            float length = court != null ? court.length : 18f;
            float width = court != null ? court.width : 9f;
            float depth = court != null ? court.outfieldDepth : 3f;
            _center = court != null ? court.Center : Vector3.zero;
            _flip = local != null && local.Team == TeamId.Away; // local team always attacks "up"

            if (!Mathf.Approximately(length, _length) || !Mathf.Approximately(width, _width) ||
                !Mathf.Approximately(depth, _outfieldDepth) || _flip != _geometryFlip)
            {
                RebuildCourt(length, width, depth);
            }

            DrawBalls();
            DrawPlayers(local);
        }

        // ------------------------------------------------------------------ geometry

        private void RebuildCourt(float length, float width, float depth)
        {
            _length = length;
            _width = width;
            _outfieldDepth = depth;
            _geometryFlip = _flip;

            Vector2 area = _area.rect.size;
            if (area.x <= 1f || area.y <= 1f) area = _root.sizeDelta - new Vector2(28f, 28f);
            float extentX = width * 0.5f + 0.6f;
            float extentZ = length * 0.5f + depth + 0.6f;
            _scale = Mathf.Min(area.x / (2f * extentX), area.y / (2f * extentZ));

            float halfL = length * 0.5f;
            float halfW = width * 0.5f;
            const float line = 2f;

            // Halves (court-space z ranges) mapped through the orientation.
            SetZone(_homeHalf, -halfL, 0f, halfW);
            SetZone(_awayHalf, 0f, halfL, halfW);
            SetZone(_awayOutfield, -halfL - depth, -halfL, halfW); // Away's outfield: behind the Home baseline
            SetZone(_homeOutfield, halfL, halfL + depth, halfW);   // Home's outfield: behind the Away baseline

            float w = width * _scale;
            float h = length * _scale;
            _outline[0].rectTransform.anchoredPosition = new Vector2(0f, h * 0.5f);
            _outline[0].rectTransform.sizeDelta = new Vector2(w + line, line);
            _outline[1].rectTransform.anchoredPosition = new Vector2(0f, -h * 0.5f);
            _outline[1].rectTransform.sizeDelta = new Vector2(w + line, line);
            _outline[2].rectTransform.anchoredPosition = new Vector2(-w * 0.5f, 0f);
            _outline[2].rectTransform.sizeDelta = new Vector2(line, h + line);
            _outline[3].rectTransform.anchoredPosition = new Vector2(w * 0.5f, 0f);
            _outline[3].rectTransform.sizeDelta = new Vector2(line, h + line);
            _centerLine.rectTransform.anchoredPosition = Vector2.zero;
            _centerLine.rectTransform.sizeDelta = new Vector2(w, line);
        }

        private void SetZone(Image image, float zMin, float zMax, float halfWidth)
        {
            float zCenter = (zMin + zMax) * 0.5f;
            if (_flip) zCenter = -zCenter;
            image.rectTransform.anchoredPosition = new Vector2(0f, zCenter * _scale);
            image.rectTransform.sizeDelta = new Vector2(halfWidth * 2f * _scale, (zMax - zMin) * _scale);
        }

        private Vector2 ToMap(Vector3 world)
        {
            float x = world.x - _center.x;
            float z = world.z - _center.z;
            if (_flip)
            {
                x = -x;
                z = -z;
            }
            return new Vector2(x * _scale, z * _scale);
        }

        // ------------------------------------------------------------------ dynamic content

        private void DrawPlayers(DodgeballPlayer local)
        {
            IReadOnlyList<DodgeballPlayer> all = PlayerRegistry.All;
            int used = 0;
            bool localDrawn = false;

            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || !p.IsInitialized) continue;

                if (ReferenceEquals(p, local))
                {
                    localDrawn = true;
                    _local.anchoredPosition = ToMap(p.Position);
                    Vector3 f = p.Forward;
                    if (_flip) f = -f;
                    float angle = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                    _local.localRotation = Quaternion.Euler(0f, 0f, -angle);
                    continue;
                }

                if (local != null && PlayerRegistry.AreEnemies(local, p) && IsHiddenFromEnemies(p)) continue;
                if (used >= MaxPlayers) continue;

                var dot = _players[used++];
                if (!dot.gameObject.activeSelf) dot.gameObject.SetActive(true);
                dot.rectTransform.anchoredPosition = ToMap(p.Position);

                float alpha = 1f;
                if (!p.IsInfield) alpha = 0.5f;
                if (p.Health != null && p.Health.IsEliminated) alpha = 0.3f;
                dot.color = UiTheme.WithAlpha(UiTheme.TeamColor(p.Team), alpha);
            }

            for (int i = used; i < MaxPlayers; i++)
                if (_players[i].gameObject.activeSelf) _players[i].gameObject.SetActive(false);

            if (_local.gameObject.activeSelf != localDrawn) _local.gameObject.SetActive(localDrawn);
            if (localDrawn && local != null) _localArrow.color = Color.Lerp(UiTheme.TeamColor(local.Team), Color.white, 0.5f);
        }

        private static bool IsHiddenFromEnemies(DodgeballPlayer p)
        {
            var status = p.Status;
            return status != null && (status.Has(StatusEffectType.SilentFootsteps) || status.Has(StatusEffectType.Cloaked));
        }

        private void DrawBalls()
        {
            int used = 0;
            var manager = BallManager.Instance;
            if (manager != null)
            {
                IReadOnlyList<DodgeBall> balls = manager.MatchBalls;
                if (balls != null)
                {
                    for (int i = 0; i < balls.Count && used < MaxBalls; i++)
                    {
                        var ball = balls[i];
                        if (ball == null) continue;
                        var state = ball.State;
                        if (state == BallState.Despawned || state == BallState.Held) continue;

                        var dot = _balls[used++];
                        if (!dot.gameObject.activeSelf) dot.gameObject.SetActive(true);
                        dot.rectTransform.anchoredPosition = ToMap(ball.transform.position);
                        dot.color = state == BallState.Live ? Color.white : UiTheme.WithAlpha(new Color(0.85f, 0.87f, 0.9f, 1f), 0.75f);
                        float s = state == BallState.Live ? 8f : 6f;
                        dot.rectTransform.sizeDelta = new Vector2(s, s);
                    }
                }
            }

            for (int i = used; i < MaxBalls; i++)
                if (_balls[i].gameObject.activeSelf) _balls[i].gameObject.SetActive(false);
        }
    }
}
