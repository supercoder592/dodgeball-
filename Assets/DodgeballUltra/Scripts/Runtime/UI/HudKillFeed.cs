using UnityEngine;
using UnityEngine.UI;

namespace DodgeballUltra.UI
{
    /// <summary>
    /// Top-right event feed (eliminations and returns to the infield). Newest entry on top, each entry lives
    /// <see cref="entryLifetime"/> seconds and fades out; a fixed pool of rows is recycled (no instantiation after build).
    /// Entry text is rich text (team-coloured names) built once per event.
    /// </summary>
    public sealed class HudKillFeed
    {
        private struct Row
        {
            public RectTransform Rect;
            public CanvasGroup Group;
            public Image Accent;
            public Text Text;
        }

        private struct Entry
        {
            public string Text;
            public Color Accent;
            public float Age;
            public bool Used;
        }

        private readonly Row[] _rows;
        private readonly Entry[] _entries;

        /// <summary>Seconds an entry stays on screen.</summary>
        public float entryLifetime = 6f;
        /// <summary>Fade-out time at the end of the lifetime.</summary>
        public float fadeTime = 0.6f;

        public HudKillFeed(RectTransform parent, float margin, int maxEntries)
        {
            maxEntries = Mathf.Clamp(maxEntries, 1, 10);
            _rows = new Row[maxEntries];
            _entries = new Entry[maxEntries];

            const float rowHeight = 38f;
            const float spacing = 6f;
            var root = UiFactory.CreateRect("KillFeed", parent).Place(UiAnchor.TopRight, UiAnchor.TopRight, new Vector2(-margin, -margin),
                new Vector2(460f, maxEntries * (rowHeight + spacing)));

            for (int i = 0; i < maxEntries; i++)
            {
                var bg = UiFactory.CreatePanel(root, "Row" + i, UiTheme.Panel, true);
                bg.rectTransform.Place(UiAnchor.TopRight, UiAnchor.TopRight, new Vector2(0f, -i * (rowHeight + spacing)), new Vector2(460f, rowHeight));
                var accent = UiFactory.CreateImage(bg.transform, "Accent", UiFactory.WhiteSprite, Color.white);
                accent.rectTransform.Place(UiAnchor.Right, UiAnchor.Right, Vector2.zero, new Vector2(4f, rowHeight));
                var text = UiFactory.CreateText(bg.transform, "Text", string.Empty, 19, UiTheme.TextPrimary, TextAnchor.MiddleRight);
                text.rectTransform.Stretch(12f, 16f, 0f, 0f);
                var group = UiFactory.AddCanvasGroup(bg.gameObject, 0f);
                bg.gameObject.SetActive(false);
                _rows[i] = new Row { Rect = bg.rectTransform, Group = group, Accent = accent, Text = text };
            }
        }

        /// <summary>Adds an entry at the top (older entries move down; the oldest falls off).</summary>
        public void Push(string richText, Color accent)
        {
            for (int i = _entries.Length - 1; i > 0; i--) _entries[i] = _entries[i - 1];
            _entries[0] = new Entry { Text = richText, Accent = accent, Age = 0f, Used = true };
            for (int i = 0; i < _rows.Length; i++) ApplyRow(i);
        }

        public void Clear()
        {
            for (int i = 0; i < _entries.Length; i++) _entries[i] = default;
            for (int i = 0; i < _rows.Length; i++) ApplyRow(i);
        }

        public void Tick(float dt)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (!_entries[i].Used) continue;
                _entries[i].Age += dt;
                float remaining = entryLifetime - _entries[i].Age;
                if (remaining <= 0f)
                {
                    _entries[i].Used = false;
                    ApplyRow(i);
                    continue;
                }
                float appear = Mathf.Clamp01(_entries[i].Age / 0.15f);
                _rows[i].Group.alpha = Mathf.Min(appear, Mathf.Clamp01(remaining / fadeTime));
            }
        }

        private void ApplyRow(int i)
        {
            var row = _rows[i];
            var entry = _entries[i];
            if (!entry.Used)
            {
                if (row.Rect.gameObject.activeSelf) row.Rect.gameObject.SetActive(false);
                return;
            }
            if (!row.Rect.gameObject.activeSelf) row.Rect.gameObject.SetActive(true);
            row.Text.text = entry.Text;
            row.Accent.color = entry.Accent;
            row.Group.alpha = Mathf.Clamp01(entry.Age / 0.15f);
        }
    }
}
