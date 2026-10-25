using System.Reflection;
using HarmonyLib;
using Verse;

namespace PeteTimesSix.CompactHediffs.Rimworld
{
	/// <summary>
	/// 原版没有公开的 tendDuration 累计治疗量。这里一次性解析私有字段并缓存访问器，
	/// 解析失败时 Log.Error 并在之后安静地返回 0（不做每帧反射）。
	/// </summary>
	public static class PrivatedAccessors
	{
		private static readonly AccessTools.FieldRef<HediffComp_TendDuration, float>? TotalTendQualityRef = Resolve();

		private static AccessTools.FieldRef<HediffComp_TendDuration, float>? Resolve()
		{
			FieldInfo? field = AccessTools.Field(typeof(HediffComp_TendDuration), "totalTendQuality");
			if (field != null)
				return AccessTools.FieldRefAccess<HediffComp_TendDuration, float>(field);

			Log.Error("[CompactHediffs] 未找到 HediffComp_TendDuration.totalTendQuality，累计治疗条已停用。");
			return null;
		}

		public static float GetTotalTendQuality(this HediffComp_TendDuration instance)
		{
			if (TotalTendQualityRef == null)
				return 0f;

			return TotalTendQualityRef.Invoke(instance);
		}
	}
}
