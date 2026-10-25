using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.ModCompat
{
    /// <summary>Choose Your Medicine：在血条行右侧画一个“手动指定药物”按钮。</summary>
    [StaticConstructorOnStartup]
    public static class ChooseYourMedicine
    {
        public static bool Active { get; private set; }

        public static readonly int IconWidth = 16;
        public static readonly int IconHeight = 16;

        public delegate void DrawButtonToAssignMedManuallyDelegate(Rect rect, Pawn pawn, IGrouping<HediffDef, Hediff> grouping);

        public static DrawButtonToAssignMedManuallyDelegate? DrawButtonToAssignMedManually { get; private set; }

        private const string PatcherTypeName = "ChooseYourMedicine.CompatibilityCompactHediffs";

        static ChooseYourMedicine()
        {
            if (ModLister.GetActiveModWithIdentifier("Kopp.ChooseYourMedicine") == null)
                return;

            try
            {
                var patcherType = AccessTools.TypeByName(PatcherTypeName);
                if (patcherType == null)
                {
                    // 对端尚未挂上兼容补丁，静默跳过属于预期（不是错误）
                    return;
                }

                var method = AccessTools.Method(patcherType, "DrawButtonToAssignMedManually");
                if (method == null)
                {
                    Log.Warning("[CompactHediffs] Choose Your Medicine 已加载，但未找到 DrawButtonToAssignMedManually，行内指定药物按钮已停用。");
                    return;
                }

                DrawButtonToAssignMedManually = AccessTools.MethodDelegate<DrawButtonToAssignMedManuallyDelegate>(method);
                Active = DrawButtonToAssignMedManually != null;
            }
            catch (Exception ex)
            {
                Log.Error($"[CompactHediffs] Choose Your Medicine 兼容初始化失败，相关按钮已停用：\n{ex}");
            }
        }
    }
}
