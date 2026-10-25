using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PeteTimesSix.CompactHediffs.Rimworld;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.HarmonyPatches
{
    /// <summary>按“优先治疗”重排身体部位分组，使正在流血/需要处理的部位排在前面。</summary>
    [HarmonyPatch(typeof(HealthCardUtility), "VisibleHediffGroupsInOrder")]
    public static class HealthCardUtility_VisibleHediffGroupsInOrder
    {
        [HarmonyPostfix]
        public static void Postfix(ref IEnumerable<IGrouping<BodyPartRecord, Hediff>> __result, Pawn pawn, bool showBloodLoss)
        {
            __result = CustomHealthCardUtility.ReorderHediffGroups(__result, pawn, showBloodLoss);
        }
    }

    /// <summary>
    /// 把 DrawPawnHealthCard 里写死的 0.375（左栏占比）换成 "0.375 * 原始宽度 / 当前宽度"，
    /// 这样加宽标签页时左栏不会被一起拉宽。
    /// </summary>
    [HarmonyPatch(typeof(HealthCardUtility), "DrawPawnHealthCard")]
    public static class HealthCardUtility_DrawPawnHealthCard
    {
        private const float VanillaLeftColumnFraction = 0.375f;

        private static readonly MethodInfo? GetAlteredMultMethod =
            AccessTools.Method(typeof(HealthCardUtility_DrawPawnHealthCard), nameof(GetAlteredMult));

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();

            if (GetAlteredMultMethod == null)
            {
                Log.Error("[CompactHediffs] 找不到 GetAlteredMult，DrawPawnHealthCard 的 Transpiler 已跳过。");
                return list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var instruction = list[i];
                if (instruction.opcode == OpCodes.Ldc_R4
                    && instruction.operand is float constant
                    && Math.Abs(constant - VanillaLeftColumnFraction) < 0.0001f)
                {
                    // ldc.r4 与 call 的栈效果一致（都压入一个 float），可直接替换
                    var replacement = new CodeInstruction(OpCodes.Call, GetAlteredMultMethod);
                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    list[i] = replacement;
                    return list;
                }
            }

            Log.Error("[CompactHediffs] DrawPawnHealthCard 中未找到 0.375 常量，左栏宽度自适应已停用（Transpiler 已跳过，原方法照常运行）。");
            return list;
        }

        public static float GetAlteredMult()
        {
            var settings = CompactHediffsMod.Settings;
            if (!settings.Enabled || settings.extraTabWidth < 1f || !ITab_Pawn_Health_Patches.HasInitialSize)
                return VanillaLeftColumnFraction;

            float initialWidth = ITab_Pawn_Health_Patches.InitialSize.x;
            float currentWidth = ITab_Pawn_Health_Patches.GetSize().x;
            if (currentWidth <= 0f)
                return VanillaLeftColumnFraction;

            return VanillaLeftColumnFraction * (initialWidth / currentWidth);
        }
    }

    /// <summary>用自定义行绘制替换原版 <c>HealthCardUtility.DrawHediffRow</c>。</summary>
    [HarmonyPatch(typeof(HealthCardUtility), "DrawHediffRow")]
    public static class HealthCardUtility_DrawHediffRow
    {
        // 形参名必须与目标方法一致：1.7 的 DrawHediffRow 比 1.6 多了一个可选参数 rowLeftPad。
        // 注意 showDebugOptions 是 DrawHediffListing 的参数，不在这一层，行内调试按钮按原版条件自行判断。
        [HarmonyPrefix]
        public static bool Prefix(Rect rect, Pawn pawn, IEnumerable<Hediff> diffs, ref float curY, float rowLeftPad)
        {
            if (!CompactHediffsMod.Settings.Enabled || !CustomHealthCardUtility.Available)
                return true;

            CustomHealthCardUtility.DrawHediffRow(rect, pawn, diffs, ref curY, rowLeftPad);
            return false;
        }
    }

    /// <summary>
    /// 记录健康列表的可视区高度，供行绘制做滚动裁剪。
    /// 列表本身就在滚动视图里，滚出屏幕的行每帧仍在做文本排版和进度条分块，hediff 一多就是主要开销。
    /// </summary>
    [HarmonyPatch(typeof(HealthCardUtility), "DrawHediffListing")]
    public static class HealthCardUtility_DrawHediffListing
    {
        [HarmonyPrefix]
        public static void Prefix(Rect rect)
        {
            if (CompactHediffsMod.Settings.Enabled && CustomHealthCardUtility.Available)
                CustomHealthCardUtility.BeginListingViewport(rect.height);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            CustomHealthCardUtility.EndListingViewport();
        }
    }
}
