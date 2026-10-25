using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.ModCompat
{
    /// <summary>SmartMedicine：行内医疗护理图标 + 右键护理菜单。</summary>
    [StaticConstructorOnStartup]
    public static class SmartMedicine
    {
        public static bool Active { get; private set; }

        public delegate List<FloatMenuOption> CreateCareMenuOptionsWithListDelegate(List<Hediff> affectedHediffs, Hediff? primaryHediff = null);
        public delegate List<Action<Rect>> GetElementsByListDelegate(List<Hediff> hediffs);

        public static CreateCareMenuOptionsWithListDelegate? CreateCareMenuOptionsWithList { get; private set; }
        public static GetElementsByListDelegate? GetElementsByList { get; private set; }

        static SmartMedicine()
        {
            if (!ModLister.AnyModActiveNoSuffix(new[] { "Memegoddess.SmartMedicine" }))
                return;

            try
            {
                var createOptionsMethod = AccessTools.Method("SmartMedicine.HediffRowPriorityCare:CreateCareMenuOptionsWithList");
                var getElementsMethod = AccessTools.Method("SmartMedicine.HediffRowPriorityCare:GetElementsByList");

                if (createOptionsMethod == null || getElementsMethod == null)
                {
                    Log.Warning($"[CompactHediffs] SmartMedicine 已加载，但行内接口未找到（CreateCareMenuOptionsWithList={createOptionsMethod != null}, GetElementsByList={getElementsMethod != null}），相关图标已停用。");
                    return;
                }

                CreateCareMenuOptionsWithList = AccessTools.MethodDelegate<CreateCareMenuOptionsWithListDelegate>(createOptionsMethod);
                GetElementsByList = AccessTools.MethodDelegate<GetElementsByListDelegate>(getElementsMethod);

                Active = CreateCareMenuOptionsWithList != null && GetElementsByList != null;
                if (!Active)
                    Log.Warning("[CompactHediffs] SmartMedicine 委托创建失败，相关图标已停用。");
            }
            catch (Exception ex)
            {
                Log.Error($"[CompactHediffs] SmartMedicine 兼容初始化失败，相关图标已停用：\n{ex}");
                CreateCareMenuOptionsWithList = null;
                GetElementsByList = null;
                Active = false;
            }
        }
    }
}
