using DodgeballUltra.Match;
using DodgeballUltra.Player;
using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Top-centre broadcast scoreboard:
    /// <code>
    /// [▌HOME  ▮▮▯ | 1 ]  [ 2:14 · ROUND 2 ]  [ 0 | ▮▮▮  AWAY▐]
    /// </code>
    /// Team names in team colours, round wins in the coloured score boxes, infield pips (lit = player still in the infield
    /// and alive), round timer (turns red and pulses in the last 10 s of a live round) and the round number. The local
    /// team is tagged "YOUR TEAM". Reads MatchManager / PlayerRegistry each frame and only touches text on change.
    /// </summary>
    public sealed class HudScoreboard
    {
        private const int MaxPips = 6;

        private sealed class TeamBlock
        {
            public TeamId Team;
            public CachedText Score;
            public Image ScoreBox;
            public Image[] Pips;
            public Text YourTeam;
            public int ShownTotal = -1;
            public int ShownInfield = -1;
        }

        private readonly RectTransform _root;
        private readonly CachedText _timer;
        private readonly CachedText _round;
        private readonly RectTransform _timerRect;
        private readonly TeamBlock _home;
        private readonly TeamBlock _away;
        private TeamId _shownLocalTeam = (TeamId)(-99);

        /// <summary>Seconds left at which the timer turns red.</summary>
        public float urgentSeconds = 10f;

        public HudScoreboard(RectTransform parent, float margin)
        {
            _root = UiFactory.CreateRect("Scoreboard", parent).Place(UiAnchor.Top, UiAnchor.Top, new Vector2(0f, -margin * 0.6f), new Vector2(860f, 112f));

            // ---------------------------------------------------------------- centre clock
            var clock = UiFactory.CreatePanel(_root, "Clock", UiTheme.PanelStrong);
            clock.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, Vector2.zero, new Vector2(184f, 104f));
            var timerText = UiFactory.CreateText(clock.transform, "Timer", "0:00", 50, UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            timerText.rectTransform.Place(UiAnchor.Top, UiAnchor.Top, new Vector2(0f, -6f), new Vector2(184f, 62f));
            _timerRect = timerText.rectTransform;
            _timer = new CachedText(timerText);
            var roundText = UiFactory.CreateText(clock.transform, "Round", "ROUND 1", 18, UiTheme.TextSecondary, TextAnchor.MiddleCenter);
            roundText.rectTransform.Place(UiAnchor.Bottom, UiAnchor.Bottom, new Vector2(0f, 10f), new Vector2(184f, 26f));
            _round = new CachedText(roundText);

            _home = BuildTeam(TeamId.Home, -1f);
            _away = BuildTeam(TeamId.Away, +1f);
        }

        private TeamBlock BuildTeam(TeamId team, float side)
        {
            var color = UiTheme.TeamColor(team);
            bool left = side < 0f;
            var block = new TeamBlock { Team = team, Pips = new Image[MaxPips] };

            // Block sits next to the clock: pivot on the clock side.
            var panel = UiFactory.CreatePanel(_root, UiTheme.TeamName(team), UiTheme.Panel);
            panel.rectTransform.Place(UiAnchor.Top, left ? UiAnchor.TopRight : UiAnchor.TopLeft,
                new Vector2(side * 98f, -8f), new Vector2(330f, 84f));

            // Team colour bar on the outer edge.
            var bar = UiFactory.CreateImage(panel.transform, "Bar", UiFactory.WhiteSprite, color);
            bar.rectTransform.Place(left ? UiAnchor.Left : UiAnchor.Right, left ? UiAnchor.Left : UiAnchor.Right, Vector2.zero, new Vector2(6f, 84f));

            // Score box (round wins) on the inner edge.
            block.ScoreBox = UiFactory.CreatePanel(panel.transform, "ScoreBox", UiTheme.WithAlpha(color, 0.92f), true);
            block.ScoreBox.rectTransform.Place(left ? UiAnchor.Right : UiAnchor.Left, left ? UiAnchor.Right : UiAnchor.Left,
                new Vector2(left ? -8f : 8f, 0f), new Vector2(70f, 68f));
            var score = UiFactory.CreateText(block.ScoreBox.transform, "Score", "0", 46, Color.white, TextAnchor.MiddleCenter);
            score.rectTransform.Stretch();
            block.Score = new CachedText(score);

            // Team name.
            var name = UiFactory.CreateText(panel.transform, "Name", UiTheme.TeamName(team), 30, UiTheme.TextPrimary,
                left ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft);
            name.rectTransform.Place(left ? UiAnchor.TopRight : UiAnchor.TopLeft, left ? UiAnchor.TopRight : UiAnchor.TopLeft,
                new Vector2(left ? -92f : 92f, -6f), new Vector2(210f, 40f));

            // Infield pips under the name.
            var pipRow = UiFactory.CreateRect("Infield", panel.transform).Place(left ? UiAnchor.BottomRight : UiAnchor.BottomLeft,
                left ? UiAnchor.BottomRight : UiAnchor.BottomLeft, new Vector2(left ? -92f : 92f, 12f), new Vector2(210f, 14f));
            for (int i = 0; i < MaxPips; i++)
            {
                var pip = UiFactory.CreatePanel(pipRow, "Pip" + i, Color.white, true);
                float x = (i * 30f) * (left ? -1f : 1f);
                pip.rectTransform.Place(left ? UiAnchor.Right : UiAnchor.Left, left ? UiAnchor.Right : UiAnchor.Left, new Vector2(x, 0f), new Vector2(24f, 10f));
                pip.gameObject.SetActive(false);
                block.Pips[i] = pip;
            }

            // "YOUR TEAM" tag below the block.
            block.YourTeam = UiFactory.CreateText(panel.transform, "YourTeam", "YOUR TEAM", 15, color,
                left ? TextAnchor.UpperRight : TextAnchor.UpperLeft);
            block.YourTeam.rectTransform.Place(left ? UiAnchor.BottomRight : UiAnchor.BottomLeft, left ? UiAnchor.TopRight : UiAnchor.TopLeft,
                new Vector2(left ? -8f : 8f, -6f), new Vector2(200f, 20f));
            block.YourTeam.gameObject.SetActive(false);
            return block;
        }

        public void SetVisible(bool visible)
        {
            if (_root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        public void Tick(DodgeballPlayer local, float dt)
        {
            var match = MatchManager.Instance;

            // ---------------------------------------------------------------- clock
            if (match != null)
            {
                float remaining = Mathf.Max(0f, match.RoundTimeRemaining);
                _timer.Set(UiStrings.Timer(Mathf.CeilToInt(remaining - 1e-3f)));
                bool urgent = match.Phase == MatchPhase.Playing && remaining <= urgentSeconds;
                _timer.SetColor(urgent ? Color.Lerp(UiTheme.Danger, UiTheme.TextPrimary, HudAnim.Pulse(2f) * 0.35f) : UiTheme.TextPrimary);
                float scale = urgent ? 1f + 0.06f * HudAnim.Pulse(1f) : 1f;
                _timerRect.localScale = new Vector3(scale, scale, 1f);
                _round.Set(match.Phase == MatchPhase.MatchEnd ? "FINAL" : UiStrings.Round(Mathf.Max(1, match.RoundNumber)));

                _home.Score.SetInt(match.GetScore(TeamId.Home));
                _away.Score.SetInt(match.GetScore(TeamId.Away));
            }
            else
            {
                _timer.Set("-:--");
                _timer.SetColor(UiTheme.TextSecondary);
                _round.Set(string.Empty);
            }

            // ---------------------------------------------------------------- infield pips
            CountTeam(TeamId.Home, out int homeTotal, out int homeIn);
            CountTeam(TeamId.Away, out int awayTotal, out int awayIn);
            if (match != null && match.Rules != null)
            {
                homeTotal = Mathf.Max(homeTotal, match.Rules.playersPerTeam);
                awayTotal = Mathf.Max(awayTotal, match.Rules.playersPerTeam);
            }
            UpdatePips(_home, homeTotal, homeIn);
            UpdatePips(_away, awayTotal, awayIn);

            // ---------------------------------------------------------------- "YOUR TEAM"
            var localTeam = local != null ? local.Team : TeamId.None;
            if (localTeam != _shownLocalTeam)
            {
                _shownLocalTeam = localTeam;
                _home.YourTeam.gameObject.SetActive(localTeam == TeamId.Home);
                _away.YourTeam.gameObject.SetActive(localTeam == TeamId.Away);
            }
        }

        private static void CountTeam(TeamId team, out int total, out int infield)
        {
            total = 0;
            infield = 0;
            var all = PlayerRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.Team != team) continue;
                total++;
                if (p.IsInfield && p.Health != null && p.Health.IsAlive) infield++;
            }
        }

        private static void UpdatePips(TeamBlock block, int total, int infield)
        {
            total = Mathf.Clamp(total, 0, MaxPips);
            infield = Mathf.Clamp(infield, 0, total);
            if (total == block.ShownTotal && infield == block.ShownInfield) return;
            block.ShownTotal = total;
            block.ShownInfield = infield;
            var lit = Color.Lerp(UiTheme.TeamColor(block.Team), Color.white, 0.55f);
            for (int i = 0; i < MaxPips; i++)
            {
                var pip = block.Pips[i];
                bool shown = i < total;
                if (pip.gameObject.activeSelf != shown) pip.gameObject.SetActive(shown);
                if (shown) pip.color = i < infield ? lit : UiTheme.Track;
            }
        }
    }
}
