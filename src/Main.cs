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

        // r150（2026-10-30 之后的 build）把原来的 Perfect 拆成了 PerfectMinus / XPerfect / PerfectPlus，
        // 旧名 Perfect 在枚举里已经不存在，这里按新名列出；old config 里的 Perfect 由 MigrateLegacyPerfect() 自动摊过去
        // r150 之后游戏又加了「X完美判定文本」预设：选「仅完美」时三个完美档会被合并成同一条「完美！」，
        // 这时改用 PerfectCombined 这一档的文字（老配置里没有就自动从 XPerfect 拷一份）
        private const string CombinedSlot = "PerfectCombined";

        private static readonly string[] Slots = {
            "TooEarly", "VeryEarly", "EarlyPerfect", "PerfectMinus", "XPerfect", "PerfectPlus",
            CombinedSlot,
            "LatePerfect", "VeryLate", "TooLate",
            "FailMiss", "FailOverload", "Multipress", "OverPress"
        };

        private static readonly string[] SlotLabels = {
            "太快！！ (TooEarly)",
            "太快！ (VeryEarly)",
            "稍快！ (EarlyPerfect)",
            "稍快完美 -完美！ (PerfectMinus)",
            "X完美！ (XPerfect)",
            "稍慢完美 完美！+ (PerfectPlus)",
            "完美！合并时 (PerfectCombined)",
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
        private static FieldInfo borderField;          // scrHitTextMesh.xPerfectBorder（X 完美的描边层）

        // 游戏设置「X完美判定文本」：Persistence.hitMarginPerfectText（静态属性，枚举类型）
        private static PropertyInfo presetProp;
        private static MethodInfo locGet;              // RDString.Get(string, Dictionary)

        // 游戏设置界面：面板里改完预设后，顺手把「设置 → 玩法」里那一行也刷新，不然它会留旧文案
        private static Type settingsMenuType;
        private static MethodInfo updateSettingMethod;
        private static PropertyInfo currentSettingsProp;
        private static FieldInfo settingTypeField;
        private static FieldInfo valueLabelField;      // PauseSettingButton.valueLabel（那一行右侧显示的值）
        private static string locPerfectPlain;         // 「完美！」的当前语言文本

        private static readonly string[] PresetNames = {
            "仅完美",
            "X完美与-完美！+",
            "X完美（无粒子）与-完美！+",
            "X完美",
            "X完美（无粒子）",
            "-完美！+"
        };

        // 与上面一一对应，游戏本地化键 pauseMenu.settings.HitMarginPerfectTextPreset.<成员名>
        private static readonly string[] PresetEnumNames = {
            "Default",
            "ShowXPerfectWithParticleAndSignedPerfects",
            "ShowXPerfectAndSignedPerfects",
            "ShowXPerfectWithParticle",
            "ShowXPerfect",
            "ShowSignedPerfects"
        };

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

        // 关掉渐变色时把原来的四角颜色缓存在这里，重新开启就还原，而不是退回纯色
        private static readonly Dictionary<string, Color[]> gradStash =
            new Dictionary<string, Color[]>(StringComparer.OrdinalIgnoreCase);
        private static bool pendingSave = false;

        // 渐变颜色块是否展开 RGBA 滑条（key = slot#idx）
        private static readonly Dictionary<string, bool> gradOpen =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // 逐字彩虹（整体渐变）：每档一个开关，开着时按字符逐个上彩虹色
        private static readonly Dictionary<string, bool> perChar =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // 每档第一次真正把字换掉时记一条日志，方便更新后一眼看出到底生效没有
        private static readonly HashSet<string> appliedSlots = new HashSet<string>();

        // UMM 开关状态：关掉 mod 时 Harmony patch 仍然挂着，必须靠这两个标志跳过替换
        private static bool enabled = true;
        private static UnityModManager.ModEntry umiEntry;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Log = modEntry.Logger;
            modDir = modEntry.Path;
            LoadConfig();
            MigrateLegacyPerfect();
            MigrateCombinedPerfect();
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
                borderField = AccessTools.Field(hitTextType, "xPerfectBorder");
                if (lastColorField == null) Log.Log("警告：找不到 scrHitTextMesh.lastColor，颜色自定义可能不生效");

                try
                {
                    Type persistenceType = AccessTools.TypeByName("Persistence");
                    if (persistenceType != null)
                        presetProp = persistenceType.GetProperty("hitMarginPerfectText",
                            BindingFlags.Public | BindingFlags.Static);
                    if (presetProp == null) Log.Log("找不到 Persistence.hitMarginPerfectText，面板里的预设选择失效（换字仍按旧逻辑）");
                }
                catch (Exception e)
                {
                    Log.Log("读取 X完美判定文本 设置失败: " + e.Message);
                }
                ResolveLocalization();

                settingsMenuType = AccessTools.TypeByName("SettingsMenu");
                if (settingsMenuType != null)
                {
                    updateSettingMethod = settingsMenuType.GetMethod("UpdateSetting",
                        BindingFlags.Public | BindingFlags.Instance);
                    // currentSettings 是 private 属性，漏了 NonPublic 就会默默返回 null，同步永远不生效
                    currentSettingsProp = settingsMenuType.GetProperty("currentSettings",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    Type psbType = AccessTools.TypeByName("PauseSettingButton");
                    if (psbType != null)
                    {
                        settingTypeField = psbType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                        valueLabelField = psbType.GetField("valueLabel", BindingFlags.Public | BindingFlags.Instance);
                    }
                    if (updateSettingMethod == null || currentSettingsProp == null)
                        Log.Log("找不到 SettingsMenu.UpdateSetting / currentSettings，面板改动无法同步到游戏设置页");
                }

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
            Refresh(__instance);
        }

        // 判定飘字每次显示都会走 Show，这里再刷一次：改了字不用重开谱子，编辑器里也实时生效
        public static void ShowPostfix(object __instance)
        {
            Refresh(__instance);
        }

        private static void Refresh(object instance)
        {
            // 合并判断必须在改字之前做：改完之后 text 已经是自定义内容，认不出是不是「完美！」了
            string rawSlot;
            string slot = ResolveSlot(instance, out rawSlot);
            if (slot == null) return;
            ApplyText(instance, slot, rawSlot);
            ApplyColor(instance, slot, rawSlot);
        }

        // 当前这一档该用哪份文字：正常就是档位自己；被游戏的「X完美判定文本」合并成「完美！」时用合并档
        private static string ResolveSlot(object instance, out string rawSlot)
        {
            rawSlot = null;
            if (!enabled || instance == null || marginField == null) return null;
            if (umiEntry != null && !umiEntry.Active) return null;

            object margin;
            try { margin = marginField.GetValue(instance); }
            catch { return null; }
            if (margin == null) return null;
            rawSlot = margin.ToString();
            if (!IsPerfectSlot(rawSlot)) return rawSlot;

            if (locGet == null) return rawSlot;
            TextMeshPro tmp = null;
            try { if (textField != null) tmp = textField.GetValue(instance) as TextMeshPro; } catch { }
            if (tmp == null) return rawSlot;

            string plain = locPerfectPlain;
            string own = GetLocalized("HitMargin." + rawSlot);
            if (string.IsNullOrEmpty(plain) || string.IsNullOrEmpty(own) || own == plain) return rawSlot;
            return StripTags(tmp.text) == plain ? CombinedSlot : rawSlot;
        }

        // 当前预设下游戏到底会不会显示这一档：不显示的行在面板里灰掉，免得白改
        private static bool SlotActive(string slot)
        {
            int pv = GetPresetValue();
            if (pv < 0) return true;             // 老版本没有预设，全都显示
            if (slot == CombinedSlot) return pv == 0 || pv >= 3;
            if (slot == "XPerfect") return pv == 1 || pv == 2 || pv == 3 || pv == 4;
            if (slot == "PerfectMinus" || slot == "PerfectPlus") return pv == 1 || pv == 2 || pv == 5;
            return true;
        }

        private static bool IsPerfectSlot(string slot)
        {
            return slot == "PerfectMinus" || slot == "XPerfect" || slot == "PerfectPlus";
        }

        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Regex.Replace(s, "<[^>]*>", "").Trim();
        }

        private static void ApplyText(object instance, string slot, string rawSlot)
        {
            try
            {
                if (instance == null || textField == null) return;

                string want;
                if (!replacements.TryGetValue(slot, out want)) return;
                if (want == null) return;
                if (want.Length == 0)
                {
                    // 框里一个字都没有 = 这一档什么都不显示（跟「留空就是游戏原样」不是一回事）
                    var blank = textField.GetValue(instance) as TextMeshPro;
                    if (blank != null && blank.text != "") blank.text = "";
                    SyncBorder(instance, "");
                    if (appliedSlots.Add(slot) && Log != null)
                        Log.Log("文字已清空（这一档不显示任何字）: " + slot);
                    return;
                }
                want = WrapGradient(want, slot);

                var tmp = textField.GetValue(instance) as TextMeshPro;
                if (tmp == null) return;
                if (tmp.text == want) { SyncBorder(instance, want); return; }

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
                SyncBorder(instance, want);
                if (appliedSlots.Add(slot) && Log != null)
                    Log.Log("文字替换已生效: " + slot + " -> " + want);
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("ApplyText 异常: " + e.Message);
            }
        }

        // X 完美的描边层是游戏在 Show 里照抄一份当时的 text，我们的 postfix 后跑，
        // 所以换完字要顺手把它同步一遍，不然边框上还留着旧文案
        private static void SyncBorder(object instance, string want)
        {
            if (borderField == null) return;
            try
            {
                var border = borderField.GetValue(instance) as TextMeshPro;
                if (border != null && border.text != want) border.text = want;
            }
            catch { }
        }

        // 颜色覆盖：Init 里游戏按档位从 RDConstants.hitMarginColours 取色写进 lastColor，
        // Show 时又拿 lastColor 重新上色，所以两处都得改 lastColor + text.color 才真正生效。
        private static void ApplyColor(object instance, string effSlot, string rawSlot)
        {
            try
            {
                if (instance == null || textField == null || lastColorField == null) return;

                var tmp = textField.GetValue(instance) as TextMeshPro;
                if (tmp == null) return;

                // 合并档优先用它自己的配色；没配就退回当前判定档自己的颜色
                string slot = effSlot;
                if (slot != rawSlot
                    && !colors.ContainsKey(slot) && !gradients.ContainsKey(slot) && !perChar.ContainsKey(slot))
                    slot = rawSlot;

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

        // 色块预览：有渐变色就按渐变画（横/纵/四角），没有就画纯色
        private static void DrawSlotPreview(Rect r, string slot)
        {
            Color old = GUI.color;
            Color[] g;
            bool hasGrad = gradients.TryGetValue(slot, out g) && g != null && g.Length == 4;
            if (!hasGrad)
            {
                Color c;
                if (!colors.TryGetValue(slot, out c)) c = OriginalColor(slot);
                GUI.color = c;
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = old;
                return;
            }

            string mode;
            gradientModes.TryGetValue(slot, out mode);
            if (mode != "H" && mode != "V" && mode != "C") mode = "H";

            const int N = 8;
            if (mode == "C")
            {
                // 四角 = 双线性插值：v=0 是上边，v=1 是下边；g[0]/g[1] 左上/右上，g[2]/g[3] 左下/右下
                float cw = Mathf.Ceil(r.width / N);
                float ch = Mathf.Ceil(r.height / N);
                for (int y = 0; y < N; y++)
                {
                    for (int x = 0; x < N; x++)
                    {
                        float u = (x + 0.5f) / N;
                        float v = (y + 0.5f) / N;
                        Color top = Color.Lerp(g[0], g[1], u);
                        Color bottom = Color.Lerp(g[2], g[3], u);
                        GUI.color = Color.Lerp(top, bottom, v);
                        GUI.DrawTexture(new Rect(r.x + r.width * x / N, r.y + r.height * y / N, cw, ch),
                            Texture2D.whiteTexture);
                    }
                }
            }
            else
            {
                bool horiz = mode == "H";
                Color from = g[0];
                Color to = horiz ? g[1] : g[2];
                float w = Mathf.Ceil(r.width / N);
                float h = Mathf.Ceil(r.height / N);
                for (int k = 0; k < N; k++)
                {
                    GUI.color = Color.Lerp(from, to, k / (N - 1f));
                    if (horiz) GUI.DrawTexture(new Rect(r.x + r.width * k / N, r.y, w, r.height), Texture2D.whiteTexture);
                    else GUI.DrawTexture(new Rect(r.x, r.y + r.height * k / N, r.width, h), Texture2D.whiteTexture);
                }
            }
            GUI.color = old;
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
            DrawSlotPreview(bar, slot);
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
                if (hasGrad)
                {
                    // 关闭只是停用，把四角颜色存起来（同时写进 config），下次开启原样还原
                    gradStash[slot] = new Color[] { g[0], g[1], g[2], g[3] };
                    gradients.Remove(slot);
                }
                else
                {
                    Color[] seed;
                    if (gradStash.TryGetValue(slot, out seed) && seed != null && seed.Length == 4)
                        gradients[slot] = new Color[] { seed[0], seed[1], seed[2], seed[3] };
                    else
                        gradients[slot] = new Color[] { c, c, c, c };
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
                    "colourXPerfect", "colourPerfect",
                    "colourPerfect",
                    "colourLittleLate", "colourVeryLate", "colourTooLate",
                    "colourFail", "colourFail", "colourMultipress", "colourFail"
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
                string sigBefore = ConfigSignature();
                GUILayout.Label("判定文字：改完立即生效（下一次判定就变），改完自动写入 config.json，重启后保持。");
                GUILayout.Label("文字框默认显示游戏自己的文本；把字全删掉 = 这一档什么都不显示；点「恢复默认」回到游戏原样。点「调色」展开 RGB(A) 滑条（0.00~1.00），默认值 = 游戏原色，想要纯白就填 1/1/1/1。");
                GUILayout.Space(6f);

                GUILayout.BeginHorizontal();
                GUILayout.Label("X完美判定文本", GUILayout.Width(130f));
                int pv = GetPresetValue();
                if (presetProp == null || pv < 0)
                {
                    GUILayout.Label("（当前游戏版本没有这个设置项）");
                }
                else
                {
                    if (GUILayout.Button("◀", GUILayout.Width(30f)))
                        SetPresetValue((pv + PresetNames.Length - 1) % PresetNames.Length);
                    GUILayout.Label(PresetNames[Mathf.Clamp(pv, 0, PresetNames.Length - 1)], GUILayout.Width(200f));
                    if (GUILayout.Button("▶", GUILayout.Width(30f)))
                        SetPresetValue((pv + 1) % PresetNames.Length);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label("跟游戏「设置」里的同名选项是同一个值，两边同步。灰色行 = 当前预设不显示该档，改了也不会出现。");
                GUILayout.Space(6f);

                for (int i = 0; i < Slots.Length; i++)
                {
                    string slot = Slots[i];
                    string def = DefaultText(slot);
                    string current;
                    if (!replacements.TryGetValue(slot, out current)) current = def;
                    if (current == null) current = "";

                    bool active = SlotActive(slot);
                    bool prevEnabled = GUI.enabled;
                    if (!active) GUI.enabled = false;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(SlotLabels[i], GUILayout.Width(170f));

                    string v = GUILayout.TextField(current, GUILayout.Width(160f));
                    if (v != current)
                    {
                        // 跟游戏默认文本一样就当没改（不写配置）；删到空字符串才是「什么都不显示」
                        if (v == def) replacements.Remove(slot);
                        else replacements[slot] = v;
                    }

                    Rect swatch = GUILayoutUtility.GetRect(24f, 24f, GUILayout.Width(24f), GUILayout.Height(24f));
                    DrawSlotPreview(swatch, slot);

                    bool open;
                    colorOpen.TryGetValue(slot, out open);
                    if (GUILayout.Button(open ? "收起" : "调色", GUILayout.Width(64f)))
                    {
                        colorOpen[slot] = !open;
                        open = !open;
                    }
                    if (GUILayout.Button("恢复默认", GUILayout.Width(72f)))
                    {
                        replacements.Remove(slot);
                        colors.Remove(slot);
                        gradients.Remove(slot);
                        gradStash.Remove(slot);
                        gradientModes.Remove(slot);
                        perChar.Remove(slot);
                        colorOpen[slot] = false;
                        open = false;
                    }
                    GUILayout.EndHorizontal();

                    if (open) DrawColorEditor(slot);
                    GUI.enabled = prevEnabled;
                }

                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("保存", GUILayout.Width(110f))) SaveConfig();
                GUILayout.EndHorizontal();

                // 滑条/输入框改完松手就落盘，不点「保存」也不会丢
                if (ConfigSignature() != sigBefore) pendingSave = true;
                if (pendingSave && Event.current != null
                    && (Event.current.type == EventType.MouseUp || Event.current.type == EventType.KeyUp))
                {
                    SaveConfig();
                    pendingSave = false;
                }
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
                    if (!replacements.TryGetValue(Slots[i], out v) || v == null) continue;
                    // 空字符串是显式配置：表示这一档不显示任何字，必须写进去
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
                }
                // 关掉渐变后的缓存颜色也写进去，重新开启时还原
                for (int i = 0; i < Slots.Length; i++)
                {
                    Color[] st;
                    if (!gradStash.TryGetValue(Slots[i], out st) || st == null || st.Length != 4) continue;
                    if (gradients.ContainsKey(Slots[i])) continue;
                    sb.Append("  \"GradStash_").Append(Slots[i]).Append("\": \"")
                      .Append(ColorUtility.ToHtmlStringRGBA(st[0])).Append(",")
                      .Append(ColorUtility.ToHtmlStringRGBA(st[1])).Append(",")
                      .Append(ColorUtility.ToHtmlStringRGBA(st[2])).Append(",")
                      .Append(ColorUtility.ToHtmlStringRGBA(st[3])).Append("\",\n");
                }
                // 方向 / 整体渐变开关跟渐变颜色分开存：关掉渐变色时也保留，重开直接沿用
                for (int i = 0; i < Slots.Length; i++)
                {
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

        // 面板内容指纹：用来判断这一帧有没有改过东西（改了才在松手时落盘）
        private static string ConfigSignature()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Slots.Length; i++)
            {
                string s = Slots[i];
                string v;
                if (replacements.TryGetValue(s, out v)) sb.Append(s).Append('=').Append(v).Append(';');
                Color c;
                if (colors.TryGetValue(s, out c)) sb.Append('c').Append(ColorUtility.ToHtmlStringRGBA(c)).Append(';');
                Color[] g;
                if (gradients.TryGetValue(s, out g) && g != null && g.Length == 4)
                    sb.Append('g').Append(ColorUtility.ToHtmlStringRGBA(g[0]))
                      .Append(ColorUtility.ToHtmlStringRGBA(g[1]))
                      .Append(ColorUtility.ToHtmlStringRGBA(g[2]))
                      .Append(ColorUtility.ToHtmlStringRGBA(g[3])).Append(';');
                Color[] st;
                if (gradStash.TryGetValue(s, out st) && st != null && st.Length == 4)
                    sb.Append('s').Append(ColorUtility.ToHtmlStringRGBA(st[0])).Append(';');
                string m;
                if (gradientModes.TryGetValue(s, out m)) sb.Append('m').Append(m).Append(';');
                bool pc;
                if (perChar.TryGetValue(s, out pc) && pc) sb.Append('p').Append(';');
            }
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // r150 之后的 build 把 Perfect 拆成 PerfectMinus / XPerfect / PerfectPlus：
        // 老 config.json 里的 "Perfect" 自动摊到这三个档位，免得游戏一更新设置就悄悄失效
        private static void MigrateLegacyPerfect()
        {
            string[] targets = { "PerfectMinus", "XPerfect", "PerfectPlus" };
            bool any = MigrateLegacy(replacements, targets);
            any |= MigrateLegacy(colors, targets);
            any |= MigrateLegacy(gradients, targets);
            any |= MigrateLegacy(gradientModes, targets);
            any |= MigrateLegacy(perChar, targets);
            if (any && Log != null)
                Log.Log("旧配置里的 Perfect 已迁移到 PerfectMinus / XPerfect / PerfectPlus（点「保存」会写成新档位）");
        }

        // 新加的合并档：没有配置就从 XPerfect 拷一份，保证老用户选「仅完美」时还是自己那套字
        private static void MigrateCombinedPerfect()
        {
            if (replacements.ContainsKey(CombinedSlot)) return;
            string text;
            if (!replacements.TryGetValue("XPerfect", out text) || string.IsNullOrEmpty(text)) return;
            replacements[CombinedSlot] = text;
            Color c;
            if (colors.TryGetValue("XPerfect", out c)) colors[CombinedSlot] = c;
            if (Log != null) Log.Log("已把 XPerfect 的文字复制到合并档 PerfectCombined（「仅完美」等预设会用它）");
        }

        // ---------- 「X完美判定文本」预设 ----------

        private static void ResolveLocalization()
        {
            try
            {
                Type rdStringType = AccessTools.TypeByName("RDString");
                if (rdStringType == null) return;
                locGet = rdStringType.GetMethod("Get",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(string), typeof(Dictionary<string, object>) },
                    null);
                if (locGet == null) return;
                locPerfectPlain = GetLocalized("HitMargin.Perfect");
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("本地化接口不可用（合并判断会退回旧行为）: " + e.Message);
            }
        }

        private static string GetLocalized(string key)
        {
            if (locGet == null) return null;
            try { return locGet.Invoke(null, new object[] { key, null }) as string; }
            catch { return null; }
        }

        // 这一档游戏自己会显示的文本（面板里默认就填它）；取不到本地化时用内置简中兜底
        private static string DefaultText(string slot)
        {
            string key = slot == CombinedSlot ? "HitMargin.Perfect" : "HitMargin." + slot;
            string s = GetLocalized(key);
            if (!string.IsNullOrEmpty(s)) return s;
            switch (slot)
            {
                case "TooEarly": return "太快！！";
                case "VeryEarly": return "太快！";
                case "EarlyPerfect": return "稍快！";
                case "PerfectMinus": return "-完美！";
                case "XPerfect": return "X完美！";
                case "PerfectPlus": return "完美！+";
                case "PerfectCombined": return "完美！";
                case "LatePerfect": return "稍慢！";
                case "VeryLate": return "太慢！";
                case "TooLate": return "太慢！！";
                case "FailMiss": return "失误……";
                case "FailOverload": return "按太快……";
                case "Multipress": return "多次按键！！";
                case "OverPress": return "无法再快！！";
            }
            return "";
        }

        // 返回 -1 表示这个游戏版本没有该设置项
        private static int GetPresetValue()
        {
            if (presetProp == null) return -1;
            try
            {
                object v = presetProp.GetValue(null);
                return v == null ? -1 : Convert.ToInt32(v);
            }
            catch { return -1; }
        }

        // 直接改游戏自己的设置值：面板和「设置」菜单看到的是同一个数
        private static void SetPresetValue(int value)
        {
            if (presetProp == null) return;
            try
            {
                presetProp.SetValue(null, Enum.ToObject(presetProp.PropertyType, value));
                SyncGameSettingsUI();
                int now = GetPresetValue();
                if (Log != null)
                    Log.Log("X完美判定文本 -> " + (now >= 0 && now < PresetNames.Length ? PresetNames[now] : "?"));
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("改 X完美判定文本 失败: " + e.Message);
            }
        }

        // 设置界面动作 0 = 重新读一遍 Persistence 并刷新文字，等于游戏内自己点了一次
        private static void SyncGameSettingsUI()
        {
            if (settingsMenuType == null || updateSettingMethod == null
                || currentSettingsProp == null || settingTypeField == null) return;
            try
            {
                object refresh = Enum.ToObject(updateSettingMethod.GetParameters()[1].ParameterType, 0);
                UnityEngine.Object[] menus = Resources.FindObjectsOfTypeAll(settingsMenuType);
                int touched = 0;
                foreach (UnityEngine.Object menu in menus)
                {
                    MonoBehaviour mb = menu as MonoBehaviour;
                    if (mb == null) continue;
                    System.Collections.IList list = currentSettingsProp.GetValue(menu) as System.Collections.IList;
                    if (list == null) continue;
                    foreach (object btn in list)
                    {
                        if (btn == null) continue;
                        if (!"hitMarginPerfectText".Equals(settingTypeField.GetValue(btn) as string)) continue;
                        updateSettingMethod.Invoke(menu, new object[] { btn, refresh });
                        WriteValueLabel(btn);
                        touched++;
                    }
                }
                int pv = GetPresetValue();
                string shown = pv >= 0 && pv < PresetNames.Length ? PresetNames[pv] : "?";
                if (Log != null) Log.Log("已同步游戏设置页 -> " + shown + "（命中 " + touched + " 处）");
            }
            catch (Exception e)
            {
                if (Log != null) Log.Log("同步游戏设置界面失败: " + e.Message);
            }
        }

        // 游戏那一行是按 RDString 键 pauseMenu.settings.<枚举>.<成员名> 取字的；这里直接补写一遍，
        // 就算设置页没被重建（不在暂停菜单里）也不会留旧文案
        private static void WriteValueLabel(object btn)
        {
            if (valueLabelField == null || locGet == null) return;
            int pv = GetPresetValue();
            if (pv < 0 || pv >= PresetNames.Length) return;
            UnityEngine.UI.Text label = valueLabelField.GetValue(btn) as UnityEngine.UI.Text;
            if (label == null) return;
            string text = null;
            try
            {
                text = locGet.Invoke(null, new object[] {
                    "pauseMenu.settings.HitMarginPerfectTextPreset." + PresetEnumNames[pv], null }) as string;
            }
            catch { text = null; }
            if (!string.IsNullOrEmpty(text) && text.IndexOf("pauseMenu.settings.", StringComparison.Ordinal) >= 0)
                text = null;
            label.text = string.IsNullOrEmpty(text) ? PresetNames[pv] : text;
        }

        private static bool MigrateLegacy<T>(Dictionary<string, T> dict, string[] targets)
        {
            T legacy;
            if (!dict.TryGetValue("Perfect", out legacy)) return false;
            bool any = false;
            foreach (string t in targets)
            {
                if (dict.ContainsKey(t)) continue;
                dict[t] = legacy;
                any = true;
            }
            return any;
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
                    if (key.StartsWith("GradStash_", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts2 = val.Split(',');
                        if (parts2.Length == 4)
                        {
                            var arr2 = new Color[4];
                            bool ok2 = true;
                            for (int k = 0; k < 4; k++)
                            {
                                if (!TryParseColor(parts2[k], out arr2[k])) { ok2 = false; break; }
                            }
                            if (ok2) gradStash[key.Substring(10)] = arr2;
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
