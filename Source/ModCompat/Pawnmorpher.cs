using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace PeteTimesSix.CompactHediffs.ModCompat
{
    /// <summary>Pawnmorpher：变形 hediff 的纹理配色、替换身体部位的文字与提示。</summary>
    [StaticConstructorOnStartup]
    public static class Pawnmorpher
    {
        public static bool Active { get; private set; }

        public static HashSet<HediffDef>? PawnmorpherHediffDefs { get; private set; }
        public static HashSet<HediffDef>? CoreMutationHediffDefs { get; private set; }

        public delegate string GetTooltipDelegate(IEnumerable<Hediff> hediffs);

        public static GetTooltipDelegate? GetTooltip { get; private set; }

        static Pawnmorpher()
        {
            if (ModLister.GetActiveModWithIdentifier("tachyonite.pawnmorpher") == null
                && ModLister.GetActiveModWithIdentifier("tachyonite.pawnmorpherpublic") == null)
                return;

            try
            {
                CachePawnmorpherHediffs();

                var patcherType = AccessTools.TypeByName("Pawnmorph.PatchHealthCardUtilityDrawHediffRow");
                if (patcherType != null)
                {
                    var tooltipMethod = AccessTools.Method(patcherType, "Tooltip");
                    if (tooltipMethod != null)
                        GetTooltip = AccessTools.MethodDelegate<GetTooltipDelegate>(tooltipMethod);
                }

                Active = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[CompactHediffs] Pawnmorpher 兼容初始化失败，相关配色与提示已停用：\n{ex}");
                PawnmorpherHediffDefs = null;
                CoreMutationHediffDefs = null;
                GetTooltip = null;
                Active = false;
            }
        }

        private static void CachePawnmorpherHediffs()
        {
            var removeCompType = AccessTools.TypeByName("Pawnmorph.Hediffs.RemoveFromPartComp");
            var removeCompPropsType = AccessTools.TypeByName("Pawnmorph.Hediffs.RemoveFromPartCompProperties");
            var layerField = removeCompPropsType != null ? AccessTools.Field(removeCompPropsType, "layer") : null;

            if (removeCompType == null || layerField == null)
            {
                Log.Warning("[CompactHediffs] Pawnmorpher 的 RemoveFromPartComp/(layer) 未找到，核心突变部位名折叠已停用（其余功能照常）。");
            }

            PawnmorpherHediffDefs = new HashSet<HediffDef>();
            CoreMutationHediffDefs = new HashSet<HediffDef>();

            foreach (var hediffDef in DefDatabase<HediffDef>.AllDefsListForReading)
            {
                var ns = hediffDef.GetType().Namespace;
                if (ns == null || !ns.Contains("Pawnmorph"))
                    continue;

                PawnmorpherHediffDefs.Add(hediffDef);

                if (removeCompType == null || layerField == null || !hediffDef.HasComp(removeCompType))
                    continue;

                var mutationLayerUntyped = layerField.GetValue(hediffDef.CompPropsFor(removeCompType));
                if (mutationLayerUntyped == null)
                    continue;

                var mutationLayerRaw = (int)Convert.ChangeType(mutationLayerUntyped, Enum.GetUnderlyingType(mutationLayerUntyped.GetType()));
                if (mutationLayerRaw == 1) // Core = 1
                    CoreMutationHediffDefs.Add(hediffDef);
            }
        }
    }
}
