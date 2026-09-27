using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;
#if DU_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace DodgeballUltra.UI
{
    /// <summary>Common anchor/pivot presets (normalised rect coordinates).</summary>
    public static class UiAnchor
    {
        public static readonly Vector2 TopLeft = new Vector2(0f, 1f);
        public static readonly Vector2 Top = new Vector2(0.5f, 1f);
        public static readonly Vector2 TopRight = new Vector2(1f, 1f);
        public static readonly Vector2 Left = new Vector2(0f, 0.5f);
        public static readonly Vector2 Center = new Vector2(0.5f, 0.5f);
        public static readonly Vector2 Right = new Vector2(1f, 0.5f);
        public static readonly Vector2 BottomLeft = new Vector2(0f, 0f);
        public static readonly Vector2 Bottom = new Vector2(0.5f, 0f);
        public static readonly Vector2 BottomRight = new Vector2(1f, 0f);
    }

    /// <summary>
    /// Builds uGUI hierarchies in code (no prefabs): screen-space canvases with the 1920×1080 CanvasScaler, rects, text,
    /// images, buttons, and a small set of procedurally generated, anti-aliased sprites (rounded panel, disc, ring, soft
    /// glow, screen-edge vignette, triangle, fades). Also guarantees an <see cref="EventSystem"/> with the input module that
    /// matches the compiled input backend.
    /// <para>
    /// UI uses the built-in UI shader through uGUI; no runtime materials are created here (engineering rule 8).
    /// Generated textures are cached for the session and recreated if Unity destroyed them (leaving play mode).
    /// </para>
    /// </summary>
    public static class UiFactory
    {
        /// <summary>Unity's built-in "UI" layer.</summary>
        public const int UiLayer = 5;

        private static Font s_font;
        private static Sprite s_white;
        private static Sprite s_rounded;
        private static Sprite s_roundedSmall;
        private static Sprite s_circle;
        private static Sprite s_ring;
        private static Sprite s_softGlow;
        private static Sprite s_vignette;
        private static Sprite s_triangle;
        private static Sprite s_horizontalFade;
        private static Sprite s_verticalFade;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_font = null;
            s_white = s_rounded = s_roundedSmall = s_circle = s_ring = s_softGlow = s_vignette = s_triangle = null;
            s_horizontalFade = s_verticalFade = null;
        }

        // ------------------------------------------------------------------ font

        /// <summary>
        /// Built-in runtime font: "LegacyRuntime.ttf" (Unity 2022.2+) with "Arial.ttf" as fallback (older editors), then any
        /// OS sans-serif font.
        /// </summary>
        public static Font DefaultFont
        {
            get
            {
                if (s_font != null) return s_font;
#if UNITY_2022_2_OR_NEWER
                s_font = TryBuiltinFont("LegacyRuntime.ttf") ?? TryBuiltinFont("Arial.ttf");
#else
                s_font = TryBuiltinFont("Arial.ttf") ?? TryBuiltinFont("LegacyRuntime.ttf");
#endif
                if (s_font == null)
                {
                    try
                    {
                        s_font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" }, 32);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Dodgeball Ultra] No UI font available: {e.Message}");
                    }
                }
                return s_font;
            }
        }

        private static Font TryBuiltinFont(string name)
        {
            try
            {
                var f = Resources.GetBuiltinResource<Font>(name);
                return f != null ? f : null;
            }
            catch (Exception)
            {
                return null; // Unity 6 throws for "Arial.ttf"; 2021 logs + returns null for "LegacyRuntime.ttf".
            }
        }

        // ------------------------------------------------------------------ sprites

        /// <summary>Plain white quad (tinted by Image.color). Needed for Filled images.</summary>
        public static Sprite WhiteSprite => s_white != null ? s_white : (s_white = BuildSprite("DU_UI_White", 4, 4, (x, y) => 1f, Vector4.zero));

        /// <summary>64 px rounded rectangle, 14 px corner radius, 9-sliced (use Image.Type.Sliced).</summary>
        public static Sprite RoundedSprite => s_rounded != null ? s_rounded : (s_rounded = BuildRounded("DU_UI_Rounded", 64, 14f));

        /// <summary>32 px rounded rectangle, 6 px radius, 9-sliced (chips, small buttons, bars).</summary>
        public static Sprite RoundedSmallSprite => s_roundedSmall != null ? s_roundedSmall : (s_roundedSmall = BuildRounded("DU_UI_RoundedSmall", 32, 6f));

        /// <summary>Anti-aliased disc (radial fills, dots).</summary>
        public static Sprite CircleSprite
        {
            get
            {
                if (s_circle != null) return s_circle;
                const int size = 128;
                const float r = size * 0.5f - 1f;
                return s_circle = BuildSprite("DU_UI_Circle", size, size, (x, y) =>
                {
                    float d = Distance(x, y, size);
                    return Mathf.Clamp01(r - d + 0.5f);
                }, Vector4.zero);
            }
        }

        /// <summary>Anti-aliased ring (progress rings around ability slots, lock markers).</summary>
        public static Sprite RingSprite
        {
            get
            {
                if (s_ring != null) return s_ring;
                const int size = 128;
                const float outer = size * 0.5f - 1f;
                const float thickness = 9f;
                return s_ring = BuildSprite("DU_UI_Ring", size, size, (x, y) =>
                {
                    float d = Distance(x, y, size);
                    return Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - (outer - thickness) + 0.5f);
                }, Vector4.zero);
            }
        }

        /// <summary>Soft radial glow (quadratic falloff) for "ready" pulses.</summary>
        public static Sprite SoftGlowSprite
        {
            get
            {
                if (s_softGlow != null) return s_softGlow;
                const int size = 128;
                const float r = size * 0.5f;
                return s_softGlow = BuildSprite("DU_UI_SoftGlow", size, size, (x, y) =>
                {
                    float k = 1f - Mathf.Clamp01(Distance(x, y, size) / r);
                    return k * k;
                }, Vector4.zero);
            }
        }

        /// <summary>Full-screen edge vignette: transparent centre, opaque rounded-square edges (Danger Sense).</summary>
        public static Sprite EdgeVignetteSprite
        {
            get
            {
                if (s_vignette != null) return s_vignette;
                const int size = 256;
                return s_vignette = BuildSprite("DU_UI_Vignette", size, size, (x, y) =>
                {
                    float u = Mathf.Abs((x + 0.5f) / size * 2f - 1f);
                    float v = Mathf.Abs((y + 0.5f) / size * 2f - 1f);
                    // Super-ellipse distance (p = 4): hugs the screen edges but keeps soft corners.
                    float e = Mathf.Pow(Mathf.Pow(u, 4f) + Mathf.Pow(v, 4f), 0.25f);
                    float a = Mathf.InverseLerp(0.6f, 1.02f, e);
                    return a * a * (3f - 2f * a);
                }, Vector4.zero);
            }
        }

        /// <summary>Upward pointing triangle (minimap facing marker).</summary>
        public static Sprite TriangleSprite
        {
            get
            {
                if (s_triangle != null) return s_triangle;
                const int size = 64;
                return s_triangle = BuildSprite("DU_UI_Triangle", size, size, (x, y) =>
                {
                    // 4×4 supersampling for clean edges.
                    int inside = 0;
                    for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        float px = (x + (sx + 0.5f) / 4f) / size; // 0..1
                        float py = (y + (sy + 0.5f) / 4f) / size;
                        // Apex at (0.5, 0.95), base from (0.1, 0.08) to (0.9, 0.08).
                        if (py < 0.08f || py > 0.95f) continue;
                        float halfWidth = 0.4f * (0.95f - py) / 0.87f;
                        if (Mathf.Abs(px - 0.5f) <= halfWidth) inside++;
                    }
                    return inside / 16f;
                }, Vector4.zero);
            }
        }

        /// <summary>Horizontal strip that is opaque in the middle and fades out left and right (banners, separators).</summary>
        public static Sprite HorizontalFadeSprite
        {
            get
            {
                if (s_horizontalFade != null) return s_horizontalFade;
                const int w = 256;
                return s_horizontalFade = BuildSprite("DU_UI_HFade", w, 2, (x, y) =>
                {
                    float u = Mathf.Abs((x + 0.5f) / w * 2f - 1f);
                    float a = 1f - Mathf.InverseLerp(0.45f, 1f, u);
                    return a * a * (3f - 2f * a);
                }, Vector4.zero);
            }
        }

        /// <summary>Vertical gradient, opaque at the bottom and transparent at the top (backgrounds).</summary>
        public static Sprite VerticalFadeSprite
        {
            get
            {
                if (s_verticalFade != null) return s_verticalFade;
                const int h = 128;
                return s_verticalFade = BuildSprite("DU_UI_VFade", 2, h, (x, y) =>
                {
                    float v = 1f - (y + 0.5f) / h;
                    return v * v;
                }, Vector4.zero);
            }
        }

        private static float Distance(int x, int y, int size)
        {
            float c = size * 0.5f;
            float dx = x + 0.5f - c;
            float dy = y + 0.5f - c;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static Sprite BuildRounded(string name, int size, float radius)
        {
            float border = Mathf.Ceil(radius) + 2f;
            return BuildSprite(name, size, size, (x, y) =>
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                float cx = Mathf.Clamp(px, radius, size - radius);
                float cy = Mathf.Clamp(py, radius, size - radius);
                float dx = px - cx;
                float dy = py - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                return Mathf.Clamp01(radius - d + 0.5f);
            }, new Vector4(border, border, border, border));
        }

        /// <summary>Creates a white sprite whose alpha is given per pixel by <paramref name="alpha"/>.</summary>
        private static Sprite BuildSprite(string name, int width, int height, Func<int, int, float> alpha, Vector4 border)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha(x, y)) * 255f);
                pixels[y * width + x] = new Color32(255, 255, 255, a);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f, 0u,
                SpriteMeshType.FullRect, border);
            sprite.name = name;
            return sprite;
        }

        // ------------------------------------------------------------------ canvas / rects

        /// <summary>
        /// Screen-space-overlay canvas with CanvasScaler (1920×1080, match 0.5). <paramref name="interactive"/> adds a
        /// GraphicRaycaster (menus); the HUD is non-interactive so it never steals clicks.
        /// </summary>
        public static Canvas CreateScreenCanvas(string name, int sortingOrder, Transform parent, bool interactive)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = UiLayer };
            if (parent != null) go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            canvas.pixelPerfect = false;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = UiTheme.ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = UiTheme.MatchWidthOrHeight;
            scaler.referencePixelsPerUnit = 100f;

            if (interactive) go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>
        /// Nested canvas for frequently changing widgets: their per-frame vertex updates then only rebuild their own batch,
        /// not the whole HUD.
        /// </summary>
        public static Canvas AddSubCanvas(GameObject go)
        {
            var canvas = go.GetComponent<Canvas>();
            if (canvas == null) canvas = go.AddComponent<Canvas>();
            return canvas;
        }

        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = UiLayer };
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>Anchors the rect to a single point with a fixed size.</summary>
        public static RectTransform Place(this RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Stretches the rect over its parent with optional insets (reference px).</summary>
        public static RectTransform Stretch(this RectTransform rt, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = UiAnchor.Center;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        // ------------------------------------------------------------------ graphics

        public static Image CreateImage(Transform parent, string name, Sprite sprite, Color color, Image.Type type = Image.Type.Simple)
        {
            var rt = CreateRect(name, parent);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = type;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>Rounded, 9-sliced panel.</summary>
        public static Image CreatePanel(Transform parent, string name, Color color, bool small = false) =>
            CreateImage(parent, name, small ? RoundedSmallSprite : RoundedSprite, color, Image.Type.Sliced);

        /// <summary>Filled image (radial or linear) for bars, cooldown sweeps and rings.</summary>
        public static Image CreateFilled(Transform parent, string name, Sprite sprite, Color color, Image.FillMethod method, int origin,
            bool clockwise = true)
        {
            var image = CreateImage(parent, name, sprite, color, Image.Type.Filled);
            image.fillMethod = method;
            image.fillOrigin = origin;
            image.fillClockwise = clockwise;
            image.fillAmount = 1f;
            return image;
        }

        public static Text CreateText(Transform parent, string name, string text, int size, Color color, TextAnchor alignment,
            FontStyle style = FontStyle.Bold, bool shadow = true)
        {
            var rt = CreateRect(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = DefaultFont;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = alignment;
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.text = text ?? string.Empty;
            if (shadow) AddShadow(t, UiTheme.TextShadow, new Vector2(1.5f, -1.5f));
            return t;
        }

        /// <summary>Multi-line paragraph text that wraps inside its rect.</summary>
        public static Text CreateParagraph(Transform parent, string name, string text, int size, Color color, TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var t = CreateText(parent, name, text, size, color, alignment, FontStyle.Normal, false);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.lineSpacing = 1.1f;
            return t;
        }

        public static Shadow AddShadow(Graphic graphic, Color color, Vector2 distance)
        {
            var shadow = graphic.gameObject.AddComponent<Shadow>();
            shadow.effectColor = color;
            shadow.effectDistance = distance;
            return shadow;
        }

        public static CanvasGroup AddCanvasGroup(GameObject go, float alpha = 1f)
        {
            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();
            group.alpha = alpha;
            group.interactable = false;
            group.blocksRaycasts = false;
            return group;
        }

        // ------------------------------------------------------------------ buttons

        /// <summary>Colour tint block for dark surface buttons (hover brightens, press darkens).</summary>
        public static ColorBlock ButtonColors()
        {
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = new Color(0.8f, 0.8f, 0.82f, 1f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.6f, 0.6f, 0.62f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            return colors;
        }

        /// <summary>Rounded button with a centred label. The background image is the raycast / tint target.</summary>
        public static Button CreateButton(Transform parent, string name, string label, int fontSize, UnityAction onClick, out Text labelText)
        {
            var background = CreatePanel(parent, name, UiTheme.Surface, true);
            background.raycastTarget = true;
            var button = background.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = ButtonColors();

            labelText = CreateText(background.transform, "Label", label, fontSize, UiTheme.TextPrimary, TextAnchor.MiddleCenter);
            labelText.rectTransform.Stretch(8f, 8f, 0f, 0f);

            if (onClick != null) button.onClick.AddListener(onClick);
            return button;
        }

        /// <summary>Explicit navigation (deterministic keyboard / gamepad focus moves).</summary>
        public static void SetNavigation(Selectable selectable, Selectable up, Selectable down, Selectable left, Selectable right)
        {
            if (selectable == null) return;
            var nav = new Navigation
            {
                mode = Navigation.Mode.Explicit,
                selectOnUp = up,
                selectOnDown = down,
                selectOnLeft = left,
                selectOnRight = right,
            };
            selectable.navigation = nav;
        }

        // ------------------------------------------------------------------ event system

        /// <summary>
        /// Makes sure the scene has an EventSystem with an input module matching the compiled input backend:
        /// InputSystemUIInputModule (default actions auto-assigned) when the Input System is compiled in, otherwise
        /// StandaloneInputModule. A StandaloneInputModule is replaced when the legacy Input Manager is disabled (it would
        /// throw every frame).
        /// </summary>
        public static EventSystem EnsureEventSystem()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) eventSystem = Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                var go = new GameObject("EventSystem");
                eventSystem = go.AddComponent<EventSystem>();
            }
            EnsureInputModule(eventSystem.gameObject);
            return eventSystem;
        }

        private static void EnsureInputModule(GameObject go)
        {
#if DU_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (go.GetComponent<InputSystemUIInputModule>() != null) return;
            var legacy = go.GetComponent<StandaloneInputModule>();
#if ENABLE_LEGACY_INPUT_MANAGER
            if (legacy != null && legacy.enabled) return; // both backends active: the existing module works
#else
            if (legacy != null)
            {
                legacy.enabled = false;
                Object.Destroy(legacy);
            }
#endif
            go.AddComponent<InputSystemUIInputModule>();
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (go.GetComponent<BaseInputModule>() == null) go.AddComponent<StandaloneInputModule>();
#endif
        }

        /// <summary>Selects <paramref name="target"/> in the current EventSystem (keyboard / gamepad focus).</summary>
        public static void Focus(GameObject target)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null || target == null) return;
            if (eventSystem.alreadySelecting) return;
            eventSystem.SetSelectedGameObject(target);
        }

        /// <summary>Initials for portraits/icons without art: "Rayne" → "R", "Magnetic Pull" → "MP".</summary>
        public static string Initials(string name, int max = 2)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            var chars = new char[max];
            int count = 0;
            bool atWordStart = true;
            for (int i = 0; i < name.Length && count < max; i++)
            {
                char c = name[i];
                if (char.IsWhiteSpace(c) || c == '-' || c == '_')
                {
                    atWordStart = true;
                    continue;
                }
                if (atWordStart && char.IsLetterOrDigit(c)) chars[count++] = char.ToUpperInvariant(c);
                atWordStart = false;
            }
            return count > 0 ? new string(chars, 0, count) : "?";
        }
    }
}
