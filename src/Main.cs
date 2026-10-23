using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityModManagerNet;
using UnityEngine;

namespace HitTextTweaker
{
    /// <summary>
    /// 判定文字改写：把任意 HitMargin 档位的飘字换成自定义内容。
    /// 游戏内 UMM 设置界面可直接改字（改完立即生效，点「保存」写回 config.json）。
    /// 档位名：TooEarly / VeryEarly / EarlyPerfect / Perfect / LatePerfect / VeryLate / TooLate
    ///         FailMiss / FailOverload / Multipress / OverPress
    /// 输入框留空 = 该档位保持游戏原样。
    /// </summary>
    public static class Main
    {
        public static UnityModManager.ModEntry.ModLogger Log;

        private static readonly Dictionary<string, string> replacements =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] Slots = {
            "TooEarly", "VeryEarly", "EarlyPerfect", "Perfect", "LatePerfect", "VeryLate", "TooLate",
            "FailMiss", "FailOverload", "Multipress", "OverPress"
        };

        private static readonly string[] SlotLabels = {
            "太快！！ (TooEarly)",
            "太快！ (VeryEarly)",
            "稍快！ (EarlyPerfect)",
            "完美！ (Perfect)",
            "稍慢！ (LatePerfect)",
            "太慢！ (VeryLate)",
            "太慢！！ (TooLate)",
            "失误…… (FailMiss)",
            "按太快…… (FailOverload)",
            "多次按键！！ (Multipress)",
            "无法再快！！ (OverPress)"
        };

        private static Type hitTextType;
        private static string modDir;
        private static FieldInfo marginField;
        private static FieldInfo textField;
        private static FieldInfo lastColorField;

        // 每档判定文字的颜色（留空 = 游戏原色）
        private static readonly Dictionary<string, Color> colors =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> colorOpen =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // 游戏原色：RDConstants.data.hitMarginColours 各档的运行时颜色，用于默认显示与「恢复游戏原色」
        private static readonly Dictionary<string, Color> gameColors =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private static bool gameColorsLogged = false;

        // 四角渐变：Grad_<slot> = "左上,右上,左下,右下"（8 位 hex）
        private static readonly Dictionary<string, Color[]> gradients =
            new Dictionary<string, Color[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> gradInputs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 渐变方向：H 横向（左→右）、V 纵向（上→下）、C 四角自定义
        private static readonly Dictionary<string, string> gradientModes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 渐变颜色块是否展开 RGBA 滑条（key = slot#idx）
        private static readonly Dictionary<string, bool> gradOpen =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // 逐字彩虹（整体渐变）：每档一个开关，开着时按字符逐个上彩虹色
        private static readonly Dictionary<string, bool> perChar =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // UMM 开关状态：关掉 mod 时 Harmony patch 仍然挂着，必须靠这两个标志跳过替换
        private static bool enabled = true;
        private static UnityModManager.ModEntry umiEntry;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Log = modEntry.Logger;
            modDir = modEntry.Path;
            LoadConfig();
            EnsureGameColors();

            try
            {
                hitTextType = AccessTools.TypeByName("scrHitTextMesh");
                if (hitTextType == null)
                {
                    Log.Log("找不到 scrHitTextMesh，游戏版本不匹配？");
                    return false;
                }

                MethodInfo init = AccessTools.Method(hitTextType, "Init");
                if (init == null)
                {
                    Log.Log("找不到 scrHitTextMesh.Init，游戏版本不匹配？");
                    return false;
                }

                MethodInfo show = AccessTools.Method(hitTextType, "Show");
                if (show == null)
                {
                    Log.Log("找不到 scrHitTextMesh.Show，游戏版本不匹配？");
                    return false;
                }

                marginField = AccessTools.Field(hitTextType, "hitMargin");
                textField = AccessTools.Field(hitTextType, "text");
                lastColorField = AccessTools.Field(hitTextType, "lastColor");
                if (lastColorField == null) Log.Log("警告：找不到 scrHitTextMesh.lastColor，颜色自定义可能不生效");

                var harmony = new Harmony("LittleBlueWhale.HitTextTweaker");
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(Main).GetMethod(
                    "InitPostfix", BindingFlags.Static | BindingFlags.Public)));
                harmony.Patch(show, postfix: new HarmonyMethod(typeof(Main).GetMethod(
                    "ShowPostfix", BindingFlags.Static | BindingFlags.Public)));

                umiEntry = modEntry;
                enabled = modEntry.Active;
                modEntry.OnGUI = OnGUI;
                modEntry.OnSaveGUI = OnGUI;
                modEntry.OnToggle = OnToggle;

                Log.Log("已挂接 scrHitTextMesh.Init + Show（编辑器里也实时换字）| 生效的档位: " + string.Join(", ", new List<string>(replacements.Keys).ToArray()));
                Log.Log("颜色自定义档位: " + string.Join(", ", new List<string>(colors.Keys).ToArray()));
                foreach (KeyValuePair<string, string> kv in replacements)
                    Log.Log("  " + kv.Key + " -> " + kv.Value);
                Log.Log("设置界面：UMM 列表里点本 mod 的齿轮/设置按钮");
                return true;
            }
            catch (Exception e)
            {
                Log.Log("加载失败: " + e);
                return false;
            }
        }

        public static bool OnToggle(UnityModManager.ModEntry entry, bool value)
        {
            enabled = value;
            if (Log != null) Log.Log("mod 开关：" + (value ? "启用" : "禁用，判定文字恢复游戏原样"));
            return true;
        }

        public static void InitPostfix(object __instance)
        {
            ApplyText(__instance);
            ApplyColor(__instance);
        }

        // 判定飘字每次显示都会走 Show，这里再刷一次：改了字不用重开谱子，编辑器里也实时生效
        public static void ShowPostfix(object __instance)
        {
            ApplyText(__instance);
            ApplyColor(__instance);
        }

        private static void ApplyText(object instance)
        {
            // 关掉 mod 后 patch 还挂着：先看开关状态，禁用时完全不碰文字
            if (!enabled) return;
            if (umiEntry != null && !umiEntry.Active) return;
            try
            {
                if (instance == null || marginField == null || textField == null) return;

                object margin = marginField.GetValue(instance);
                if (margin == null) return;

                string slot = margin.ToString();
                string want;
                if (!replacements.TryGetValue(slot, out want)) return;
                if (string.IsNullOrEmpty(want)) return;
                want = WrapGradient(want, slot);

                var tmp = textField.GetValue(instance) as TextMeshPro;
                if (tmp == null) return;
                if (tmp.text == want) return;

                tmp.overflowMode = TextOverflowModes.Overflow;
                try
                {
                    PropertyInfo wrap = tmp.GetType().GetProperty("textWrappingMode");
                    if (wrap != null && wrap.CanWrite)
                        wrap.SetValue(tmp, Enum.Parse(wrap.PropertyType, "NoWrap"));
                    else
                    {
                        PropertyInfo oldWrap = tmp.GetType().GetProperty("enableWordWrapping");
                        if (oldWrap != null && oldWrap.CanWrite) oldWrap.SetValue(tmp, false);
                    }
                }
                catch { }

                tmp.text = want;
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("ApplyText 异常: " + e.Message);
            }
        }

        // 颜色覆盖：Init 里游戏按档位从 RDConstants.hitMarginColours 取色写进 lastColor，
        // Show 时又拿 lastColor 重新上色，所以两处都得改 lastColor + text.color 才真正生效。
        private static void ApplyColor(object instance)
        {
            if (!enabled) return;
            if (umiEntry != null && !umiEntry.Active) return;
            try
            {
                if (instance == null || marginField == null || textField == null || lastColorField == null) return;

                object margin = marginField.GetValue(instance);
                if (margin == null) return;

                string slot = margin.ToString();
                var tmp = textField.GetValue(instance) as TextMeshPro;
                if (tmp == null) return;

                // 渐变优先：四角渐变打开时颜色由 colorGradient 决定
                Color[] g;
                if (gradients.TryGetValue(slot, out g) && g != null && g.Length == 4)
                {
                    bool pc;
                    perChar.TryGetValue(slot, out pc);
                    if (pc)
                    {
                        // 横向整体渐变：颜色由 ApplyText 生成的 <color> 富文本控制
                        // 纵向/四角：整段没法按行插值（文字只有一行），退化成每个字内部四角渐变
                        string gm;
                        gradientModes.TryGetValue(slot, out gm);
                        if (gm == "V" || gm == "C")
                        {
                            tmp.enableVertexGradient = true;
                            tmp.colorGradient = new VertexGradient(g[0], g[1], g[2], g[3]);
                            float a3 = tmp.color.a;
                            tmp.color = new Color(1f, 1f, 1f, a3);
                            lastColorField.SetValue(instance, Color.white);
                            return;
                        }
                        tmp.enableVertexGradient = false;
                        float alpha = tmp.color.a;
                        tmp.color = new Color(1f, 1f, 1f, alpha);
                        lastColorField.SetValue(instance, Color.white);
                        return;
                    }
                    tmp.enableVertexGradient = true;
                    tmp.colorGradient = new VertexGradient(g[0], g[1], g[2], g[3]);
                    float alpha2 = tmp.color.a;
                    tmp.color = new Color(1f, 1f, 1f, alpha2);
                    lastColorField.SetValue(instance, Color.white);
                    return;
                }
                if (tmp.enableVertexGradient) tmp.enableVertexGradient = false;

                Color want;
                if (!colors.TryGetValue(slot, out want)) return;

                // 保留当前 alpha（淡出动画由游戏的 DOFade 控制），只替换 RGB
                Color c = want;
                c.a = tmp.color.a;
                tmp.color = c;
                lastColorField.SetValue(instance, want);
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("ApplyColor 异常: " + e.Message);
            }
        }

        private static void DrawColorEditor(string slot)
        {
            Color c;
            if (!colors.TryGetValue(slot, out c)) c = OriginalColor(slot);
            Color n = c;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("颜色（0.00 ~ 1.00，可拖滑条或填数值）");
            n.r = ChannelRow("R", c.r);
            n.g = ChannelRow("G", c.g);
            n.b = ChannelRow("B", c.b);
            n.a = ChannelRow("A", c.a);
            if (n != c) colors[slot] = n;

            GUILayout.BeginHorizontal();
            GUILayout.Label("预览:", GUILayout.Width(40f));
            Rect bar = GUILayoutUtility.GetRect(120f, 20f, GUILayout.Width(120f), GUILayout.Height(20f));
            Color oldCol = GUI.color;
            GUI.color = n;
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = oldCol;
            GUILayout.EndHorizontal();

            if (GUILayout.Button("恢复游戏原色", GUILayout.Width(160f)))
            {
                colors[slot] = OriginalColor(slot);
                SaveConfig();
            }

            Color[] g;
            bool hasGrad = gradients.TryGetValue(slot, out g) && g != null && g.Length == 4;
            GUILayout.Space(3f);
            if (GUILayout.Button(hasGrad ? "关闭渐变色" : "开启渐变色", GUILayout.Width(160f)))
            {
                if (hasGrad) { gradients.Remove(slot); gradientModes.Remove(slot); }
                else
                {
                    Color c0 = hasGrad ? g[0] : c;
                    gradients[slot] = new Color[] { c0, c0, c0, c0 };
                }
                SaveConfig();
            }
            Color[] gv;
            if (gradients.TryGetValue(slot, out gv) && gv != null && gv.Length == 4)
            {
                bool pc;
                perChar.TryGetValue(slot, out pc);
                string gradBtn = pc ? "整体渐变：开（整个文本渐变）" : "整体渐变：关（每个字内部渐变）";
                if (GUILayout.Button(gradBtn, GUILayout.Width(300f)))
                {
                    perChar[slot] = !pc;
                    SaveConfig();
                }
                string mode;
                gradientModes.TryGetValue(slot, out mode);
                if (mode != "H" && mode != "V" && mode != "C") mode = "H";

                GUILayout.BeginHorizontal();
                if (GUILayout.Toggle(mode == "H", "横向（左→右）", GUILayout.Width(140f))) mode = "H";
                if (GUILayout.Toggle(mode == "V", "纵向（上→下）", GUILayout.Width(140f))) mode = "V";
                if (GUILayout.Toggle(mode == "C", "四角", GUILayout.Width(70f))) mode = "C";
                GUILayout.EndHorizontal();
                gradientModes[slot] = mode;

                if (mode == "H")
                {
                    gv[0] = gv[2] = DrawGradColorBlock(slot, 0, gv[0], "左色");
                    gv[1] = gv[3] = DrawGradColorBlock(slot, 1, gv[1], "右色");
                }
                else if (mode == "V")
                {
                    gv[0] = gv[1] = DrawGradColorBlock(slot, 0, gv[0], "上色");
                    gv[2] = gv[3] = DrawGradColorBlock(slot, 2, gv[2], "下色");
                }
                else
                {
                    string[] cornerNames = { "左上", "右上", "左下", "右下" };
                    for (int k = 0; k < 4; k++) gv[k] = DrawGradColorBlock(slot, k, gv[k], cornerNames[k]);
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("渐变预览:", GUILayout.Width(66f));
                if (mode == "H" || mode == "V")
                {
                    Color from = mode == "H" ? gv[0] : gv[0];
                    Color to = mode == "H" ? gv[1] : gv[2];
                    for (int k = 0; k < 8; k++)
                    {
                        Rect seg = GUILayoutUtility.GetRect(15f, 20f, GUILayout.Width(15f), GUILayout.Height(20f));
                        Color old2 = GUI.color;
                        GUI.color = Color.Lerp(from, to, k / 7f);
                        GUI.DrawTexture(seg, Texture2D.whiteTexture);
                        GUI.color = old2;
                    }
                }
                else
                {
                    for (int k = 0; k < 4; k++)
                    {
                        Rect seg = GUILayoutUtility.GetRect(30f, 20f, GUILayout.Width(30f), GUILayout.Height(20f));
                        Color old2 = GUI.color;
                        GUI.color = gv[k];
                        GUI.DrawTexture(seg, Texture2D.whiteTexture);
                        GUI.color = old2;
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private static Color DrawGradColorBlock(string slot, int idx, Color cur, string label)
        {
            string key = slot + "#" + idx;
            bool open;
            gradOpen.TryGetValue(key, out open);

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(48f));
            Rect sw = GUILayoutUtility.GetRect(24f, 20f, GUILayout.Width(24f), GUILayout.Height(20f));
            Color old = GUI.color;
            GUI.color = cur;
            GUI.DrawTexture(sw, Texture2D.whiteTexture);
            GUI.color = old;
            if (GUILayout.Button(open ? "收起" : "调色", GUILayout.Width(56f)))
            {
                open = !open;
                gradOpen[key] = open;
            }
            GUILayout.EndHorizontal();

            if (open)
            {
                Color n = cur;
                n.r = ChannelRow("R", n.r);
                n.g = ChannelRow("G", n.g);
                n.b = ChannelRow("B", n.b);
                n.a = ChannelRow("A", n.a);
                cur = n;
            }
            return cur;
        }

        private static Color ParseHex(string hex)
        {
            Color c;
            if (TryParseColor(hex, out c)) return c;
            return Color.white;
        }

        // 整体渐变：把文本按字逐个包 <color> 标签，让 TMP 自己渲染颜色（比改 mesh 顶点色稳，淡出不留残影）
        private static string WrapGradient(string text, string slot)
        {
            bool pc;
            if (!perChar.TryGetValue(slot, out pc) || !pc) return text;
            Color[] g;
            if (!gradients.TryGetValue(slot, out g) || g == null || g.Length != 4) return text;
            string gm;
            gradientModes.TryGetValue(slot, out gm);
            if (gm == "V" || gm == "C") return text; // 纵向/四角交给四角渐变（单行文字没法按行插值）
            Color ga = g[0], gb = g[1];

            var sb = new System.Text.StringBuilder();
            int n = text.Length;
            for (int i = 0; i < n; i++)
            {
                char ch = text[i];
                if (ch == '\n' || ch == '\r') { sb.Append(ch); continue; }
                Color c = Color.Lerp(ga, gb, n > 1 ? (float)i / (n - 1) : 0.5f);
                sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGBA(c)).Append(">").Append(ch).Append("</color>");
            }
            return sb.ToString();
        }

        private static float ChannelRow(string name, float v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, GUILayout.Width(18f));
            float n = GUILayout.HorizontalSlider(v, 0f, 1f, GUILayout.Width(170f));
            string s = GUILayout.TextField(n.ToString("0.00", CultureInfo.InvariantCulture), GUILayout.Width(58f));
            float parsed;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                n = Mathf.Clamp01(parsed);
            GUILayout.EndHorizontal();
            return n;
        }

        // 读取游戏默认判定颜色（RDConstants.data.hitMarginColours），读不到时回落白色
        private static void EnsureGameColors()
        {
            if (gameColors.Count > 0) return;
            try
            {
                Type rdType = Type.GetType("RDConstants, Assembly-CSharp");
                if (rdType == null) { LogOnce("读取游戏原色：找不到 RDConstants"); return; }
                PropertyInfo dataProp = rdType.GetProperty("data", BindingFlags.Public | BindingFlags.Static);
                object data = dataProp != null ? dataProp.GetValue(null) : null;
                if (data == null) { LogOnce("读取游戏原色：RDConstants.data 为空（可能加载太早，打开面板时会再试）"); return; }
                FieldInfo hmcField = rdType.GetField("hitMarginColours", BindingFlags.Public | BindingFlags.Instance);
                object scheme = hmcField != null ? hmcField.GetValue(data) : null;
                if (scheme == null) { LogOnce("读取游戏原色：hitMarginColours 为空"); return; }
                Type st = scheme.GetType();
                // 与 Slots 一一对应（OverPress 与游戏一致用 colourFail）
                string[] fieldNames = {
                    "colourTooEarly", "colourVeryEarly", "colourLittleEarly", "colourPerfect",
                    "colourLittleLate", "colourVeryLate", "colourTooLate", "colourFail",
                    "colourFail", "colourMultipress", "colourFail"
                };
                for (int i = 0; i < Slots.Length && i < fieldNames.Length; i++)
                {
                    FieldInfo f = st.GetField(fieldNames[i], BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;
                    gameColors[Slots[i]] = (Color)f.GetValue(scheme);
                }
                if (gameColors.Count > 0 && !gameColorsLogged)
                {
                    gameColorsLogged = true;
                    foreach (string slot in Slots)
                    {
                        Color c;
                        if (gameColors.TryGetValue(slot, out c))
                            Log.Log("游戏原色 " + slot + " = " + ColorUtility.ToHtmlStringRGBA(c));
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce("读取游戏原色异常: " + e.Message);
            }
        }

        private static void LogOnce(string msg)
        {
            if (gameColorsLogged) return;
            gameColorsLogged = true;
            if (Log != null) Log.Log(msg);
        }

        private static Color OriginalColor(string slot)
        {
            EnsureGameColors();
            Color c;
            return gameColors.TryGetValue(slot, out c) ? c : Color.white;
        }

        private static bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 3 && s.Length != 6 && s.Length != 8) return false;
            return ColorUtility.TryParseHtmlString("#" + s, out c);
        }

        // ---------- 设置界面 ----------

        public static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            try
            {
                GUILayout.Label("判定文字：改完立即生效（下一次判定就变），点「保存」写入 config.json，重启后保持。");
                GUILayout.Label("文字框留空 = 该档位保持游戏原样；点「调色」展开 RGB(A) 滑条（0.00~1.00），默认值 = 游戏原色，想要纯白就填 1/1/1/1。");
                GUILayout.Space(4f);

                for (int i = 0; i < Slots.Length; i++)
                {
                    string slot = Slots[i];
                    string current;
                    if (!replacements.TryGetValue(slot, out current)) current = "";

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(SlotLabels[i], GUILayout.Width(170f));

                    string v = GUILayout.TextField(current, GUILayout.Width(160f));
                    if (v != current)
                    {
                        if (string.IsNullOrEmpty(v)) replacements.Remove(slot);
                        else replacements[slot] = v;
                    }

                    Color sw;
                    if (!colors.TryGetValue(slot, out sw)) sw = OriginalColor(slot);
                    Rect swatch = GUILayoutUtility.GetRect(24f, 24f, GUILayout.Width(24f), GUILayout.Height(24f));
                    Color oldCol = GUI.color;
                    GUI.color = sw;
                    GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                    GUI.color = oldCol;

                    bool open;
                    colorOpen.TryGetValue(slot, out open);
                    if (GUILayout.Button(open ? "收起" : "调色", GUILayout.Width(64f)))
                    {
                        colorOpen[slot] = !open;
                        open = !open;
                    }
                    if (GUILayout.Button("清空", GUILayout.Width(64f)))
                    {
                        replacements.Remove(slot);
                        colors.Remove(slot);
                        colorOpen[slot] = false;
                        open = false;
                    }
                    GUILayout.EndHorizontal();

                    if (open) DrawColorEditor(slot);
                }

                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("保存", GUILayout.Width(110f))) SaveConfig();
                GUILayout.EndHorizontal();
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("OnGUI 异常: " + e.Message);
            }
        }

        private static void SaveConfig()
        {
            try
            {
                if (string.IsNullOrEmpty(modDir)) return;
                string path = Path.Combine(modDir, "config.json");
                var sb = new System.Text.StringBuilder();
                sb.Append("{\n");
                for (int i = 0; i < Slots.Length; i++)
                {
                    string v;
                    if (!replacements.TryGetValue(Slots[i], out v) || string.IsNullOrEmpty(v)) continue;
                    sb.Append("  \"").Append(Slots[i]).Append("\": \"").Append(JsonEscape(v)).Append("\",\n");
                }
                for (int i = 0; i < Slots.Length; i++)
                {
                    Color c;
                    if (!colors.TryGetValue(Slots[i], out c)) continue;
                    sb.Append("  \"Color_").Append(Slots[i]).Append("\": \"").Append(ColorUtility.ToHtmlStringRGBA(c)).Append("\",\n");
                }
                for (int i = 0; i < Slots.Length; i++)
                {
                    Color[] g;
                    if (!gradients.TryGetValue(Slots[i], out g) || g == null || g.Length != 4) continue;
                    sb.Append("  \"Grad_").Append(Slots[i]).Append("\": \"")
                      .Append(ColorUtility.ToHtmlStringRGBA(g[0])).Append(",")
                      .Append(ColorUtility.ToHtmlStringRGBA(g[1])).Append(",")
                      .Append(ColorUtility.ToHtmlStringRGBA(g[2])).Append(",")
                      .Append(ColorUtility.ToHtmlStringRGBA(g[3])).Append("\",\n");
                    string gm;
                    if (gradientModes.TryGetValue(Slots[i], out gm) && !string.IsNullOrEmpty(gm))
                        sb.Append("  \"GradDir_").Append(Slots[i]).Append("\": \"").Append(gm).Append("\",\n");
                    bool pc;
                    if (perChar.TryGetValue(Slots[i], out pc) && pc)
                        sb.Append("  \"GradPerChar_").Append(Slots[i]).Append("\": \"1\",\n");
                }
                sb.Append("  \"__comment\": \"档位留空不写，就是游戏原样\"\n}\n");
                File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                Log.Log("已保存 config.json");
            }
            catch (Exception e)
            {
                Log.Log("保存失败: " + e.Message);
            }
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void LoadConfig()
        {
            try
            {
                if (string.IsNullOrEmpty(modDir)) return;
                string path = Path.Combine(modDir, "config.json");
                if (!File.Exists(path)) return;
                string s = File.ReadAllText(path);
                MatchCollection ms = Regex.Matches(s, "\"([A-Za-z_]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                foreach (Match m in ms)
                {
                    string key = m.Groups[1].Value;
                    if (key == "__comment") continue;
                    string val = Regex.Replace(m.Groups[2].Value, "\\\\(.)", "$1");
                    if (string.IsNullOrEmpty(val)) continue;
                    if (key.StartsWith("Color_", StringComparison.OrdinalIgnoreCase))
                    {
                        Color c;
                        if (TryParseColor(val, out c)) colors[key.Substring(6)] = c;
                        continue;
                    }
                    if (key.StartsWith("Grad_", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = val.Split(',');
                        if (parts.Length == 4)
                        {
                            var arr = new Color[4];
                            bool ok = true;
                            for (int k = 0; k < 4; k++)
                            {
                                if (!TryParseColor(parts[k], out arr[k])) { ok = false; break; }
                            }
                            if (ok) gradients[key.Substring(5)] = arr;
                        }
                        continue;
                    }
                    if (key.StartsWith("GradDir_", StringComparison.OrdinalIgnoreCase))
                    {
                        gradientModes[key.Substring(8)] = val.Trim();
                        continue;
                    }
                    if (key.StartsWith("GradPerChar_", StringComparison.OrdinalIgnoreCase))
                    {
                        perChar[key.Substring(12)] = (val.Trim() == "1");
                        continue;
                    }
                    replacements[key] = val;
                }
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("读取 config.json 失败，用默认值: " + e.Message);
            }
        }
    }
}
