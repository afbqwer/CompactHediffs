using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PeteTimesSix.CompactHediffs.Rimworld;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.ModCompat
{
    /// <summary>
    /// Nice Health Tab（Andromeda.NiceHealthTab）兼容。
    ///
    /// NHT 的新健康页把 hediff 列表拆成了好几块：
    ///   - statusList：全身性疾病/怀孕/营养不良等，带免疫与预后（NHT 自己画）；
    ///   - dangerList：失血/濒死提示（NHT 自己画）；
    ///   - blocks：按身体部位分组的 hediff 列表（本类接管）；
    ///   - 命中率/疾病概率/护甲/恢复时间等模式是另一套行，NHT 自己画；
    ///   - 手术预览、伤害类型选择器、筛选（按能力/疼痛）、无匹配/折叠提示也都保留。
    ///
    /// 也就是说不能简单地把 NHT 的整个列表换成 vanilla DrawHediffListing —— 那样会连带丢掉
    /// 筛选、预后、命中/护甲/恢复时间、手术预览等 NHT 的功能。这里只替换“每个身体部位块”的绘制：
    ///   - 行高在 NHT 构建列表时按本模组的排版重新计算（AddPartBlock）；
    ///   - 绘制时复用 <see cref="CustomHealthCardUtility.DrawHediffRow"/>，把 NHT 给的行区域整块画成紧凑行；
    ///   - 过滤后的 hediff 列表、身体部位顺序、操作菜单、悬停联动都沿用 NHT 的。
    ///
    /// 全部通过反射接入，NHT 未安装时不会加载它的任何类型。
    /// </summary>
    public static class NiceHealthTab
    {
        private const float NhtScrollbarWidth = 16f;
        private const float NhtCullMargin = 128f;

        private static bool initialized;
        private static bool available;
        private static bool bloodLossBarAvailable;

        private static Type? bodyPartTableType;
        private static Type? partBlockType;

        private static FieldInfo? bodyPartPawnField;
        private static FieldInfo? lastContentWidthField;
        private static FieldInfo? scrollPositionField;
        private static FieldInfo? filterField;
        private static FieldInfo? thingForMedBillsField;
        private static FieldInfo? lastCheapSignatureField;
        private static FieldInfo? lastSignatureField;
        private static FieldInfo? ownerGeometryField;
        private static FieldInfo? timingsModeField;
        private static FieldInfo? hitPercentModeField;
        private static FieldInfo? diseasePercentModeField;
        private static FieldInfo? armorModeField;

        private static FieldInfo? partBlockPawnField;
        private static FieldInfo? partBlockPartField;
        private static FieldInfo? partBlockOwnerField;
        private static FieldInfo? partBlockGeometryField;

        // NHT 的全身失血提示块（dangerList 里的 DangerBlock）
        private static Type? dangerBlockType;
        private static FieldInfo? dangerBlockPawnField;
        private static FieldInfo? dangerBlockGeometryField;
        private static FieldInfo? dangerBlockVisibleField;
        private static FieldInfo? dangerBlockBloodProfileField;
        private static FieldInfo? bloodProfileUsePawnIconColorField;
        private static FieldInfo? bloodProfileIconColorField;
        private static int dangerRowsBefore;

        private static FieldInfo? hoveredHediffField;
        private static FieldInfo? animatedRowHeightField;
        private static FieldInfo? animatedRowGapAfterField;

        /// <summary>
        /// NHT 的 byPart 不含“全身”块（全身 hediff 走单独的列表），所以不能只靠它反查。
        /// 在 NHT 构建部位块时把 hediff 列表按 (widget, pawn, 部位 index) 存一份，
        /// 绘制时直接用这份“已被 NHT 筛选/排序过”的列表。BuildRows 开头会整体清空。
        /// </summary>
        private static readonly ConditionalWeakTable<object, Dictionary<(int PawnId, int PartIndex), List<Hediff>>> partHediffs =
            new ConditionalWeakTable<object, Dictionary<(int PawnId, int PartIndex), List<Hediff>>>();

        /// <summary>影响紧凑行高度的设置快照；变化时强制 NHT 重建列表，否则沿用的还是旧行高。</summary>
        private static int appliedSettingsStamp;

        /// <summary>设置窗口里改过东西，下一次列表重建时强制刷新（不必提前触碰 NHT 的实例）。</summary>
        private static bool settingsDirty;

        private static MethodInfo? openFloatMenuForPartMethod;
        private static bool partClickWarningLogged;
        private static bool bloodLossBarBroken;

        public static bool Available => available;

        /// <summary>NHT 的失血提示块结构可用（失血条可以显示）。</summary>
        public static bool BloodLossBarAvailable => bloodLossBarAvailable;

        /// <summary>当前是否真的在使用紧凑行：NHT 存在、总开关打开、兼容开关打开。</summary>
        public static bool Active =>
            available
            && CompactHediffsMod.Settings != null
            && CompactHediffsMod.Settings.Enabled
            && CompactHediffsMod.Settings.niceHealthTabCompat;

        /// <summary>NHT 失血提示条：独立开关，不要求同时开启身体部位紧凑行。</summary>
        public static bool BloodLossBarActive =>
            bloodLossBarAvailable
            && CompactHediffsMod.Settings != null
            && CompactHediffsMod.Settings.Enabled
            && CompactHediffsMod.Settings.niceHealthTabBloodLossBar;

        public static void Init(Harmony harmony)
        {
            if (initialized)
                return;

            initialized = true;

            try
            {
                bodyPartTableType = AccessTools.TypeByName("NiceHealthTab.BodyPartTableWidget");
                if (bodyPartTableType == null)
                    return; // 没装 NHT：静默退出

                partBlockType = AccessTools.Inner(bodyPartTableType, "PartBlock");
                if (partBlockType == null)
                {
                    Log.Warning("[CompactHediffs] 检测到 Nice Health Tab，但找不到 BodyPartTableWidget+PartBlock，兼容已停用。");
                    return;
                }

                bodyPartPawnField = AccessTools.Field(bodyPartTableType, "Pawn");
                lastContentWidthField = AccessTools.Field(bodyPartTableType, "lastContentWidth");
                scrollPositionField = AccessTools.Field(bodyPartTableType, "scrollPosition");
                filterField = AccessTools.Field(bodyPartTableType, "Filter");
                thingForMedBillsField = AccessTools.Field(bodyPartTableType, "ThingForMedBills");
                lastCheapSignatureField = AccessTools.Field(bodyPartTableType, "lastCheapSignature");
                lastSignatureField = AccessTools.Field(bodyPartTableType, "lastSignature");

                // NHT 的这几种模式是另外一套行（命中率/疾病概率/护甲/恢复时间），
                // 它们各自带有非 hediff 的信息，交给 NHT 原样绘制。
                timingsModeField = AccessTools.Field(bodyPartTableType, "TimingsMode");
                hitPercentModeField = AccessTools.Field(bodyPartTableType, "HitPercentMode");
                diseasePercentModeField = AccessTools.Field(bodyPartTableType, "DiseasePercentMode");
                armorModeField = AccessTools.Field(bodyPartTableType, "ArmorMode");

                partBlockPawnField = AccessTools.Field(partBlockType, "Pawn");
                partBlockPartField = AccessTools.Field(partBlockType, "Part");
                partBlockOwnerField = AccessTools.Field(partBlockType, "Owner");
                partBlockGeometryField = AccessTools.Field(partBlockType, "Geometry"); // 继承自 MilkyWay.Widget
                ownerGeometryField = AccessTools.Field(bodyPartTableType, "Geometry");

                dangerBlockType = AccessTools.Inner(bodyPartTableType, "DangerBlock");
                if (dangerBlockType != null)
                {
                    dangerBlockPawnField = AccessTools.Field(dangerBlockType, "Pawn");
                    dangerBlockGeometryField = AccessTools.Field(dangerBlockType, "Geometry");
                    dangerBlockVisibleField = AccessTools.Field(dangerBlockType, "Visible");
                    dangerBlockBloodProfileField = AccessTools.Field(dangerBlockType, "BloodProfile");

                    var bloodProfileType = AccessTools.TypeByName("NiceHealthTab.BloodDisplayProfile");
                    bloodProfileUsePawnIconColorField = bloodProfileType != null ? AccessTools.Field(bloodProfileType, "UsePawnIconColor") : null;
                    bloodProfileIconColorField = bloodProfileType != null ? AccessTools.Field(bloodProfileType, "IconColor") : null;
                }

                var filterType = AccessTools.TypeByName("NiceHealthTab.HediffListFilter");
                hoveredHediffField = filterType != null ? AccessTools.Field(filterType, "HoveredHediff") : null;

                openFloatMenuForPartMethod = AccessTools.Method("NiceHealthTab.DollOperations:OpenFloatMenuForPart");

                var addPartBlockMethod = FindMethod(bodyPartTableType, "AddPartBlock");
                var partBlockDrawMethod = AccessTools.Method(partBlockType, "Draw");
                var rebuildMethod = FindMethod(bodyPartTableType, "RebuildIfChanged");
                var buildRowsMethod = FindMethod(bodyPartTableType, "BuildRows");

                if (addPartBlockMethod == null || partBlockDrawMethod == null || buildRowsMethod == null
                    || rebuildMethod == null
                    || partBlockPawnField == null || partBlockPartField == null || partBlockOwnerField == null
                    || partBlockGeometryField == null)
                {
                    Log.Warning("[CompactHediffs] 检测到 Nice Health Tab，但它的健康列表结构不匹配，兼容已停用。");
                    return;
                }

                harmony.Patch(addPartBlockMethod,
                    postfix: new HarmonyMethod(typeof(NiceHealthTab), nameof(AddPartBlock_Postfix)));

                harmony.Patch(partBlockDrawMethod,
                    prefix: new HarmonyMethod(typeof(NiceHealthTab), nameof(PartBlock_Draw_Prefix)));

                if (rebuildMethod != null)
                {
                    harmony.Patch(rebuildMethod,
                        prefix: new HarmonyMethod(typeof(NiceHealthTab), nameof(RebuildIfChanged_Prefix)));
                }

                if (buildRowsMethod != null)
                {
                    harmony.Patch(buildRowsMethod,
                        prefix: new HarmonyMethod(typeof(NiceHealthTab), nameof(BuildRows_Prefix)));
                }

                // 全身失血提示条是附加功能：结构不匹配时只停用它，不影响上面的紧凑行
                var dangerDrawMethod = dangerBlockType != null ? AccessTools.Method(dangerBlockType, "Draw") : null;
                var addDangerMethod = FindMethod(bodyPartTableType, "AddDangerBlock");
                if (dangerBlockType != null && dangerDrawMethod != null && addDangerMethod != null
                    && dangerBlockPawnField != null && dangerBlockGeometryField != null && dangerBlockVisibleField != null)
                {
                    harmony.Patch(addDangerMethod,
                        prefix: new HarmonyMethod(typeof(NiceHealthTab), nameof(AddDangerBlock_Prefix)),
                        postfix: new HarmonyMethod(typeof(NiceHealthTab), nameof(AddDangerBlock_Postfix)));

                    harmony.Patch(dangerDrawMethod,
                        postfix: new HarmonyMethod(typeof(NiceHealthTab), nameof(DangerBlock_Draw_Postfix)));

                    bloodLossBarAvailable = true;
                }
                else
                {
                    Log.Warning("[CompactHediffs] Nice Health Tab 的失血提示块结构不匹配，失血条已停用（紧凑行不受影响）。");
                }

                appliedSettingsStamp = ComputeSettingsStamp();

                available = true;
                Log.Message("[CompactHediffs] 已接入 Nice Health Tab：身体部位列表使用紧凑行，失血提示补上严重度条（均可在设置中关闭）。");
            }
            catch (Exception ex)
            {
                available = false;
                Log.Error($"[CompactHediffs] Nice Health Tab 兼容初始化失败，已回退到 NHT 自带列表：\n{ex}");
            }
        }

        private static MethodInfo? FindMethod(Type type, string name)
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == name);
        }

        /// <summary>
        /// NHT 构建每个部位块时，行高是按它自己的 22px 行预估的。
        /// 这里在它加完块之后，把这一块的 AnimatedListRow.Height 改成紧凑行真正需要的高度，
        /// 否则多行标签、图标和进度条会被下一块压住。
        /// </summary>
        public static void AddPartBlock_Postfix(object __instance, object[] __args)
        {
            if (!Active || !CustomHealthCardUtility.Available)
                return;

            try
            {
                // AddPartBlock(List<AnimatedListRow> rows, BodyPartRecord part, List<Hediff> hediffs)
                if (__args == null || __args.Length < 3)
                    return;

                if (!(__args[0] is System.Collections.IList rows) || rows.Count == 0)
                    return;

                if (!(__args[2] is List<Hediff> hediffs) || hediffs.Count == 0)
                    return;

                // 命中率/疾病概率/护甲/恢复时间模式由 NHT 自己绘制，不要按紧凑行改高度
                bool specialMode = SpecialModeActive(__instance);

                var pawn = bodyPartPawnField?.GetValue(__instance) as Pawn;
                if (pawn == null)
                    return;

                // 缓存这次真正参与显示、且已被 NHT 筛选过的 hediff（含“全身”块）
                var part = __args[1] as BodyPartRecord;
                CachePartHediffs(__instance, pawn, part, hediffs);

                if (specialMode)
                    return;

                float width = GetMeasureWidth(__instance);
                if (width <= 1f)
                    return;

                GameFont oldFont = Text.Font;
                bool oldWrap = Text.WordWrap;
                TextAnchor oldAnchor = Text.Anchor;

                float height;
                try
                {
                    Text.Font = GameFont.Small;
                    Text.WordWrap = true;
                    Text.Anchor = TextAnchor.UpperLeft;

                    height = CustomHealthCardUtility.MeasureHediffRowHeight(
                        new Rect(0f, 0f, width, 0f), pawn, hediffs, 0f);
                }
                finally
                {
                    Text.Font = oldFont;
                    Text.WordWrap = oldWrap;
                    Text.Anchor = oldAnchor;
                }

                // NHT 自己的最小行高是 22；别让紧凑行把块压得比它更矮
                if (float.IsNaN(height) || float.IsInfinity(height))
                    height = 22f;
                height = Mathf.Max(height, 22f);

                object row = rows[rows.Count - 1]!;
                var heightField = animatedRowHeightField
                    ??= AccessTools.Field(row.GetType(), "Height");
                heightField?.SetValue(row, height);

                // NHT 给每个部位块自带 2px GapAfter；本模组已有 entrySpacing 控制间距，
                // 不置零的话两边会叠加，看起来就是“设置了间距但间隔还是偏大”。
                var gapAfterField = animatedRowGapAfterField
                    ??= AccessTools.Field(row.GetType(), "GapAfter");
                gapAfterField?.SetValue(row, 0f);
            }
            catch (Exception ex)
            {
                DisableAfterError(ex);
            }
        }

        /// <summary>真正替换部位块绘制：身体部位列、健康条、分组、图标、进度条、提示全部交给本模组。</summary>
        public static bool PartBlock_Draw_Prefix(object __instance)
        {
            if (!Active || !CustomHealthCardUtility.Available)
                return true;

            try
            {
                var pawn = partBlockPawnField?.GetValue(__instance) as Pawn;
                var part = partBlockPartField?.GetValue(__instance) as BodyPartRecord;
                object? owner = partBlockOwnerField?.GetValue(__instance);
                if (pawn == null || owner == null)
                    return true;

                if (SpecialModeActive(owner))
                    return true;

                // 用 NHT 构建这一块时缓存的列表：能力/疼痛筛选、排序、全身块都已经处理过
                if (!TryGetCachedHediffs(owner, pawn, part, out List<Hediff>? hediffs) || hediffs == null || hediffs.Count == 0)
                    return true;

                if (!(partBlockGeometryField?.GetValue(__instance) is Rect geometry))
                    return true;

                if (geometry.width <= 0f || geometry.height <= 0f)
                    return false; // 动画刚出现时高度还是 0，别回退成 NHT 的绘制

                // 滚动裁剪。NHT 的 AnimatedList 会把所有块都画一遍，不裁的话伤口一多等于全量重绘。
                // 这里用保守的可视高度（整个 widget 的内高）——宁可多画几行，也不能误裁。
                if (IsOutsideViewport(owner, geometry))
                    return false;

                if (pawn.health == null || pawn.health.hediffSet == null)
                    return true;

                // NHT 原来点身体部位标题会弹出该部位的手术菜单。紧凑行把标题画在自己的左栏里，
                // 这里把点击补回去（手术预览、缺少材料等仍旧由 NHT 的 DollOperations 处理）。
                // 必须在绘制之前处理：行本身会把点击交给原版的战斗日志逻辑，事件之后就被消费了。
                HandlePartClick(pawn, part, owner, geometry);

                GameFont oldFont = Text.Font;
                bool oldWrap = Text.WordWrap;
                TextAnchor oldAnchor = Text.Anchor;
                Color oldColor = GUI.color;

                try
                {
                    Text.Font = GameFont.Small;
                    Text.WordWrap = true;
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;

                    // NHT 原本鼠标悬停有整行高亮；紧凑行的隔行高亮打开时它会自己顺带画悬停高亮。
                    if (!CompactHediffsMod.Settings.evenOddHighlights && Mouse.IsOver(geometry))
                        GUI.DrawTexture(geometry, TexUI.HighlightTex);

                    bool oldSuppress = CustomHealthCardUtility.SuppressViewportCulling;
                    CustomHealthCardUtility.SuppressViewportCulling = true;
                    try
                    {
                        float curY = geometry.y;
                        CustomHealthCardUtility.DrawHediffRow(geometry, pawn, hediffs, ref curY, 0f);
                    }
                    finally
                    {
                        CustomHealthCardUtility.SuppressViewportCulling = oldSuppress;
                    }
                }
                finally
                {
                    Text.Font = oldFont;
                    Text.WordWrap = oldWrap;
                    Text.Anchor = oldAnchor;
                    GUI.color = oldColor;
                }

                // NHT 的概览面板会根据“当前悬停的 hediff”高亮相关能力/疼痛项，这里保持联动。
                if (Mouse.IsOver(geometry) && filterField != null && hoveredHediffField != null)
                {
                    object? filter = filterField.GetValue(owner);
                    if (filter != null)
                        hoveredHediffField.SetValue(filter, hediffs[0]);
                }

                return false;
            }
            catch (Exception ex)
            {
                DisableAfterError(ex);
                return true;
            }
        }

        /// <summary>
        /// NHT 的失血提示块只有图标 + 文字。这里给这一行额外留出条高，
        /// 由 <see cref="DangerBlock_Draw_Postfix"/> 在文字下方画失血严重度条。
        /// </summary>
        public static void AddDangerBlock_Prefix(object[] __args)
        {
            dangerRowsBefore = __args != null && __args.Length > 0 && __args[0] is System.Collections.IList rows
                ? rows.Count
                : -1;
        }

        public static void AddDangerBlock_Postfix(object __instance, object[] __args)
        {
            if (!BloodLossBarActive || bloodLossBarBroken)
                return;

            try
            {
                // AddDangerBlock(List<AnimatedListRow> danger)
                if (__args == null || __args.Length < 1 || !(__args[0] is System.Collections.IList rows) || rows.Count == 0)
                    return;

                // 方法可能因“没有失血问题”提前返回，必须确认它确实加了一行
                if (dangerRowsBefore < 0 || rows.Count != dangerRowsBefore + 1)
                    return;

                var pawn = bodyPartPawnField?.GetValue(__instance) as Pawn;
                if (pawn == null)
                    return;

                int barHeight = CustomHealthCardUtility.GetBloodLossBarHeight(pawn);
                if (barHeight <= 0)
                    return;

                // 原内容在行内垂直居中；多留 2*条高+4，文字底部与条之间就有 1px 空隙
                object row = rows[rows.Count - 1]!;
                var heightField = animatedRowHeightField ??= AccessTools.Field(row.GetType(), "Height");
                if (heightField?.GetValue(row) is float current)
                    heightField.SetValue(row, current + barHeight * 2 + 4);
            }
            catch (Exception ex)
            {
                DisableBloodLossBar(ex);
            }
        }

        /// <summary>NHT 的全身失血提示绘制完之后，在文字下方补一条失血严重度条。</summary>
        public static void DangerBlock_Draw_Postfix(object __instance)
        {
            if (!BloodLossBarActive || bloodLossBarBroken)
                return;

            try
            {
                if (dangerBlockVisibleField?.GetValue(__instance) is bool visible && !visible)
                    return;

                var pawn = dangerBlockPawnField?.GetValue(__instance) as Pawn;
                if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                    return;

                if (!HasBloodProblem(pawn))
                    return;

                if (!(dangerBlockGeometryField?.GetValue(__instance) is Rect geometry)
                    || geometry.width <= 0f || geometry.height <= 0f)
                    return;

                int barHeight = CustomHealthCardUtility.GetBloodLossBarHeight(pawn);
                if (barHeight <= 0)
                    return;

                // 和 NHT 的文字区对齐：左边让出 4 + 22(图标) + 4，右边留 4
                float barX = geometry.x + 30f;
                float barWidth = geometry.xMax - barX - 4f;
                if (barWidth <= 1f)
                    return;

                Rect barRect = new Rect(barX, geometry.yMax - barHeight - 1f, barWidth, barHeight);
                CustomHealthCardUtility.DrawBloodLossBar(barRect, pawn, GetNhtBloodColorOverride(__instance));
            }
            catch (Exception ex)
            {
                DisableBloodLossBar(ex);
            }
        }

        /// <summary>与 NHT 的 DangerBlock 完全一致的显示条件。</summary>
        private static bool HasBloodProblem(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            if (pawn.health.hediffSet.BleedRateTotal > 0.01f)
                return true;

            Hediff? bloodLoss = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
            return bloodLoss != null && bloodLoss.Visible;
        }

        /// <summary>
        /// NHT 的异色血液（neutro）使用它自己的图标色；普通血液继续用本模组的失血渐变色。
        /// </summary>
        private static Color? GetNhtBloodColorOverride(object dangerBlock)
        {
            object? profile = dangerBlockBloodProfileField?.GetValue(dangerBlock);
            if (profile == null)
                return null;

            if (bloodProfileUsePawnIconColorField?.GetValue(profile) is bool usePawnColor && usePawnColor
                && bloodProfileIconColorField?.GetValue(profile) is Color iconColor)
            {
                return iconColor;
            }

            return null;
        }

        private static void DisableBloodLossBar(Exception ex)
        {
            if (bloodLossBarBroken)
                return;

            bloodLossBarBroken = true;
            Log.Warning($"[CompactHediffs] Nice Health Tab 失血条绘制失败，已停用该附加功能：\n{ex}");
        }

        private static void CachePartHediffs(object owner, Pawn pawn, BodyPartRecord? part, List<Hediff> hediffs)
        {
            Dictionary<(int PawnId, int PartIndex), List<Hediff>> map = partHediffs.GetOrCreateValue(owner);
            map[(pawn.thingIDNumber, part?.Index ?? -1)] = hediffs;
        }

        private static bool TryGetCachedHediffs(object owner, Pawn pawn, BodyPartRecord? part, out List<Hediff>? hediffs)
        {
            if (partHediffs.TryGetValue(owner, out Dictionary<(int PawnId, int PartIndex), List<Hediff>>? map)
                && map.TryGetValue((pawn.thingIDNumber, part?.Index ?? -1), out List<Hediff>? found))
            {
                hediffs = found;
                return true;
            }

            hediffs = null;
            return false;
        }

        /// <summary>命中率/疾病概率/护甲/恢复时间是 NHT 的另一套行，紧凑行不参与。</summary>
        private static bool SpecialModeActive(object bodyPartTable)
        {
            return IsTrue(timingsModeField, bodyPartTable)
                || IsTrue(hitPercentModeField, bodyPartTable)
                || IsTrue(diseasePercentModeField, bodyPartTable)
                || IsTrue(armorModeField, bodyPartTable);
        }

        private static bool IsTrue(FieldInfo? field, object instance)
        {
            return field?.GetValue(instance) is bool value && value;
        }

        /// <summary>
        /// 每次重建列表先丢掉旧缓存：某个部位治好后会变成“健康部位块”（不经过 AddPartBlock），
        /// 不清的话会拿上一次的 hediff 列表把它画成紧凑行。
        /// </summary>
        public static void BuildRows_Prefix(object __instance)
        {
            if (!available)
                return;

            partHediffs.Remove(__instance);
        }

        /// <summary>影响行高的设置变化后，让 NHT 重新测一遍（否则列表会按旧高度留白/压字）。</summary>
        public static void RebuildIfChanged_Prefix(object __instance)
        {
            if (!available || CompactHediffsMod.Settings == null)
                return;

            int stamp = ComputeSettingsStamp();
            if (stamp == appliedSettingsStamp && !settingsDirty)
                return;

            appliedSettingsStamp = stamp;
            settingsDirty = false;
            lastCheapSignatureField?.SetValue(__instance, null);
            lastSignatureField?.SetValue(__instance, null);
        }

        private static int ComputeSettingsStamp()
        {
            var s = CompactHediffsMod.Settings;
            if (s == null)
                return 0;

            unchecked
            {
                int hash = s.Enabled ? 1 : 0;
                hash = hash * 31 + (s.niceHealthTabCompat ? 1 : 0);
                hash = hash * 31 + (s.niceHealthTabBloodLossBar ? 1 : 0);
                hash = hash * 31 + (s.replacingPartInBodypart ? 1 : 0);
                hash = hash * 31 + (s.italicizeMissing ? 1 : 0);
                hash = hash * 31 + (s.verticalSeparator ? 1 : 0);
                hash = hash * 31 + (s.horizontalSeparator ? 1 : 0);
                hash = hash * 31 + (s.internalSeparator ? 1 : 0);
                hash = hash * 31 + (s.tendPrioritySort ? 1 : 0);
                hash = hash * 31 + (s.bleedingIcons ? 1 : 0);
                hash = hash * 31 + (s.tendingIcons ? 1 : 0);
                hash = hash * 31 + (s.perHediffIcons ? 1 : 0);
                hash = hash * 31 + (s.showCumulativeThreatment ? 1 : 0);
                hash = hash * 31 + (s.showHiddenProgressConditions ? 1 : 0);
                hash = hash * 31 + (int)s.severityBarsPosition;
                hash = hash * 31 + (int)s.severityBarMode;
                hash = hash * 31 + s.separatorSize;
                hash = hash * 31 + s.entrySpacing;
                hash = hash * 31 + s.internalBarHeight;

                // 语言变了文字宽高也会变；NHT 自己的签名里没有它
                hash = hash * 31 + (LanguageDatabase.activeLanguage?.folderName?.GetHashCode() ?? 0);
                return hash;
            }
        }

        private static float GetMeasureWidth(object bodyPartTable)
        {
            float width = lastContentWidthField?.GetValue(bodyPartTable) is float w ? w : 0f;
            if (width <= 1f && ownerGeometryField?.GetValue(bodyPartTable) is Rect geometry)
                width = geometry.width;

            // 有滚动条时 NHT 的滚动内容会窄 16px；按窄的测，画出来只会多留一点白，不会被压住。
            return width - NhtScrollbarWidth;
        }

        private static bool IsOutsideViewport(object owner, Rect geometry)
        {
            if (scrollPositionField?.GetValue(owner) is not Vector2 scroll)
                return false;

            if (float.IsNaN(scroll.y))
                return false;

            float visibleHeight = ownerGeometryField?.GetValue(owner) is Rect ownerGeometry
                ? ownerGeometry.height
                : 0f;

            // NHT 的块坐标是“滚动内容坐标”，从 0 开始
            return geometry.y + geometry.height < scroll.y - NhtCullMargin
                || geometry.y > scroll.y + visibleHeight + NhtCullMargin;
        }

        private static void HandlePartClick(Pawn pawn, BodyPartRecord? part, object owner, Rect geometry)
        {
            if (openFloatMenuForPartMethod == null || Event.current == null)
                return;

            if (Event.current.type != EventType.MouseDown)
                return;

            // 与 CustomHealthCardUtility 的身体部位列保持一致（行宽的 37.5%）。
            // 最右侧 10px 是“替代部件”的信息按钮，留给它自己处理，别抢成手术菜单。
            float clickWidth = Mathf.Max(0f, geometry.width * 0.375f - CustomHealthCardUtility.IconHeight / 2f);
            Rect partColumn = new Rect(geometry.x, geometry.y, clickWidth, geometry.height);
            if (!Mouse.IsOver(partColumn))
                return;

            try
            {
                object? thing = thingForMedBillsField?.GetValue(owner);
                openFloatMenuForPartMethod.Invoke(null, new object?[] { pawn, part, thing });

                // 吃掉这次点击，别让紧凑行再把同一下点击当成“跳到战斗日志”
                Event.current.Use();
            }
            catch (Exception ex)
            {
                // 手术菜单是 NHT 的功能，出错不应该反过来停用整个列表
                if (!partClickWarningLogged)
                {
                    partClickWarningLogged = true;
                    Log.Warning($"[CompactHediffs] 打开 Nice Health Tab 身体部位菜单失败：\n{ex}");
                }
            }
        }

        /// <summary>
        /// 设置变化（总开关 / 兼容开关）后让 NHT 下一帧重建列表：
        /// 行高是按紧凑行重新算过的，不重建的话会继续沿用旧高度。
        /// </summary>
        public static void NotifySettingsChanged()
        {
            if (!initialized || !available)
                return;

            settingsDirty = true;
        }

        private static void DisableAfterError(Exception ex)
        {
            // 只报一次并整体停用，避免每帧每条 hediff 刷日志
            if (!available)
                return;

            available = false;
            Log.Error($"[CompactHediffs] Nice Health Tab 兼容绘制失败，已回退到 NHT 自带列表：\n{ex}");
        }
    }
}
