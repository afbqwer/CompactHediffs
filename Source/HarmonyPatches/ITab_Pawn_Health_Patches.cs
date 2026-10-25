using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.HarmonyPatches
{
    /// <summary>
    /// 记录 <see cref="ITab_Pawn_Health"/> 的原始尺寸，并在此基础上叠加设置里的额外宽高。
    /// 标签页实例由游戏反射构造且全局只有一个，所以在这里挂构造函数补丁拿引用。
    /// </summary>
    [HarmonyPatch(typeof(ITab_Pawn_Health), MethodType.Constructor)]
    public static class ITab_Pawn_Health_Patches
    {
        // size 定义在 Verse.InspectTabBase 上（protected Vector2），不是 ITab_Pawn_Health 自己声明的
        private static readonly FieldInfo? SizeField = AccessTools.Field(typeof(InspectTabBase), "size");

        public static ITab_Pawn_Health? SingletonRef { get; private set; }
        public static Vector2 InitialSize { get; private set; }
        public static bool HasInitialSize { get; private set; }

        [HarmonyPrepare]
        public static void Init()
        {
            if (SizeField == null)
            {
                Log.Error("[CompactHediffs] 未找到 Verse.InspectTabBase.size，健康标签页尺寸设置已停用（其余功能不受影响）。");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(ITab_Pawn_Health __instance)
        {
            SingletonRef = __instance;
            var settings = CompactHediffsMod.Settings;
            SetExtraSize(settings.extraTabWidth, settings.extraTabHeight);
        }

        /// <summary>在当前原始尺寸上叠加额外宽高。未拿到标签页实例时静默跳过（构造函数补丁会补上）。</summary>
        public static void SetExtraSize(float extraWidth, float extraHeight)
        {
            if (SizeField == null || SingletonRef == null)
                return;

            if (!HasInitialSize)
            {
                if (SizeField.GetValue(SingletonRef) is Vector2 current)
                {
                    InitialSize = current;
                    HasInitialSize = true;
                }
                else
                {
                    Log.Error("[CompactHediffs] 读取 ITab_Pawn_Health.size 失败，健康标签页尺寸设置已停用。");
                    return;
                }
            }

            SizeField.SetValue(SingletonRef, new Vector2(InitialSize.x + extraWidth, InitialSize.y + extraHeight));
        }

        public static Vector2 GetSize()
        {
            if (SizeField != null && SingletonRef != null && SizeField.GetValue(SingletonRef) is Vector2 current)
                return current;

            return InitialSize;
        }
    }
}
