using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using PeteTimesSix.CompactHediffs.ModCompat;
using PeteTimesSix.CompactHediffs.Rimworld.UI;
using PeteTimesSix.CompactHediffs.Rimworld.UI_compat;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.Rimworld
{
	/// <summary>
	/// 原版 <see cref="HealthCardUtility.DrawHediffRow"/> 的替代实现。
	/// 依赖的私有成员在静态构造里一次性解析并显式报错；<see cref="Available"/> 为 false 时补丁回退到原版绘制。
	/// </summary>
	[StaticConstructorOnStartup]
	public static class CustomHealthCardUtility
	{
		public static readonly Color HighlightColor = new Color(0.5f, 0.5f, 0.5f, 1f);
		public static readonly Color StaticHighlightColor = new Color(0.75f, 0.75f, 0.85f, 1f);
		public static readonly Color CyanWhite = new Color(0.8f, 1.0f, 1.0f, 1f);
		public static readonly Color BluishGreen = new Color(0.0f, 1.0f, 0.4f, 1f);
		public static readonly Color MissingBodyPart = new Color(0.15f, 0.0f, 0.0f, 1f);

		public static readonly int IconHeight = 20;
		public static readonly int DefaultIconHeight = 18;
		public static readonly int BleedIconWidth = 15;
		public static readonly int TendIconWidth = 15;
		public static readonly int InfoIconWidth = 10;
		public static readonly int DevRemoveButtonWidth = 20;

		public static readonly int SmartMedicineIconWidth = 20;
		public static readonly int SmartMedicineIconHeight = 20;

		private delegate bool CanEntryBeClickedDelegate(IEnumerable<Hediff> diffs, Pawn pawn);
		private delegate void EntryClickedDelegate(IEnumerable<Hediff> diffs, Pawn pawn);
		private delegate string GetTooltipDelegate(Pawn pawn, BodyPartRecord? part);
		private delegate float GetListPriorityDelegate(BodyPartRecord? rec);

		private static AccessTools.FieldRef<bool>? highlight;
		private static AccessTools.FieldRef<bool>? showHediffsDebugInfo;

		private static CanEntryBeClickedDelegate? canEntryBeClicked;
		private static EntryClickedDelegate? entryClicked;
		private static GetTooltipDelegate? getTooltip;
		private static GetListPriorityDelegate? getListPriority;

		/// <summary>核心私有成员至少解析成功一个，才值得接管原版行绘制。</summary>
		public static bool Available { get; private set; }

		private static bool ShowHediffsDebugInfo => showHediffsDebugInfo != null && showHediffsDebugInfo.Invoke();

		static CustomHealthCardUtility()
		{
			try
			{
				ResolveVanillaMembers();
			}
			catch (Exception ex)
			{
				// 静态构造抛异常会变成 TypeInitializationException，后续每次访问都炸；
				// 这里兜住并整体停用特性，让模组其余部分照常工作。
				Log.Error($"[CompactHediffs] 解析原版健康面板成员时抛出异常，紧凑血条行已停用（回退原版绘制）：\n{ex}");
				highlight = null;
				showHediffsDebugInfo = null;
				canEntryBeClicked = null;
				entryClicked = null;
				getTooltip = null;
				getListPriority = null;
			}
		}

		private static void ResolveVanillaMembers()
		{
			var healthCardType = typeof(HealthCardUtility);

			var highlightField = AccessTools.Field(healthCardType, "highlight");
			var debugInfoField = AccessTools.Field(healthCardType, "showHediffsDebugInfo");
			var canEntryBeClickedMethod = AccessTools.Method(healthCardType, "CanEntryBeClicked");
			var entryClickedMethod = AccessTools.Method(healthCardType, "EntryClicked");
			var getTooltipMethod = AccessTools.Method(healthCardType, "GetTooltip");
			var getListPriorityMethod = AccessTools.Method(healthCardType, "GetListPriority");

			if (highlightField != null)
				highlight = AccessTools.StaticFieldRefAccess<bool>(highlightField);
			else
				Log.Error("[CompactHediffs] 未找到 HealthCardUtility.highlight，隔行高亮已停用。");

			if (debugInfoField != null)
				showHediffsDebugInfo = AccessTools.StaticFieldRefAccess<bool>(debugInfoField);
			else
				Log.Error("[CompactHediffs] 未找到 HealthCardUtility.showHediffsDebugInfo，提示框将不显示调试信息。");

			if (canEntryBeClickedMethod != null)
				canEntryBeClicked = AccessTools.MethodDelegate<CanEntryBeClickedDelegate>(canEntryBeClickedMethod);
			else
				Log.Error("[CompactHediffs] 未找到 HealthCardUtility.CanEntryBeClicked，行点击已停用。");

			if (entryClickedMethod != null)
				entryClicked = AccessTools.MethodDelegate<EntryClickedDelegate>(entryClickedMethod);
			else
				Log.Error("[CompactHediffs] 未找到 HealthCardUtility.EntryClicked，行点击已停用。");

			if (getTooltipMethod != null)
				getTooltip = AccessTools.MethodDelegate<GetTooltipDelegate>(getTooltipMethod);
			else
				Log.Error("[CompactHediffs] 未找到 HealthCardUtility.GetTooltip，部位提示已停用。");

			if (getListPriorityMethod != null)
				getListPriority = AccessTools.MethodDelegate<GetListPriorityDelegate>(getListPriorityMethod);
			else
				Log.Error("[CompactHediffs] 未找到 HealthCardUtility.GetListPriority，部位排序将退化为默认顺序。");

			// 三个成员都拿不到，说明原版私有 API 已经大改，别再硬接管绘制
			Available = highlight != null || showHediffsDebugInfo != null || canEntryBeClicked != null;

			if (!Available)
				Log.Error("[CompactHediffs] 原版健康行私有成员全部解析失败，紧凑血条行已停用（回退原版绘制）。");
		}

		public static void DrawHediffRow(Rect rowRect, Pawn pawn, IEnumerable<Hediff> diffs, ref float curY, float rowLeftPad)
		{
			// 与原版 1.7 DrawHediffRow 一致：行内调试按钮只看 ShowDevGizmos
			bool showDebugOptions = DebugSettings.ShowDevGizmos && Current.ProgramState == ProgramState.Playing;

			rowRect = rowRect.Rounded();
			int currentY = (int)curY;

			var settings = CompactHediffsMod.Settings;

			// 两个列宽要分清：身体部位列画健康条和竖分隔线，hediff 列从分隔线右侧开始。
			// 行高预估和实际绘制必须用同一个 hediff 列宽，否则 Text.CalcHeight 会在换行边界上差一行。
			int bodyPartColumnWidth = (int)(rowRect.width * 0.375f);
			int column_bodypartWidth = settings.verticalSeparator
				? bodyPartColumnWidth + settings.verticalSeparatorWidth
				: bodyPartColumnWidth;

			BodyPartRecord part = diffs.First().Part;
			Hediff? replacingPart = null;
			if (part != null && settings.replacingPartInBodypart)
				replacingPart = GetReplacingPart(diffs);

			string bodyPartText = MakeBodyPartText(pawn, part, replacingPart);

			int bodypartLabelWidth = (int)Math.Max(bodyPartColumnWidth - IconHeight / 2f - rowLeftPad, 1f);
			int bodyPartLabelHeight = (int)(Text.CalcHeight(bodyPartText, bodypartLabelWidth));
			int hediffTotalHeight = 0;

			List<IGrouping<HediffDef, Hediff>> groupings;
			if (replacingPart == null)
			{
				if (settings.tendPrioritySort)
					groupings = diffs.OrderByDescending(i => i.TendableNow(true) ? i.TendPriority : -1).GroupBy(x => x.def).ToList();
				else
					groupings = diffs.GroupBy(x => x.def).ToList();
			}
			else
			{
				if (settings.tendPrioritySort)
					groupings = diffs.Where(x => x != replacingPart).OrderByDescending(i => i.TendableNow(true) ? i.TendPriority : -1).GroupBy(x => x.def).ToList();
				else
					groupings = diffs.Where(x => x != replacingPart).GroupBy(x => x.def).ToList();
			}

			for (int i = 0; i < groupings.Count; i++)
			{
				IGrouping<HediffDef, Hediff> grouping = groupings[i];
				string hediffLabel = GenLabelForHediffGroup(grouping);
				Hediff representativeHediff = grouping.First();
				if (grouping.Count() > 1)
				{
					hediffLabel = hediffLabel + " x" + grouping.Count().ToString();
				}
				if (settings.italicizeMissing && representativeHediff.def == HediffDefOf.MissingBodyPart)
				{
					hediffLabel = hediffLabel.ApplyTag("i");
				}

				int iconsWidth = CalcIconsWidthForGrouping(grouping, showDebugOptions);
				if (iconsWidth > (rowRect.width - column_bodypartWidth) / 2f)
				{
					iconsWidth = (int)((rowRect.width - column_bodypartWidth) / 2f);
				}
				int hediffLabelWidth = (int)(rowRect.width - (column_bodypartWidth + iconsWidth));

				hediffTotalHeight += (int)(Text.CalcHeight(hediffLabel, hediffLabelWidth));

				if (settings.internalSeparator && i < groupings.Count - 1)
					hediffTotalHeight += settings.internalSeparatorHeight;

				if (settings.severityBarMode != CompactHediffs_Settings.SeverityBarMode.Off)
				{
					// 绘制时会遍历分组里的每一个 hediff，行高也必须逐个累加，
					// 不能只看 grouping.First()（同种 hediff 堆叠时原来会少算 n-1 条）。
					foreach (Hediff groupedHediff in grouping)
					{
						hediffTotalHeight += GetSeverityBarHeight(settings, groupedHediff);

						if (settings.showCumulativeThreatment)
							hediffTotalHeight += GetCumulativeBarHeight(settings, groupedHediff);
					}
				}
			}

			int totalHeight = Math.Max(bodyPartLabelHeight, hediffTotalHeight);
			if (settings.horizontalSeparator)
				totalHeight += settings.horizontalSeparatorHeight;
			Rect wholeEntryRect = new Rect(0f, currentY, rowRect.width, totalHeight).Rounded();

			if (settings.evenOddHighlights)
			{
				DoWholeRowHighlight(wholeEntryRect);
			}

			var separatorColor = settings.separatorNightMode ? Color.black : Color.grey;

			if (settings.verticalSeparator)
			{
				GUI.color = separatorColor;
				Rect verticalSeparatorRect = new Rect(bodyPartColumnWidth, currentY, settings.verticalSeparatorWidth, totalHeight).Rounded();
				GUI.DrawTexture(verticalSeparatorRect, TexUI.FastFillTex);
			}

			if (settings.horizontalSeparator)
			{
				GUI.color = separatorColor;
				Rect horizontalSeparatorRect = new Rect(0, currentY, rowRect.width, settings.horizontalSeparatorHeight).Rounded();
				GUI.DrawTexture(horizontalSeparatorRect, TexUI.FastFillTex);
				currentY += settings.horizontalSeparatorHeight;
			}

			if (settings.bodypartHealthbars && part != null)
			{
				Rect fullHealthPercentageRect = new Rect(0, currentY, bodyPartColumnWidth, settings.healthBarHeight).Rounded();

				Color healthColor = GetHealthColorForBodypart(pawn, part);
				float partMaxHealth = GetPartMaxHealth(pawn, part);
				float partHealthFraction = partMaxHealth > 0f ? pawn.health.hediffSet.GetPartHealth(part) / partMaxHealth : 1f;

				if (partHealthFraction < 1f)
				{
					GUI.color = Color.gray;
					GUI.DrawTexture(fullHealthPercentageRect, Textures.translucentWhite);
					if (partHealthFraction == 0)
					{
						GUI.color = MissingBodyPart;
						GUI.DrawTexture(fullHealthPercentageRect, TexUI.FastFillTex);
					}
					else
					{
						GUI.color = healthColor;
						Rect healthPercentageRect = new Rect(0, currentY, bodyPartColumnWidth * partHealthFraction, settings.healthBarHeight);
						GUI.DrawTexture(healthPercentageRect, Textures.translucentWhite);
					}
				}
			}

			int tooltipIDOffset = 0;

			Widgets.Label(new Rect(rowLeftPad, currentY, bodypartLabelWidth, 100f), bodyPartText);

			if (part != null)
			{
				if (getTooltip != null)
				{
					TooltipHandler.TipRegion(wholeEntryRect, new TipSignal(() => getTooltip(pawn, part), (int)currentY + 7857 + tooltipIDOffset, TooltipPriority.Pawn));
					tooltipIDOffset++;
				}

				if (replacingPart != null)
				{
					GUI.color = Color.white;
					int iconOffset = (int)Math.Max((bodyPartLabelHeight - IconHeight) / 2f, 0);
					Rect iconRect = new Rect(bodypartLabelWidth, currentY + iconOffset, IconHeight / 2f, IconHeight).Rounded();
					CustomInfoCardButtonWidget.CustomInfoCardButton(iconRect, replacingPart);

					var justColumnRect = wholeEntryRect.LeftPartPixels(column_bodypartWidth);
					if (Mouse.IsOver(justColumnRect))
					{
						TooltipHandler.TipRegion(justColumnRect, new TipSignal(() => replacingPart.GetTooltip(pawn, ShowHediffsDebugInfo), (int)currentY + 7857 + tooltipIDOffset, TooltipPriority.Pawn));
						tooltipIDOffset++;

						if (Pawnmorpher.Active && Pawnmorpher.GetTooltip != null)
						{
							//copied from Pawnmorph.PatchHealthCardUtilityDrawHediffRow
							string tooltip = Pawnmorpher.GetTooltip(new List<Hediff>() { replacingPart });
							if (!string.IsNullOrWhiteSpace(tooltip))
							{
								TooltipHandler.TipRegion(justColumnRect, new TipSignal(() => tooltip, (int)currentY + 117857 + tooltipIDOffset));
								tooltipIDOffset++;
							}
						}
					}
				}
			}

			int innerY = 0;

			GUI.color = Color.white;

			for (int i = 0; i < groupings.Count; i++)
			{
				IGrouping<HediffDef, Hediff> grouping = groupings[i];
				string hediffLabel = GenLabelForHediffGroup(grouping);
				var hediffsByPriority = grouping
					.OrderByDescending(x => x.TendableNow(true) ? x.TendPriority : -1)
					.ToList();

				Hediff representativeHediff = grouping.First();
				if (grouping.Count() > 1)
				{
					hediffLabel = hediffLabel + " x" + grouping.Count().ToString();
				}
				if (settings.italicizeMissing && representativeHediff.def == HediffDefOf.MissingBodyPart)
				{
					hediffLabel = hediffLabel.ApplyTag("i");
				}

				int iconsWidth = CalcIconsWidthForGrouping(grouping, showDebugOptions);
				float iconOverlap = 1f;
				if (iconsWidth > (rowRect.width - column_bodypartWidth) / 2f)
				{
					iconOverlap = ((rowRect.width - column_bodypartWidth) / 2f) / (float)iconsWidth;
					iconsWidth = (int)((rowRect.width - column_bodypartWidth) / 2f);
				}
				int hediffLabelWidth = (int)((rowRect.width - column_bodypartWidth) - iconsWidth);

				int hediffTextHeight = (int)Text.CalcHeight(hediffLabel, hediffLabelWidth);

				int hediffColumnWidth = (int)(rowRect.width - column_bodypartWidth);

				if (settings.severityBarMode != CompactHediffs_Settings.SeverityBarMode.Off && settings.severityBarsPosition == CompactHediffs_Settings.BarPosition.Above)
				{
					foreach (Hediff hediff in grouping)
					{
						Rect barRect = new Rect(column_bodypartWidth, currentY + innerY, hediffColumnWidth, settings.internalBarHeight).Rounded();
						innerY += DrawSeverityBar(settings, barRect, hediff);
						if (settings.showCumulativeThreatment)
						{
							Rect cumulativeBarRect = new Rect(column_bodypartWidth, currentY + innerY, hediffColumnWidth, settings.internalBarHeight).Rounded();
							innerY += DrawCumulativeThreatmentBar(settings, cumulativeBarRect, hediff);
						}
					}
				}

				Rect fullHediffRect = new Rect(column_bodypartWidth, currentY + innerY, rowRect.width - column_bodypartWidth, hediffTextHeight).Rounded();
				Rect hediffLabelrect = new Rect(column_bodypartWidth, currentY + innerY, hediffLabelWidth, hediffTextHeight).Rounded();

				//do tooltips
				if (Mouse.IsOver(fullHediffRect))
				{
					List<string> uniqueTooltips = new List<string>();
					foreach (var individualHediff in grouping)
					{
						string tooltip = individualHediff.GetTooltip(pawn, ShowHediffsDebugInfo);
						if (!string.IsNullOrWhiteSpace(tooltip))
							uniqueTooltips.Add(tooltip);

						if (Pawnmorpher.Active && Pawnmorpher.GetTooltip != null)
						{
							//copied from Pawnmorph.PatchHealthCardUtilityDrawHediffRow
							tooltip = Pawnmorpher.GetTooltip(new List<Hediff>() { individualHediff });
							if (!string.IsNullOrWhiteSpace(tooltip))
								uniqueTooltips.Add(tooltip);
						}
					}

					foreach (var tooltip in uniqueTooltips)
					{
						TooltipHandler.TipRegion(fullHediffRect, new TipSignal(() => tooltip, (int)currentY + 117857 + tooltipIDOffset));
						tooltipIDOffset++;
					}
				}

				GUI.color = GetHediffColor(settings, representativeHediff);
				//this is where smartMedicine transpiles its float menu into, so lets follow suit
				Widgets.Label(hediffLabelrect, hediffLabel);
				GUI.color = Color.white;
				if (SmartMedicine.Active)
				{
					UI_SmartMedicine.AddSmartMedicineFloatMenuButton(fullHediffRect, hediffsByPriority);
				}

				float widthAccumulator = 0;

				int iconOffset = (int)Math.Max((fullHediffRect.height - IconHeight) / 2f, 0);

				//draw dev buttons
				if (showDebugOptions)
				{
					{
						Rect iconRect = new Rect(rowRect.width - DevRemoveButtonWidth, fullHediffRect.y + iconOffset, DevRemoveButtonWidth, DevRemoveButtonWidth).Rounded();
						TooltipHandler.TipRegion(iconRect, () => "DEV: Remove hediff", 1071045645);
						GUI.color = Color.red;
						if (GUI.Button(iconRect, TexButton.Delete))
						{
							foreach (var hediff in grouping)
								pawn.health.RemoveHediff(hediff);
						}
						widthAccumulator += iconRect.width;
					}
					if (grouping.Count() == 1)
					{
						var singleHediff = grouping.First();
						if (singleHediff.def.maxSeverity < float.MaxValue || singleHediff.def.lethalSeverity > 0f)
						{
							Rect iconRect = new Rect(rowRect.width - (widthAccumulator + DevRemoveButtonWidth), fullHediffRect.y + iconOffset, DevRemoveButtonWidth, DevRemoveButtonWidth).Rounded();
							GUI.color = Color.cyan;
							TooltipHandler.TipRegion(iconRect, () => "DEV: Set severity", 2131648723);
							if (GUI.Button(iconRect, TexButton.Save))
							{
								Find.WindowStack.Add(new Dialog_DebugSetSeverity(singleHediff));
							}
							widthAccumulator += iconRect.width;
						}
						if (singleHediff.TryGetComp<HediffComp_Disappears>() != null)
						{
							Rect iconRect = new Rect(rowRect.width - (widthAccumulator + DevRemoveButtonWidth), fullHediffRect.y + iconOffset, DevRemoveButtonWidth, DevRemoveButtonWidth).Rounded();
							GUI.color = Color.yellow;
							TooltipHandler.TipRegion(iconRect, () => "DEV: Set remaining time", 6234623);
							if (GUI.Button(iconRect, TexButton.Save))
							{
								Find.WindowStack.Add(new Dialog_DebugSetHediffRemaining(singleHediff));
							}
							widthAccumulator += iconRect.width;
						}
					}
				}

				//draw info button
				{
					GUI.color = Color.white;
					Rect iconRect = new Rect(rowRect.width - (widthAccumulator + (IconHeight / 2f)), fullHediffRect.y + iconOffset, IconHeight / 2f, IconHeight).Rounded();
					CustomInfoCardButtonWidget.CustomInfoCardButton(iconRect, representativeHediff);
					widthAccumulator += iconRect.width;
				}

				var hediffsWithStateIcon = hediffsByPriority.Where(x => x.StateIcon.HasValue);
				//draw non-injury icons first
				foreach (Hediff localHediff in hediffsWithStateIcon.Where(x => x.StateIcon.Texture != Textures.Vanilla_TendedIcon_Well_Injury))
				{
					var hediffStateIcon = localHediff.StateIcon;
					GUI.color = hediffStateIcon.Color;
					int deafultIconOffset = (int)Math.Max((fullHediffRect.height - DefaultIconHeight) / 2f, 0);
					Rect iconRect = new Rect(rowRect.width - (widthAccumulator + DefaultIconHeight), fullHediffRect.y + deafultIconOffset, DefaultIconHeight, DefaultIconHeight).Rounded();
					GUI.DrawTexture(iconRect, hediffStateIcon.Texture);
					widthAccumulator += iconRect.width * iconOverlap;
				}
				//draw tended injuries
				foreach (Hediff localHediff in hediffsWithStateIcon.Where(x => x.StateIcon.Texture == Textures.Vanilla_TendedIcon_Well_Injury).OrderByDescending(x => x.TryGetComp<HediffComp_TendDuration>()?.tendQuality ?? -1f))
				{
					var hediffStateIcon = localHediff.StateIcon;
					GUI.color = hediffStateIcon.Color;
					Rect iconRect;
					if (settings.tendingIcons)
					{
						iconRect = new Rect(rowRect.width - (widthAccumulator + TendIconWidth), fullHediffRect.y + iconOffset, TendIconWidth, IconHeight).Rounded();
						DrawCustomTendingIcon(iconRect, localHediff);
					}
					else
					{
						iconRect = new Rect(rowRect.width - (widthAccumulator + IconHeight), fullHediffRect.y + iconOffset, IconHeight, IconHeight).Rounded();
						GUI.DrawTexture(iconRect, hediffStateIcon.Texture);
					}
					widthAccumulator += iconRect.width * iconOverlap;
				}
				//draw bleeding injuries
				GUI.color = Color.white;
				foreach (Hediff localHediff in hediffsByPriority.Where(x => x.Bleeding).OrderByDescending(x => x.BleedRate))
				{
					if (localHediff.Bleeding)
					{
						Rect iconRect;
						if (settings.bleedingIcons)
						{
							iconRect = new Rect(rowRect.width - (widthAccumulator + BleedIconWidth), fullHediffRect.y + iconOffset, BleedIconWidth, IconHeight).Rounded();
							DrawCustomBleedIcon(iconRect, localHediff);
						}
						else
						{
							iconRect = new Rect(rowRect.width - (widthAccumulator + IconHeight), fullHediffRect.y + iconOffset, IconHeight, IconHeight).Rounded();
							GUI.DrawTexture(iconRect.ContractedBy(GenMath.LerpDouble(0f, 0.6f, 5f, 0f, Mathf.Min(localHediff.BleedRate, 1f))), Textures.Vanilla_BleedingIcon);
						}
						widthAccumulator += iconRect.width * iconOverlap;
					}
					else
					{
						break;
					}
				}

				//draw other mod icon
				GUI.color = Color.white;
				if (SmartMedicine.Active && SmartMedicine.GetElementsByList != null)
				{
					Rect iconRect = new Rect(rowRect.width - (widthAccumulator + SmartMedicineIconWidth), fullHediffRect.y + iconOffset, rowRect.width, SmartMedicineIconHeight).Rounded();
					UI_SmartMedicine.DrawSmartMedicineIcon(iconRect, hediffsByPriority, ref widthAccumulator, SmartMedicineIconWidth);
				}
				if (ChooseYourMedicine.Active && ChooseYourMedicine.DrawButtonToAssignMedManually != null)
				{
					Rect iconRect = new Rect(rowRect.width - (widthAccumulator + ChooseYourMedicine.IconWidth), fullHediffRect.y + iconOffset, ChooseYourMedicine.IconWidth, ChooseYourMedicine.IconHeight).Rounded();
					ChooseYourMedicine.DrawButtonToAssignMedManually(iconRect, pawn, grouping);
				}

				innerY += hediffTextHeight;

				if (settings.severityBarMode != CompactHediffs_Settings.SeverityBarMode.Off && settings.severityBarsPosition == CompactHediffs_Settings.BarPosition.Below)
				{
					foreach (Hediff hediff in grouping)
					{
						Rect barRect = new Rect(column_bodypartWidth, currentY + innerY, hediffColumnWidth, settings.internalBarHeight).Rounded();
						int extraHeight = DrawSeverityBar(settings, barRect, hediff);
						innerY += extraHeight;
						if (settings.showCumulativeThreatment)
						{
							Rect cumulativeBarRect = new Rect(column_bodypartWidth, currentY + innerY, hediffColumnWidth, settings.internalBarHeight).Rounded();
							innerY += DrawCumulativeThreatmentBar(settings, cumulativeBarRect, hediff);
						}
						fullHediffRect.height += extraHeight;
					}
				}

				if (settings.internalSeparator && i < groupings.Count - 1)
				{
					Rect internalSeparatorRect = new Rect(column_bodypartWidth, currentY + innerY, (rowRect.width - column_bodypartWidth), settings.internalSeparatorHeight).Rounded();
					GUI.color = separatorColor;
					GUI.DrawTexture(internalSeparatorRect, TexUI.FastFillTex);
					innerY += settings.internalSeparatorHeight;
					fullHediffRect.height += settings.internalSeparatorHeight;
				}
			}
			GUI.color = Color.white;
			currentY += Math.Max(bodyPartLabelHeight, hediffTotalHeight);

			if (canEntryBeClicked != null && entryClicked != null)
			{
				if (Widgets.ButtonInvisible(wholeEntryRect, canEntryBeClicked(diffs, pawn)))
				{
					entryClicked(diffs, pawn);
				}
			}

			curY = (float)currentY;
		}

		public static Hediff? GetReplacingPart(IEnumerable<Hediff> hediffs)
		{
			var replacingPart = hediffs.Where(x => x is Hediff_AddedPart).FirstOrDefault();
			if (replacingPart != null)
				return replacingPart;

			if (Pawnmorpher.Active && Pawnmorpher.CoreMutationHediffDefs != null)
			{
				foreach (var hediff in hediffs)
				{
					if (Pawnmorpher.CoreMutationHediffDefs.Contains(hediff.def))
						return hediff;
				}
			}

			return null;
		}

		public static string MakeBodyPartText(Pawn pawn, BodyPartRecord? part, Hediff? replacingPart)
		{
			string bodyPartText;
			if (part == null)
			{
				bodyPartText = ColoredText.Colorize("WholeBody".Translate(), Color.grey);
			}
			else
			{
				Color healthColor = GetHealthColorForBodypart(pawn, part);
				if (replacingPart != null)
				{
					var replacingPartColorLabel = ColoredText.Colorize(replacingPart.Label, replacingPart.def.defaultLabelColor);
					var regex = new Regex(@"\b" + part.def.label + @"\b");
					if (regex.IsMatch(replacingPart.Label) && regex.IsMatch(part.Label))
					{
						replacingPartColorLabel = replacingPartColorLabel.Replace(part.def.label, ColoredText.Colorize(part.def.label, healthColor));
						bodyPartText = ColoredText.Colorize(part.Label.Replace(part.def.label, replacingPartColorLabel), healthColor);
					}
					else
					{
						bodyPartText = ColoredText.Colorize(part.Label + ", ", healthColor) + replacingPartColorLabel;
					}
				}
				else
				{
					bodyPartText = ColoredText.Colorize(part.Label, healthColor);
				}
			}

			bodyPartText = bodyPartText.CapitalizeFirstNestingAware();
			return bodyPartText;
		}

		private static float GetPartMaxHealth(Pawn pawn, BodyPartRecord part)
		{
			if (!EBF.Active || EBF.GetMaxHealth_Cached == null)
				return part.def.GetMaxHealth(pawn);

			return EBF.GetMaxHealth_Cached(part.def, pawn, part);
		}

		private static int CalcIconsWidthForGrouping(IGrouping<HediffDef, Hediff> grouping, bool showDebugOptions)
		{
			int iconsWidth = InfoIconWidth;
			if (showDebugOptions)
				iconsWidth += DevRemoveButtonWidth;

			foreach (Hediff diff in grouping)
			{
				if (diff.StateIcon.HasValue)
				{
					if (CompactHediffsMod.Settings.tendingIcons && diff.StateIcon.Texture == Textures.Vanilla_TendedIcon_Well_General)
						iconsWidth += TendIconWidth;
					else
						iconsWidth += IconHeight;
				}
				if (diff.Bleeding)
				{
					if (CompactHediffsMod.Settings.bleedingIcons)
						iconsWidth += BleedIconWidth;
					else
						iconsWidth += IconHeight;
				}
				if (SmartMedicine.Active)
				{
					iconsWidth += SmartMedicineIconWidth;
				}
				if (ChooseYourMedicine.Active)
				{
					iconsWidth += ChooseYourMedicine.IconWidth;
				}
			}
			return iconsWidth;
		}

		private static string GenLabelForHediffGroup(IGrouping<HediffDef, Hediff> grouping)
		{
			string fullLabel = grouping.First().LabelCap;
			bool mismatch = false;
			foreach (Hediff hediff in grouping)
			{
				if (hediff.LabelCap != fullLabel)
				{
					mismatch = true;
					break;
				}
			}
			if (!mismatch)
				return fullLabel;

			return grouping.First().LabelBaseCap;
		}

		private static void DrawCustomTendingIcon(Rect iconRect, Hediff hediff)
		{
			float quality = hediff.TryGetComp<HediffComp_TendDuration>()?.tendQuality ?? 0f;
			Texture2D tendIcon;
			if (quality <= 0.01)
				tendIcon = Textures.TendingIcon_0;
			else if (quality <= 0.25)
				tendIcon = Textures.TendingIcon_1;
			else if (quality < 0.5)
				tendIcon = Textures.TendingIcon_2;
			else if (quality < 0.75)
				tendIcon = Textures.TendingIcon_3;
			else if (quality < 1.0)
				tendIcon = Textures.TendingIcon_4;
			else
				tendIcon = Textures.TendingIcon_5;

			GUI.DrawTexture(iconRect, tendIcon);
		}

		private static void DrawCustomBleedIcon(Rect iconRect, Hediff hediff)
		{
			float bleedRate = hediff.BleedRate;
			float brightnessPulse = Pulser.PulseBrightness(bleedRate, 0.25f);
			GUI.color = new Color(1f, 1f, 1f, brightnessPulse);

			Texture2D bleedIcon;
			if (bleedRate <= 0.1)
				bleedIcon = Textures.BleedingIcon_0;
			else if (bleedRate < 0.25)
				bleedIcon = Textures.BleedingIcon_1;
			else if (bleedRate < 0.5)
				bleedIcon = Textures.BleedingIcon_2;
			else if (bleedRate < 1.25)
				bleedIcon = Textures.BleedingIcon_3;
			else if (bleedRate < 2.25)
				bleedIcon = Textures.BleedingIcon_4;
			else
				bleedIcon = Textures.BleedingIcon_5;

			GUI.DrawTexture(iconRect, bleedIcon);
		}

		private static Color GetHediffColor(CompactHediffs_Settings settings, Hediff hediff)
		{
			if (settings.bloodlossSpecialHandling && hediff.def == HediffDefOf.BloodLoss)
			{
				if (hediff.Severity < 0.65f)
					return new Color(1f, 1f - (hediff.Severity / 0.65f), 1f - (hediff.Severity / 0.65f));

				var factor = ((hediff.Severity - 0.65f) / 0.35f);
				return new Color(1f - (0.5f * factor), 0f, 0.25f * factor);
			}
			return hediff.LabelColor;
		}

		/// <summary>
		/// 严重度条画不画、画多高，只由这一个判断决定。
		/// 行高预估（<see cref="GetSeverityBarHeight"/>）和实际绘制（<see cref="DrawSeverityBar"/>）都走它，
		/// 否则两条条件链一旦分叉，行高就会和实际内容对不上（多留空白或压住下一行）。
		/// </summary>
		private static bool ShouldDrawSeverityBar(CompactHediffs_Settings settings, Hediff hediff, out float severityFraction, out float immunityFraction)
		{
			severityFraction = 0f;
			immunityFraction = 0f;

			if (settings.severityBarMode == CompactHediffs_Settings.SeverityBarMode.Off)
				return false;

			if (!settings.showHiddenProgressConditions && hediff.SeverityLabel == null)
				return false;

			severityFraction = GetSeverityFraction(hediff);

			var immunizableComp = hediff.TryGetComp<HediffComp_Immunizable>();
			if (immunizableComp != null && immunizableComp.Immunity > 0.001f)
				immunityFraction = immunizableComp.Immunity;

			return severityFraction > 0.001f || immunityFraction > 0.001f;
		}

		private static int GetSeverityBarHeight(CompactHediffs_Settings settings, Hediff hediff)
		{
			return ShouldDrawSeverityBar(settings, hediff, out _, out _) ? settings.internalBarHeight : 0;
		}

		/// <summary>累计治疗条同理：只有 tendDuration 配置了 disappearsAtTotalTendQuality 才有一条。</summary>
		private static bool ShouldDrawCumulativeBar(CompactHediffs_Settings settings, Hediff hediff)
		{
			if (settings.severityBarMode == CompactHediffs_Settings.SeverityBarMode.Off
				|| !settings.showCumulativeThreatment)
				return false;

			var tendDurationComp = hediff.TryGetComp<HediffComp_TendDuration>();
			return tendDurationComp != null && tendDurationComp.TProps.disappearsAtTotalTendQuality > 0;
		}

		private static int GetCumulativeBarHeight(CompactHediffs_Settings settings, Hediff hediff)
		{
			return ShouldDrawCumulativeBar(settings, hediff) ? settings.internalBarHeight : 0;
		}

		private static int DrawSeverityBar(CompactHediffs_Settings settings, Rect barRect, Hediff hediff)
		{
			if (!ShouldDrawSeverityBar(settings, hediff, out float severityFraction, out float immunityFraction))
				return 0;

			bool hasImmunity = immunityFraction > 0.001f;
			bool hasSeverity = severityFraction > 0.001f;

			var tendDurationComp = hediff.TryGetComp<HediffComp_TendDuration>();

			Color hediffColor = GetHediffColor(settings, hediff);

			GUI.color = Color.black;
			GUI.DrawTexture(barRect, Textures.translucentWhite);

			Rect? commonRect = null;
			Rect? commonRectMirror = null;
			Color commonColor = settings.severityBarHighContrast ? Color.white : hediffColor;
			Rect? severityRect = null;
			Rect? severityRectMirror = null;
			Color severityColor = settings.severityBarHighContrast ? Color.red : hediffColor;
			Rect? immunityRect = null;
			Rect? immunityRectMirror = null;
			Color immunityColor = settings.severityBarHighContrast ? BluishGreen : CyanWhite;
			Texture2D commonTexture = TexUI.FastFillTex;
			Texture2D severityTexture = Textures.Bar_Infected;
			Texture2D immunityTexture = Textures.Bar_Pill;

			switch (settings.severityBarMode)
			{
				case CompactHediffs_Settings.SeverityBarMode.LeftToRight:
					if (immunityFraction > severityFraction)
					{
						severityRect = new Rect(barRect.x, barRect.y, barRect.width * severityFraction, settings.internalBarHeight).Rounded();
						immunityRect = new Rect(barRect.x + severityRect.Value.width, barRect.y, barRect.width * (immunityFraction - severityFraction), settings.internalBarHeight).Rounded();
					}
					else if (hasImmunity)
					{
						immunityRect = new Rect(barRect.x, barRect.y, barRect.width * immunityFraction, settings.internalBarHeight).Rounded();
						severityRect = new Rect(barRect.x + immunityRect.Value.width, barRect.y, barRect.width * (severityFraction - immunityFraction), settings.internalBarHeight).Rounded();
					}
					else
					{
						commonRect = new Rect(barRect.x, barRect.y, barRect.width * severityFraction, settings.internalBarHeight).Rounded();
					}
					break;
				case CompactHediffs_Settings.SeverityBarMode.EdgeToMiddle:
					{
						var midRect = new Rect(barRect.x + ((barRect.width / 2f) - 1f), barRect.y, 2f, settings.internalBarHeight).Rounded();
						GUI.color = settings.separatorNightMode ? Color.black : Color.grey;
						GUI.DrawTexture(midRect, TexUI.FastFillTex);

						if (hasImmunity)
						{
							int severityWidth = (int)((barRect.width / 2f) * severityFraction);
							severityRect = new Rect(barRect.x, barRect.y, severityWidth, settings.internalBarHeight).Rounded();
							int immunityWidth = (int)((barRect.width / 2f) * immunityFraction);
							immunityRect = new Rect(barRect.x + (barRect.width - immunityWidth), barRect.y, immunityWidth, settings.internalBarHeight).Rounded();
						}
						else
						{
							int commonWidth = (int)((barRect.width / 2f) * severityFraction);
							commonRect = new Rect(barRect.x, barRect.y, commonWidth, settings.internalBarHeight).Rounded();
							commonRectMirror = new Rect(barRect.x + (barRect.width - commonWidth), barRect.y, commonWidth, settings.internalBarHeight).Rounded();
						}
					}
					break;
				case CompactHediffs_Settings.SeverityBarMode.MiddleToEdge:
					{
						if (hasImmunity)
						{
							int severityWidth = (int)((barRect.width / 2f) * severityFraction);
							severityRect = new Rect(barRect.x + ((barRect.width / 2f) - severityWidth), barRect.y, severityWidth, settings.internalBarHeight).Rounded();
							int immunityWidth = (int)((barRect.width / 2f) * immunityFraction);
							immunityRect = new Rect(barRect.x + (barRect.width / 2f), barRect.y, immunityWidth, settings.internalBarHeight).Rounded();
						}
						else
						{
							int commonWidth = (int)((barRect.width / 2f) * severityFraction);
							commonRect = new Rect(barRect.x + ((barRect.width / 2f) - commonWidth), barRect.y, commonWidth, settings.internalBarHeight).Rounded();
							commonRectMirror = new Rect(barRect.x + (barRect.width / 2f), barRect.y, commonWidth, settings.internalBarHeight).Rounded();
						}
					}
					break;
			}

			if (settings.bloodlossSpecialHandling && hediff.def == HediffDefOf.BloodLoss)
			{
				var bloodlossSpeed = Math.Min(hediff.pawn.health.hediffSet.BleedRateTotal, 4f);
				if (bloodlossSpeed >= 0.1f)
				{
					var alphaMult = Pulser.PulseBrightness(0.5f + bloodlossSpeed * 0.75f, 0.5f);
					commonColor.a *= alphaMult;
				}
				commonColor = hediffColor;
				commonTexture = Textures.Bar_Ragged;
			}
			else
			{
				bool canBeTendedNow = !hediff.IsPermanent() && !hediff.pawn.Dead && hediff.TendableNow(false);
				bool needsTendingNow = canBeTendedNow && tendDurationComp != null && tendDurationComp.tendTicksLeft <= 0;
				if (canBeTendedNow)
				{
					var alphaMult = needsTendingNow ? Pulser.PulseBrightness(2f, 0.5f) : Pulser.PulseBrightness(1f, 0.5f);
					commonColor.a *= alphaMult;
					severityColor.a *= alphaMult;
					immunityColor.a *= alphaMult;
				}
			}

			if (!settings.severityBarTextured)
			{
				commonTexture = TexUI.FastFillTex;
				severityTexture = TexUI.FastFillTex;
				immunityTexture = TexUI.FastFillTex;
			}
			else if (hediff.def == HediffDefOf.Malnutrition)
			{
				commonTexture = Textures.Bar_Malnutrition;
				severityTexture = Textures.Bar_Malnutrition;
			}
			else if (ModsConfig.AnomalyActive && (hediff.def == HediffDefOf.CubeComa || hediff.def == HediffDefOf.CubeInterest || hediff.def == HediffDefOf.CubeRage || hediff.def == HediffDefOf.CubeWithdrawal))
			{
				commonTexture = Textures.Bar_Cubes;
				severityTexture = Textures.Bar_Cubes;
			}
			else if (Pawnmorpher.Active && Pawnmorpher.PawnmorpherHediffDefs != null && Pawnmorpher.PawnmorpherHediffDefs.Contains(hediff.def))
			{
				commonTexture = Textures.Bar_DNA;
				severityTexture = Textures.Bar_DNA;
			}

			if (!settings.severityBarHighContrast)
			{
				if (commonTexture == TexUI.FastFillTex)
					commonColor.a *= 0.75f;
				if (severityTexture == TexUI.FastFillTex)
					severityColor.a *= 0.75f;
				if (immunityTexture == TexUI.FastFillTex)
					immunityColor.a *= 0.75f;
			}

			DrawBarSegment(commonRect, commonTexture, commonColor, settings.internalBarHeight);
			DrawBarSegment(commonRectMirror, commonTexture, commonColor, settings.internalBarHeight);
			DrawBarSegment(severityRect, severityTexture, severityColor, settings.internalBarHeight);
			DrawBarSegment(severityRectMirror, severityTexture, severityColor, settings.internalBarHeight);
			DrawBarSegment(immunityRect, immunityTexture, immunityColor, settings.internalBarHeight);
			DrawBarSegment(immunityRectMirror, immunityTexture, immunityColor, settings.internalBarHeight);

			GUI.color = Color.white;
			return settings.internalBarHeight;
		}

		private static void DrawBarSegment(Rect? rect, Texture2D texture, Color color, int barHeight)
		{
			if (!rect.HasValue || barHeight <= 0)
				return;

			GUI.color = color;
			var drawnRect = rect.Value;
			float texScale = texture.height / (float)barHeight;
			GUI.DrawTextureWithTexCoords(drawnRect, texture, new Rect(0, 0, texScale * (drawnRect.width / texture.width), texScale * (drawnRect.height / texture.height)));
		}

		private static int DrawCumulativeThreatmentBar(CompactHediffs_Settings settings, Rect barRect, Hediff hediff)
		{
			if (!ShouldDrawCumulativeBar(settings, hediff))
				return 0;

			var tendDurationComp = hediff.TryGetComp<HediffComp_TendDuration>()!;
			int tendQualityRequired = tendDurationComp.TProps.disappearsAtTotalTendQuality;

			var tendedFraction = tendDurationComp.GetTotalTendQuality() / tendQualityRequired;
			if (tendedFraction > 1)
				tendedFraction = 1f;

			GUI.color = Color.black;
			GUI.DrawTexture(barRect, Textures.translucentWhite);

			Rect? immunityRect = null;
			Rect? immunityRectMirror = null;
			Color immunityColor = settings.severityBarHighContrast ? BluishGreen : CyanWhite;
			Texture2D immunityTexture = Textures.Bar_PillBig;

			if (!settings.severityBarHighContrast)
			{
				immunityColor.a *= 0.75f;
			}

			switch (settings.severityBarMode)
			{
				case CompactHediffs_Settings.SeverityBarMode.LeftToRight:
					immunityRect = new Rect(barRect.x, barRect.y, barRect.width * tendedFraction, settings.internalBarHeight).Rounded();
					break;
				case CompactHediffs_Settings.SeverityBarMode.EdgeToMiddle:
					{
						var midRect = new Rect(barRect.x + ((barRect.width / 2f) - 1f), barRect.y, 2f, settings.internalBarHeight).Rounded();
						GUI.color = settings.separatorNightMode ? Color.black : Color.grey;
						GUI.DrawTexture(midRect, TexUI.FastFillTex);

						var width = barRect.width * (tendedFraction / 2f);
						immunityRect = new Rect(barRect.x, barRect.y, width, settings.internalBarHeight).Rounded();
						immunityRectMirror = new Rect(barRect.x + (barRect.width - width), barRect.y, width, settings.internalBarHeight).Rounded();
					}
					break;
				case CompactHediffs_Settings.SeverityBarMode.MiddleToEdge:
					{
						var half = barRect.width / 2f;
						immunityRect = new Rect(barRect.x + half - (half * tendedFraction), barRect.y, barRect.width * tendedFraction, settings.internalBarHeight).Rounded();
					}
					break;
			}

			bool canBeTendedNow = !hediff.IsPermanent() && !hediff.pawn.Dead && hediff.TendableNow(false);
			bool needsTendingNow = canBeTendedNow && tendDurationComp.tendTicksLeft <= 0;
			if (canBeTendedNow)
			{
				var alphaMult = needsTendingNow ? Pulser.PulseBrightness(2f, 0.5f) : Pulser.PulseBrightness(1f, 0.5f);
				immunityColor.a *= alphaMult;
			}

			if (!settings.severityBarTextured)
			{
				immunityTexture = TexUI.FastFillTex;
			}

			DrawBarSegment(immunityRect, immunityTexture, immunityColor, settings.internalBarHeight);
			DrawBarSegment(immunityRectMirror, immunityTexture, immunityColor, settings.internalBarHeight);

			GUI.color = Color.white;
			return settings.internalBarHeight;
		}

		/// <summary>
		/// 该 hediff 的严重度上限；返回 0 表示“没有上限信息”，此时不画严重度条。
		/// 普通伤口（Cut/Gunshot/...）的 def 既没有 lethalSeverity 也没有 maxSeverity，
		/// 原版 1.6 的写法靠返回 -1 让 fraction 变负再夹到 0 来达到同样的效果。
		/// </summary>
		private static float GetMaxSeverityForHediff(Hediff hediff)
		{
			if (hediff.def.lethalSeverity > 0f)
				return hediff.def.lethalSeverity;

			if (hediff.def.maxSeverity > 0f && hediff.def.maxSeverity < (float.MaxValue - float.Epsilon))
				return hediff.def.maxSeverity;

			if (hediff is Hediff_Pregnant)
				return 1f;

			return 0f;
		}

		/// <summary>严重度占比。没有上限（如普通伤口）或占比为负时返回 0。</summary>
		private static float GetSeverityFraction(Hediff hediff)
		{
			float maxSeverity = GetMaxSeverityForHediff(hediff);
			if (maxSeverity <= 0f)
				return 0f;

			float fraction = hediff.Severity / maxSeverity;
			return fraction < 0f ? 0f : fraction;
		}

		private static Color GetHealthColorForBodypart(Pawn pawn, BodyPartRecord part)
		{
			if (pawn == null)
				return Color.white;

			return HealthUtility.GetPartConditionLabel(pawn, part).Second;
		}

		public static void DoWholeRowHighlight(Rect rowRect)
		{
			if (highlight != null)
			{
				if (highlight.Invoke())
				{
					GUI.color = StaticHighlightColor;
					GUI.DrawTexture(rowRect, TexUI.HighlightTex);
				}
				highlight.Invoke() = !highlight.Invoke();
			}

			if (Mouse.IsOver(rowRect))
			{
				GUI.color = HighlightColor;
				GUI.DrawTexture(rowRect, TexUI.HighlightTex);
			}
		}

		public static IEnumerable<IGrouping<BodyPartRecord, Hediff>> ReorderHediffGroups(IEnumerable<IGrouping<BodyPartRecord, Hediff>> returned, Pawn pawn, bool showBloodLoss)
		{
			if (getListPriority == null)
				return returned.OrderByDescending(x => x.Max(i => i.TendableNow(true) ? i.TendPriority : -1));

			return returned
				.OrderByDescending(x => x.Max(i => i.TendableNow(true) ? i.TendPriority : -1))
				.ThenByDescending(i => getListPriority(i.First().Part));
		}
	}
}
