using System;
using HarmonyLib;
using Verse;

namespace PeteTimesSix.CompactHediffs
{
    public class CompactHediffsMod : Mod
    {
        public static CompactHediffs_Settings Settings { get; private set; } = null!;
        public static Harmony? HarmonyInstance { get; private set; }

        public CompactHediffsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<CompactHediffs_Settings>();

            try
            {
                HarmonyInstance = new Harmony("PeteTimesSix.CompactHediffs");
                HarmonyInstance.PatchAll();

                // NHT 的补丁不能用特性声明：没装它时类型不存在，PatchAll 会直接报错。
                ModCompat.NiceHealthTab.Init(HarmonyInstance);
            }
            catch (Exception ex)
            {
                // 补丁失败不应该拖垮整个存档加载，但一定要在日志里留下痕迹
                Log.Error($"[CompactHediffs] Harmony 补丁失败，模组功能已停用：\n{ex}");
            }
        }

        public override string SettingsCategory()
        {
            return "CompactHediffs_ModTitle".Translate();
        }

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            Settings.DoSettingsWindowContents(inRect);
        }
    }
}
