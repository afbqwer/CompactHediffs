using System;
using HarmonyLib;
using Verse;

namespace PeteTimesSix.CompactHediffs.ModCompat
{
    /// <summary>EBF（Extended Bodyparts Framework）：让部位最大生命值走它的缓存实现。</summary>
    [StaticConstructorOnStartup]
    public static class EBF
    {
        public static bool Active { get; private set; }

        public delegate float GetMaxHealthCachedDelegate(BodyPartDef def, Pawn pawn, BodyPartRecord record);

        public static GetMaxHealthCachedDelegate? GetMaxHealth_Cached { get; private set; }

        static EBF()
        {
            if (ModLister.GetActiveModWithIdentifier("V1024.EBFramework") == null)
                return;

            try
            {
                var method = AccessTools.Method("EBF.VanillaExtender:GetMaxHealth_Cached");
                if (method == null)
                {
                    Log.Warning("[CompactHediffs] EBF 已加载，但未找到 EBF.VanillaExtender.GetMaxHealth_Cached，部位血条回退为原版算法。");
                    return;
                }

                GetMaxHealth_Cached = AccessTools.MethodDelegate<GetMaxHealthCachedDelegate>(method);
                Active = GetMaxHealth_Cached != null;
            }
            catch (Exception ex)
            {
                Log.Error($"[CompactHediffs] EBF 兼容初始化失败，部位血条回退为原版算法：\n{ex}");
                GetMaxHealth_Cached = null;
                Active = false;
            }
        }
    }
}
