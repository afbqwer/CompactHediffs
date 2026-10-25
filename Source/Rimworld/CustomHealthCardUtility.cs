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

		/// <summary>HealthCardUtility.scrollPosition，用于滚动裁剪；解析失败时不做裁剪。</summary>
		private static AccessTools.FieldRef<Vector2>? scrollPositionField;

		/// <summary>核心私有成员至少解析成功一个，才值得接管原版行绘制。</summary>
		public static bool Available { get; private set; }

		private static bool ShowHediffsDebugInfo => showHediffsDebugInfo != null && showHediffsDebugInfo.Invoke();

		// ===== 性能相关（本项目每帧会走 Layout + Repaint 两遍，且每行都重复绘制） =====

		/// <summary>
		/// 单个分组的排版结果。测量阶段算一次、绘制阶段直接用，
		/// 避免同一行在同一帧里把标签字符串、图标宽度、文本高度算两遍。
		/// </summary>
		private struct GroupLayout
		{
			public string Label;
			public int IconsWidth;
			public float IconOverlap;
			public int TextHeight;
			public int Count;
		}

		private static readonly List<GroupLayout> groupLayouts = new List<GroupLayout>();

		/// <summary>
		/// 一个已经定好贴图与颜色的图标。收集阶段算完，绘制阶段直接画 —— 不再回读 StateIcon。
		/// </summary>
		private struct IconEntry
		{
			public Texture2D Texture;
			public Color Color;
			/// <summary>仅“原版流血图标”分支用来算缩进（按流血速度）。</summary>
			public float BleedRate;
			/// <summary>排序用：包扎图标取治疗质量、流血图标取流血速度。</summary>
			public float SortKey;
			/// <summary>这种图标代表多少条 hediff（按比例分配时当权重用）。</summary>
			public int Weight;
			/// <summary>实际要画几个。1 = 每条一个；压缩后由 <see cref="AllocateIconCounts"/> 填。</summary>
			public int Count;
		}

		/// <summary>一个分组最多画多少个图标。</summary>
		private const int MaxIconsPerGroup = 20;

		private struct IconSlot
		{
			public List<IconEntry> List;
			public int Index;
		}

		// 图标收集用的复用缓冲。UI 绘制是单线程的，且这些列表在同一轮迭代内立刻消费掉。
		private static readonly List<IconEntry> tmpStateIcons = new List<IconEntry>();
		private static readonly List<IconEntry> tmpTendedIcons = new List<IconEntry>();
		private static readonly List<IconEntry> tmpBleedingIcons = new List<IconEntry>();
		private static readonly List<IconSlot> tmpIconSlots = new List<IconSlot>();

		private static readonly Comparison<IconEntry> iconSortKeyDescending = CompareIconSortKey;

		/// <summary>
		/// 逐帧缓存。健康面板每帧会走 input phase 和 draw pass 两遍，
		/// 而 LabelCap / GetTooltip 都是现拼字符串的重活（伤口还要走 StringBuilder + Translate），
		/// 同一帧里同一只 hediff 的值必然一致，缓存一份即可省掉一半。
		/// </summary>
		private static int frameCacheStamp = -1;
		private static readonly Dictionary<Hediff, string> labelCapCache = new Dictionary<Hediff, string>();
		private static readonly Dictionary<Hediff, string> hediffTooltipCache = new Dictionary<Hediff, string>();

		/// <summary>
		/// Text.CalcHeight 的结果缓存。一次测量是整串逐字符走一遍排版（含富文本解析、字形步进查表），
		/// 而健康面板每帧会把同一批标签测量好几遍（Layout 一遍、Repaint 一遍）。
		/// 键是 (文本, 宽度)：高度只由它俩 + 字体决定，字体变了（字号/语言）整体清空。
		/// </summary>
		private static readonly Dictionary<(string Text, int Width), float> calcHeightCache =
			new Dictionary<(string, int), float>();

		private const int CalcHeightCacheLimit = 2048;

		/// <summary>同一分组最多列出的 tooltip 条数，其余折成“……另有 N 条”。再多屏幕就放不下了。</summary>
		private const int MaxHediffTooltips = 6;

		private static bool calcHeightEpochValid;
		private static GameFont calcHeightFont;
		private static float calcHeightFontSize = -1f;
		private static LoadedLanguage? calcHeightLanguage;

		// 身体部位标签里的 \b 正则原来每次 new Regex，改为按标签缓存（模板就那几个）。
		private static readonly Dictionary<string, Regex> wordBoundaryRegexCache = new Dictionary<string, Regex>();

		// 滚动裁剪：由 DrawHediffListing 的补丁在前后包一层（只在它内部绘制行，所以不需要帧号标记）。
		private static bool viewportActive;
		private static float viewportHeight;
		private const float ViewportCullMargin = 128f;

		/// <summary>进入 DrawHediffListing 时记录可视区高度（列表高度减去底部流血提示行）。</summary>
		public static void BeginListingViewport(float listingHeight)
		{
			viewportActive = true;
			viewportHeight = listingHeight - Text.LineHeight;
		}

		public static void EndListingViewport()
		{
			viewportActive = false;
		}

		/// <summary>整行（含身体部位标签）完全滚出可视区时返回 true，此时只需要累加高度、不需要绘制。</summary>
		private static bool IsRowOutsideViewport(int rowY, int rowHeight)
		{
			if (!viewportActive || viewportHeight <= 0f || scrollPositionField == null)
				return false;

			float scrollY = scrollPositionField.Invoke().y;
			if (float.IsNaN(scrollY))
				return false;

			return rowY + rowHeight < scrollY - ViewportCullMargin
				|| rowY > scrollY + viewportHeight + ViewportCullMargin;
		}

		/// <summary>带缓存的 Text.CalcHeight。</summary>
		private static float CalcHeightCached(string text, int width)
		{
			GameFont font = Text.Font;
			float fontSize = Text.CurFontStyle.fontSize;
			LoadedLanguage? language = LanguageDatabase.activeLanguage;

			if (!calcHeightEpochValid || font != calcHeightFont || fontSize != calcHeightFontSize || !ReferenceEquals(language, calcHeightLanguage))
			{
				calcHeightCache.Clear();
				calcHeightEpochValid = true;
				calcHeightFont = font;
				calcHeightFontSize = fontSize;
				calcHeightLanguage = language;
			}

			var key = (text, width);
			if (calcHeightCache.TryGetValue(key, out float height))
				return height;

			height = Text.CalcHeight(text, width);
			// 满了就不再写入：继续用旧条目、宁可多算几次，也不要无限增长
			if (calcHeightCache.Count < CalcHeightCacheLimit)
				calcHeightCache[key] = height;
			return height;
		}

		private static void TickFrameCaches()
		{
			int frame = Time.frameCount;
			if (frame == frameCacheStamp)
				return;

			frameCacheStamp = frame;
			labelCapCache.Clear();
			hediffTooltipCache.Clear();
		}

		/// <summary>LabelCap 对伤口来说是 StringBuilder + 拼接 + Translate，同一帧里问一次就够。</summary>
		private static string GetLabelCapCached(Hediff hediff)
		{
			TickFrameCaches();

			if (labelCapCache.TryGetValue(hediff, out string? cached))
				return cached;

			string labelCap = hediff.LabelCap;
			labelCapCache[hediff] = labelCap;
			return labelCap;
		}

		private static string GetHediffTooltipCached(Hediff hediff, Pawn pawn)
		{
			TickFrameCaches();

			if (hediffTooltipCache.TryGetValue(hediff, out string? cached))
				return cached;

			string tooltip = hediff.GetTooltip(pawn, ShowHediffsDebugInfo);
			hediffTooltipCache[hediff] = tooltip;
			return tooltip;
		}

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

			var scrollPosition = AccessTools.Field(healthCardType, "scrollPosition");
			if (scrollPosition != null)
				scrollPositionField = AccessTools.StaticFieldRefAccess<Vector2>(scrollPosition);
			else
				Log.Warning("[CompactHediffs] 未找到 HealthCardUtility.scrollPosition，已关闭滚动裁剪（只影响性能，不影响显示）。");

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

			// 部位健康色只取一次：原来 MakeBodyPartText 和部位血条各调一次 GetPartConditionLabel（内含字符串拼接）
			Color healthColor = part != null ? GetHealthColorForBodypart(pawn, part) : Color.white;

			string bodyPartText = MakeBodyPartText(part, replacingPart, healthColor);

			int bodypartLabelWidth = (int)Math.Max(bodyPartColumnWidth - IconHeight / 2f - rowLeftPad, 1f);
			int bodyPartLabelHeight = (int)CalcHeightCached(bodyPartText, bodypartLabelWidth);
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

			// 测量阶段顺手把每组的排版结果存下来，绘制阶段直接复用，
			// 不再把标签字符串、图标宽度、文本高度算第二遍。
			float hediffColumnWidthF = rowRect.width - column_bodypartWidth;
			int halfHediffColumnWidth = (int)(hediffColumnWidthF / 2f);
			bool barsEnabled = settings.severityBarMode != CompactHediffs_Settings.SeverityBarMode.Off;

			groupLayouts.Clear();
			for (int i = 0; i < groupings.Count; i++)
			{
				IGrouping<HediffDef, Hediff> grouping = groupings[i];
				string hediffLabel = GenLabelForHediffGroup(grouping);
				Hediff representativeHediff = grouping.First();
				int groupCount = grouping.Count();
				if (groupCount > 1)
				{
					hediffLabel = hediffLabel + " x" + groupCount.ToString();
				}
				if (settings.italicizeMissing && representativeHediff.def == HediffDefOf.MissingBodyPart)
				{
					hediffLabel = hediffLabel.ApplyTag("i");
				}

				int iconsWidth = CalcIconsWidthForGrouping(grouping, showDebugOptions);
				float iconOverlap = 1f;
				if (iconsWidth > halfHediffColumnWidth)
				{
					// 图标总宽超过半个 hediff 列时按比例压缩间距；测量和绘制必须用同一套取值
					iconOverlap = halfHediffColumnWidth / (float)iconsWidth;
					iconsWidth = halfHediffColumnWidth;
				}
				int hediffLabelWidth = (int)(hediffColumnWidthF - iconsWidth);

				int textHeight = (int)CalcHeightCached(hediffLabel, hediffLabelWidth);

				hediffTotalHeight += textHeight;

				if (settings.internalSeparator && i < groupings.Count - 1)
					hediffTotalHeight += settings.internalSeparatorHeight;

				if (barsEnabled && GroupMayDrawBars(representativeHediff))
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

				groupLayouts.Add(new GroupLayout
				{
					Label = hediffLabel,
					IconsWidth = iconsWidth,
					IconOverlap = iconOverlap,
					TextHeight = textHeight,
					Count = groupCount,
				});
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

			// 滚动裁剪：整行已经滚出可视区时，除了上面那几条几乎零成本的纯色矩形，
			// 剩下的文本排版、进度条分块、图标按钮全都不用做（行高已经算完，curY 照常推进）。
			// 这是“hediff 一多就卡”最主要的一块：一屏只看得到十几行，其余每行每帧都在白算。
			if (IsRowOutsideViewport(currentY, totalHeight))
			{
				GUI.color = Color.white;
				currentY += Math.Max(bodyPartLabelHeight, hediffTotalHeight);
				curY = currentY;
				return;
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

			int hediffColumnWidth = (int)hediffColumnWidthF;

			for (int i = 0; i < groupings.Count; i++)
			{
				IGrouping<HediffDef, Hediff> grouping = groupings[i];
				// 标签、图标宽度、文本高度都来自测量阶段，不再重算
				GroupLayout layout = groupLayouts[i];
				string hediffLabel = layout.Label;
				int iconsWidth = layout.IconsWidth;
				float iconOverlap = layout.IconOverlap;
				int hediffLabelWidth = (int)(hediffColumnWidthF - iconsWidth);
				int hediffTextHeight = layout.TextHeight;

				Hediff representativeHediff = grouping.First();

				// SmartMedicine 只在它激活时才需要完整排序表，否则不为它付排序代价
				List<Hediff>? hediffsByPriority = null;
				if (SmartMedicine.Active)
				{
					hediffsByPriority = grouping
						.OrderByDescending(x => x.TendableNow(true) ? x.TendPriority : -1)
						.ToList();
				}

				if (barsEnabled && GroupMayDrawBars(representativeHediff) && settings.severityBarsPosition == CompactHediffs_Settings.BarPosition.Above)
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
					// 必须惰性：GetTooltip 要现拼一整段文本（伤口还要走 comps + Translate），
					// 而原版 TipSignal 只在“真正要显示时”才调 textGetter（还要等悬停延迟）。
					// 原来在这里直接 GetTooltip 全部 hediff，鼠标扫过一排 40 条伤口的行时，
					// 每帧白拼 40 段文本、而且 input phase 拼的那份还会被 TipRegion 直接丢掉。
					int tipIndex = 0;
					int shownHediffs = 0;
					int omittedHediffs = 0;
					// 同一行里多个分组共用一个 id 空间，必须带上 tooltipIDOffset，否则第二组会覆盖第一组的提示
					int tipIdBase = (int)currentY + 117857 + tooltipIDOffset;

					foreach (Hediff individualHediff in grouping)
					{
						// 堆叠几十条时全列出来既卡又没人看，只给前 N 条，剩下的折成一句提示
						if (shownHediffs >= MaxHediffTooltips)
						{
							omittedHediffs++;
							continue;
						}
						shownHediffs++;

						Hediff localHediff = individualHediff;
						TooltipHandler.TipRegion(fullHediffRect, new TipSignal(
							() => GetHediffTooltipCached(localHediff, pawn), tipIdBase + tipIndex++));

						if (Pawnmorpher.Active && Pawnmorpher.GetTooltip != null)
						{
							//copied from Pawnmorph.PatchHealthCardUtilityDrawHediffRow
							string tooltip = Pawnmorpher.GetTooltip(new List<Hediff>() { individualHediff });
							if (!string.IsNullOrWhiteSpace(tooltip))
							{
								TooltipHandler.TipRegion(fullHediffRect, new TipSignal(() => tooltip, tipIdBase + tipIndex++));
							}
						}
					}

					if (omittedHediffs > 0)
					{
						string moreText = "CompactHediffs_OmittedHediffTooltips".Translate(omittedHediffs);
						TooltipHandler.TipRegion(fullHediffRect, new TipSignal(moreText, tipIdBase + tipIndex++));
					}

					tooltipIDOffset += tipIndex;
				}

				GUI.color = GetHediffColor(settings, representativeHediff);
				//this is where smartMedicine transpiles its float menu into, so lets follow suit
				Widgets.Label(hediffLabelrect, hediffLabel);
				GUI.color = Color.white;
				if (SmartMedicine.Active)
				{
					UI_SmartMedicine.AddSmartMedicineFloatMenuButton(fullHediffRect, hediffsByPriority!);
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
					if (layout.Count == 1)
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

				// 图标一次收集好：贴图、颜色、该画几个都在收集阶段定完
				CollectIconHediffs(grouping, settings);

				//draw non-injury icons first
				if (tmpStateIcons.Count > 0)
				{
					int defaultIconOffset = (int)Math.Max((fullHediffRect.height - DefaultIconHeight) / 2f, 0);
					foreach (IconEntry icon in tmpStateIcons)
					{
						GUI.color = icon.Color;
						for (int k = 0; k < icon.Count; k++)
						{
							Rect iconRect = new Rect(rowRect.width - (widthAccumulator + DefaultIconHeight), fullHediffRect.y + defaultIconOffset, DefaultIconHeight, DefaultIconHeight).Rounded();
							GUI.DrawTexture(iconRect, icon.Texture);
							widthAccumulator += iconRect.width * iconOverlap;
						}
					}
				}
				//draw tended injuries
				foreach (IconEntry icon in tmpTendedIcons)
				{
					GUI.color = icon.Color;
					for (int k = 0; k < icon.Count; k++)
					{
						Rect iconRect = settings.tendingIcons
							? new Rect(rowRect.width - (widthAccumulator + TendIconWidth), fullHediffRect.y + iconOffset, TendIconWidth, IconHeight).Rounded()
							: new Rect(rowRect.width - (widthAccumulator + IconHeight), fullHediffRect.y + iconOffset, IconHeight, IconHeight).Rounded();
						GUI.DrawTexture(iconRect, icon.Texture);
						widthAccumulator += iconRect.width * iconOverlap;
					}
				}
				//draw bleeding injuries
				GUI.color = Color.white;
				foreach (IconEntry icon in tmpBleedingIcons)
				{
					for (int k = 0; k < icon.Count; k++)
					{
						Rect iconRect;
						if (settings.bleedingIcons)
						{
							iconRect = new Rect(rowRect.width - (widthAccumulator + BleedIconWidth), fullHediffRect.y + iconOffset, BleedIconWidth, IconHeight).Rounded();
							GUI.color = icon.Color;
							GUI.DrawTexture(iconRect, icon.Texture);
						}
						else
						{
							iconRect = new Rect(rowRect.width - (widthAccumulator + IconHeight), fullHediffRect.y + iconOffset, IconHeight, IconHeight).Rounded();
							GUI.DrawTexture(iconRect.ContractedBy(GenMath.LerpDouble(0f, 0.6f, 5f, 0f, Mathf.Min(icon.BleedRate, 1f))), Textures.Vanilla_BleedingIcon);
						}
						widthAccumulator += iconRect.width * iconOverlap;
					}
				}

				//draw other mod icon
				GUI.color = Color.white;
				if (SmartMedicine.Active && SmartMedicine.GetElementsByList != null)
				{
					Rect iconRect = new Rect(rowRect.width - (widthAccumulator + SmartMedicineIconWidth), fullHediffRect.y + iconOffset, rowRect.width, SmartMedicineIconHeight).Rounded();
					UI_SmartMedicine.DrawSmartMedicineIcon(iconRect, hediffsByPriority!, ref widthAccumulator, SmartMedicineIconWidth);
				}
				if (ChooseYourMedicine.Active && ChooseYourMedicine.DrawButtonToAssignMedManually != null)
				{
					Rect iconRect = new Rect(rowRect.width - (widthAccumulator + ChooseYourMedicine.IconWidth), fullHediffRect.y + iconOffset, ChooseYourMedicine.IconWidth, ChooseYourMedicine.IconHeight).Rounded();
					ChooseYourMedicine.DrawButtonToAssignMedManually(iconRect, pawn, grouping);
				}

				innerY += hediffTextHeight;

				if (barsEnabled && GroupMayDrawBars(representativeHediff) && settings.severityBarsPosition == CompactHediffs_Settings.BarPosition.Below)
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

			if (canEntryBeClicked != null && entryClicked != null && Mouse.IsOver(wholeEntryRect))
			{
				// 原版无条件调用 CanEntryBeClicked：它会遍历 Find.BattleLog 里所有战斗的所有条目，
				// 而 ButtonInvisible 在鼠标不在行内时本来就是空操作，所以先判 hover 再问，结果一致但便宜得多。
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

		/// <summary>bodyPart 为 null 时表示全身。healthColor 由调用方取一次传进来，避免重复调用 GetPartConditionLabel。</summary>
		public static string MakeBodyPartText(BodyPartRecord? part, Hediff? replacingPart, Color healthColor)
		{
			string bodyPartText;
			if (part == null)
			{
				bodyPartText = ColoredText.Colorize("WholeBody".Translate(), Color.grey);
			}
			else
			{
				if (replacingPart != null)
				{
					var replacingPartColorLabel = ColoredText.Colorize(replacingPart.Label, replacingPart.def.defaultLabelColor);
					// 原来每次调用都 new Regex（要重新解析模式），按标签缓存一份就够了
					var regex = GetWordBoundaryRegex(part.def.label);
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

		private static Regex GetWordBoundaryRegex(string label)
		{
			if (wordBoundaryRegexCache.TryGetValue(label, out Regex? cached))
				return cached;

			if (wordBoundaryRegexCache.Count >= 64)
				wordBoundaryRegexCache.Clear();

			var regex = new Regex(@"\b" + label + @"\b");
			wordBoundaryRegexCache[label] = regex;
			return regex;
		}

		private static float GetPartMaxHealth(Pawn pawn, BodyPartRecord part)
		{
			if (!EBF.Active || EBF.GetMaxHealth_Cached == null)
				return part.def.GetMaxHealth(pawn);

			return EBF.GetMaxHealth_Cached(part.def, pawn, part);
		}

		/// <summary>
		/// 图标区总宽。直接由收集好的图标条目数算出（和绘制用的是同一批数据），
		/// 不再是“每条 hediff 加一次”。顺带修掉了原来三处宽度对不上的地方：
		/// 非伤口状态图标实际按 DefaultIconHeight 绘制、SmartMedicine / ChooseYourMedicine 每组只画一个，
		/// 但旧代码按 IconHeight 和“每条一次”计入，堆叠时会把标签挤到半个列宽。
		/// </summary>
		private static int CalcIconsWidthForGrouping(IEnumerable<Hediff> grouping, bool showDebugOptions)
		{
			CompactHediffs_Settings settings = CompactHediffsMod.Settings;

			CollectIconHediffs(grouping, settings);

			int iconsWidth = InfoIconWidth;
			if (showDebugOptions)
				iconsWidth += DevRemoveButtonWidth;

			iconsWidth += TotalIconCount(tmpStateIcons) * DefaultIconHeight;
			iconsWidth += TotalIconCount(tmpTendedIcons) * (settings.tendingIcons ? TendIconWidth : IconHeight);
			iconsWidth += TotalIconCount(tmpBleedingIcons) * (settings.bleedingIcons ? BleedIconWidth : IconHeight);

			if (SmartMedicine.Active)
				iconsWidth += SmartMedicineIconWidth;
			if (ChooseYourMedicine.Active)
				iconsWidth += ChooseYourMedicine.IconWidth;

			return iconsWidth;
		}

		/// <summary>
		/// 单次遍历把分组里的图标收集好：贴图、颜色、排序键都在这里定完，绘制阶段直接画。
		/// 关键点：<b>相同贴图只保留一个</b>。同一部位堆几十条同类伤口是常态，
		/// 原来每条伤口画一个图标（40 条就是 40 次绘制，还挤成一片糊），
		/// 现在按贴图去重 —— 治疗图标本身只有 6 档、流血图标 6 档，所以上限是 6 个而不是 N 个。
		/// </summary>
		private static void CollectIconHediffs(IEnumerable<Hediff> grouping, CompactHediffs_Settings settings)
		{
			tmpStateIcons.Clear();
			tmpTendedIcons.Clear();
			tmpBleedingIcons.Clear();

			bool customTendingIcons = settings.tendingIcons;
			bool customBleedingIcons = settings.bleedingIcons;

			foreach (Hediff hediff in grouping)
			{
				// StateIcon 每次读都要遍历 comps（TendDuration 的 CompStateIcon 还会算 IsTended/FullyImmune），
				// 原来一次循环里读了三次，这里只读一次。
				TextureAndColor stateIcon = hediff.StateIcon;
				if (stateIcon.HasValue)
				{
					if (stateIcon.Texture == Textures.Vanilla_TendedIcon_Well_Injury)
					{
						float quality = hediff.TryGetComp<HediffComp_TendDuration>()?.tendQuality ?? -1f;
						tmpTendedIcons.Add(new IconEntry
						{
							Texture = customTendingIcons ? TendingIconFor(quality) : stateIcon.Texture,
							Color = stateIcon.Color,
							SortKey = quality,
							Weight = 1,
							Count = 1,
						});
					}
					else
					{
						tmpStateIcons.Add(new IconEntry
						{
							Texture = stateIcon.Texture,
							Color = stateIcon.Color,
							Weight = 1,
							Count = 1,
						});
					}
				}

				if (hediff.Bleeding)
				{
					float bleedRate = hediff.BleedRate;
					tmpBleedingIcons.Add(new IconEntry
					{
						Texture = customBleedingIcons ? BleedingIconFor(bleedRate) : Textures.Vanilla_BleedingIcon,
						Color = customBleedingIcons
							? new Color(1f, 1f, 1f, Pulser.PulseBrightness(bleedRate, 0.25f))
							: Color.white,
						BleedRate = bleedRate,
						SortKey = bleedRate,
						Weight = 1,
						Count = 1,
					});
				}
			}

			// 按原逻辑排序：包扎图标按治疗质量降序、流血图标按流血速度降序
			if (tmpTendedIcons.Count > 1)
				tmpTendedIcons.Sort(iconSortKeyDescending);
			if (tmpBleedingIcons.Count > 1)
				tmpBleedingIcons.Sort(iconSortKeyDescending);

			// “每条状态各一个图标”是本模组的老行为，直接画；关掉后按比例压缩到上限
			if (!settings.perHediffIcons)
				CompressIcons();
		}

		/// <summary>
		/// 把“每条 hediff 一个图标”压缩成“最多 <see cref="MaxIconsPerGroup"/> 个”，
		/// 各图标按数量比例分配，且每种至少保留 1 个。
		/// 例：30 个流血_0 + 20 个流血_1 → 12 : 8，正好是 3 : 2。
		/// 分配用 D'Hondt 法：每轮把下一个名额给 “Weight / (已分配 + 1)” 最大的那种，
		/// 天然满足整数、总数恰好等于上限、且每种至少 1 个。
		/// </summary>
		private static void CompressIcons()
		{
			int total = tmpStateIcons.Count + tmpTendedIcons.Count + tmpBleedingIcons.Count;
			if (total <= MaxIconsPerGroup)
				return; // 本来就画得下，每条各画一个（这里不做任何合并）

			MergeIconsByTexture(tmpStateIcons);
			MergeIconsByTexture(tmpTendedIcons);
			MergeIconsByTexture(tmpBleedingIcons);

			tmpIconSlots.Clear();
			int weightSum = 0;
			AddIconSlots(tmpStateIcons, ref weightSum);
			AddIconSlots(tmpTendedIcons, ref weightSum);
			AddIconSlots(tmpBleedingIcons, ref weightSum);

			int distinct = tmpIconSlots.Count;
			if (distinct == 0)
				return;

			if (distinct >= MaxIconsPerGroup)
			{
				// 极端情况（不同图标就超过上限）：只留前 MaxIconsPerGroup 个，其余不画
				for (int i = MaxIconsPerGroup; i < distinct; i++)
					SetIconCount(tmpIconSlots[i], 0);
				return;
			}

			// 每种先给 1 个（保证“每种图标最少显示一个”）
			int assigned = distinct;
			while (assigned < MaxIconsPerGroup)
			{
				int bestIndex = 0;
				float bestRatio = -1f;
				for (int i = 0; i < distinct; i++)
				{
					IconSlot slot = tmpIconSlots[i];
					IconEntry entry = slot.List[slot.Index];
					float ratio = entry.Weight / (float)(entry.Count + 1);
					if (ratio > bestRatio)
					{
						bestRatio = ratio;
						bestIndex = i;
					}
				}

				if (bestRatio < 0f)
					break;

				IconSlot bestSlot = tmpIconSlots[bestIndex];
				IconEntry best = bestSlot.List[bestSlot.Index];
				best.Count++;
				bestSlot.List[bestSlot.Index] = best;
				assigned++;
			}
		}

		/// <summary>同贴图合并成一条：Weight 累加，Count 先置 1，SortKey 保留最严重的那个。</summary>
		private static void MergeIconsByTexture(List<IconEntry> list)
		{
			for (int i = 0; i < list.Count; i++)
			{
				IconEntry merged = list[i];
				for (int j = list.Count - 1; j > i; j--)
				{
					if (list[j].Texture != merged.Texture)
						continue;

					merged.Weight += list[j].Weight;
					if (list[j].SortKey > merged.SortKey)
						merged.SortKey = list[j].SortKey;
					list.RemoveAt(j);
				}
				merged.Count = 1;
				list[i] = merged;
			}
		}

		private static void AddIconSlots(List<IconEntry> list, ref int total)
		{
			for (int i = 0; i < list.Count; i++)
			{
				total += list[i].Weight;
				tmpIconSlots.Add(new IconSlot { List = list, Index = i });
			}
		}

		private static void SetIconCount(IconSlot slot, int count)
		{
			IconEntry entry = slot.List[slot.Index];
			entry.Count = count;
			slot.List[slot.Index] = entry;
		}

		/// <summary>图标总个数（压缩后一个条目可能代表多个图标）。</summary>
		private static int TotalIconCount(List<IconEntry> list)
		{
			int total = 0;
			for (int i = 0; i < list.Count; i++)
				total += list[i].Count;
			return total;
		}

		private static int CompareIconSortKey(IconEntry a, IconEntry b)
		{
			return b.SortKey.CompareTo(a.SortKey);
		}

		private static Texture2D TendingIconFor(float quality)
		{
			if (quality <= 0.01)
				return Textures.TendingIcon_0;
			if (quality <= 0.25)
				return Textures.TendingIcon_1;
			if (quality < 0.5)
				return Textures.TendingIcon_2;
			if (quality < 0.75)
				return Textures.TendingIcon_3;
			if (quality < 1.0)
				return Textures.TendingIcon_4;

			return Textures.TendingIcon_5;
		}

		private static Texture2D BleedingIconFor(float bleedRate)
		{
			if (bleedRate <= 0.1)
				return Textures.BleedingIcon_0;
			if (bleedRate < 0.25)
				return Textures.BleedingIcon_1;
			if (bleedRate < 0.5)
				return Textures.BleedingIcon_2;
			if (bleedRate < 1.25)
				return Textures.BleedingIcon_3;
			if (bleedRate < 2.25)
				return Textures.BleedingIcon_4;

			return Textures.BleedingIcon_5;
		}

		private static string GenLabelForHediffGroup(IGrouping<HediffDef, Hediff> grouping)
		{
			// 绝大多数分组只有一条 hediff，直接取标签即可（LabelCap 内部要拼 2~3 个字符串）
			if (grouping.Count() == 1)
				return GetLabelCapCached(grouping.First());

			string fullLabel = GetLabelCapCached(grouping.First());
			bool mismatch = false;
			foreach (Hediff hediff in grouping)
			{
				if (GetLabelCapCached(hediff) != fullLabel)
				{
					mismatch = true;
					break;
				}
			}
			if (!mismatch)
				return fullLabel;

			return grouping.First().LabelBaseCap;
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

			if (!settings.showHiddenProgressConditions && !HasSeverityLabel(hediff))
				return false;

			severityFraction = GetSeverityFraction(hediff);

			var immunizableComp = hediff.TryGetComp<HediffComp_Immunizable>();
			if (immunizableComp != null && immunizableComp.Immunity > 0.001f)
				immunityFraction = immunizableComp.Immunity;

			return severityFraction > 0.001f || immunityFraction > 0.001f;
		}

		/// <summary>
		/// 该 hediff 是否有 SeverityLabel（原版用它决定“这条状态要不要显示进度”）。
		/// 这里只关心“有没有”，但 SeverityLabel 是现拼的字符串（Hediff_Injury 是 Severity.ToString("F1")），
		/// 每帧每条要问好几遍，条数一多就是纯浪费；伤口占绝大多数，直接按 Severity 判空。
		/// </summary>
		private static bool HasSeverityLabel(Hediff hediff)
		{
			if (hediff.GetType() == typeof(Hediff_Injury))
				return hediff.Severity != 0f;

			return hediff.SeverityLabel != null;
		}

		/// <summary>
		/// 这一组的 def 有没有可能画出任何一种进度条。
		/// 同一个分组里 def 完全相同（连 comps 都一样），所以这个判断对整组一致 ——
		/// 为 false 时（普通伤口就是这样：既没有 maxSeverity，TendDuration 也没配累计治疗上限），
		/// 逐条遍历几十次全是空转，直接整组跳过。
		/// </summary>
		private static bool GroupMayDrawBars(Hediff representative)
		{
			if (GetMaxSeverityForHediff(representative) > 0f)
				return true;

			if (representative.TryGetComp<HediffComp_Immunizable>() != null)
				return true;

			var tendDurationComp = representative.TryGetComp<HediffComp_TendDuration>();
			return tendDurationComp != null && tendDurationComp.TProps.disappearsAtTotalTendQuality > 0;
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
				// 先定色再乘脉冲：hediffColor 的 alpha 恒为 1，先乘后覆盖会把脉冲整段丢掉（条就不会闪）
				commonColor = hediffColor;
				if (bloodlossSpeed >= 0.1f)
				{
					var alphaMult = Pulser.PulseBrightness(0.5f + bloodlossSpeed * 0.75f, 0.5f);
					commonColor.a *= alphaMult;
				}
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
			else if (hediff.def == HediffDefOf.FoodPoisoning || hediff.def == HediffDefOf.ToxicBuildup)
			{
				commonTexture = Textures.Bar_DNA;
				severityTexture = Textures.Bar_DNA;
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

			// 每种贴图只解析一次，别每段都重新比对（一段最多两次）
			Texture2D commonResolved = ResolveBarTexture(commonTexture, settings.internalBarHeight);
			Texture2D severityResolved = ResolveBarTexture(severityTexture, settings.internalBarHeight);
			Texture2D immunityResolved = ResolveBarTexture(immunityTexture, settings.internalBarHeight);

			DrawBarSegment(commonRect, commonResolved, commonColor, settings.internalBarHeight);
			DrawBarSegment(commonRectMirror, commonResolved, commonColor, settings.internalBarHeight);
			DrawBarSegment(severityRect, severityResolved, severityColor, settings.internalBarHeight);
			DrawBarSegment(severityRectMirror, severityResolved, severityColor, settings.internalBarHeight);
			DrawBarSegment(immunityRect, immunityResolved, immunityColor, settings.internalBarHeight);
			DrawBarSegment(immunityRectMirror, immunityResolved, immunityColor, settings.internalBarHeight);

			GUI.color = Color.white;
			return settings.internalBarHeight;
		}

		/// <summary>预烘焙 1:1 条纹理对应的条高（px）。条高不等于它时退回原始的 16px 贴图。</summary>
		private const int BakedBarTextureHeight = 4;

		/// <summary>
		/// 条高恰好等于烘焙高度、且“锐化进度条纹理”打开时，换成 1:1 的贴图。
		/// 此时每块绘制尺寸≈贴图像素尺寸，采样停在 mip 0，不会被 1.7 的 Linear+mip 采样器糊掉。
		/// 非条纹理（<see cref="TexUI.FastFillTex"/> 等纯色）原样返回。
		/// </summary>
		private static Texture2D ResolveBarTexture(Texture2D texture, int barHeight)
		{
			if (!CompactHediffsMod.Settings.sharpBarTextures || barHeight != BakedBarTextureHeight)
				return texture;

			if (texture == Textures.Bar_Ragged)
				return Textures.Bar_Ragged_1to1;
			if (texture == Textures.Bar_Infected)
				return Textures.Bar_Infected_1to1;
			if (texture == Textures.Bar_Pill)
				return Textures.Bar_Pill_1to1;
			if (texture == Textures.Bar_PillBig)
				return Textures.Bar_PillBig_1to1;
			if (texture == Textures.Bar_Malnutrition)
				return Textures.Bar_Malnutrition_1to1;
			if (texture == Textures.Bar_Cubes)
				return Textures.Bar_Cubes_1to1;
			if (texture == Textures.Bar_DNA)
				return Textures.Bar_DNA_1to1;

			return texture;
		}

		/// <summary>
		/// 画一段进度条。贴图按“高度铺满条高、宽度等比缩放”的尺寸横向平铺。
		/// 注意：1.7 的 IMGUI 通道全局只有两个 ClampToEdge 采样器（<c>ImGuiVulkanRenderer.CreateSampler</c>），
		/// texCoords 一旦超出 [0,1] 就会被钳到边缘像素，<c>Texture.wrapMode = Repeat</c> 在这条路径上完全不起作用
		/// —— 那正是“左边一小块纹理、右边一片纯色”的成因（旧版靠 Unity 的 Repeat 采样器平铺）。
		/// 所以这里改成手工分块，每块 UV 都保持在 [0,1] 内。
		/// </summary>
		private static void DrawBarSegment(Rect? rect, Texture2D texture, Color color, int barHeight)
		{
			if (!rect.HasValue || barHeight <= 0 || texture == null)
				return;

			Rect drawnRect = rect.Value;
			if (drawnRect.width <= 0f || drawnRect.height <= 0f || texture.width <= 0 || texture.height <= 0)
				return;

			GUI.color = color;

			// 等价于旧写法里的 texScale = 贴图高 / 条高，再换算成屏幕上的单块宽度
			float tileWidth = texture.width * (barHeight / (float)texture.height);
			if (texture.width <= 1 || tileWidth < 1f)
			{
				// 1x1 纯色贴图（TexUI.FastFillTex 等）平铺没有意义，拉伸一次就够。
				// 注意这里必须按贴图宽度判断：1x1 在 4px 条高下算出来的 tileWidth 是 4，
				// 只看 tileWidth 会把纯色条切成几十个 quad（纯色的纹理占多数，代价很实在）。
				GUI.DrawTexture(drawnRect, texture);
				return;
			}

			float right = drawnRect.xMax;
			int tiles = Mathf.CeilToInt(drawnRect.width / tileWidth);
			for (int i = 0; i < tiles; i++)
			{
				float start = drawnRect.x + i * tileWidth;
				float end = Math.Min(start + tileWidth, right);
				if (end - start <= 0.01f)
					break;

				// 末块不足一整块时按比例裁切 UV，视觉上等同于被条边缘截断
				Rect uv = new Rect(0f, 0f, Math.Min((end - start) / tileWidth, 1f), 1f);
				GUI.DrawTextureWithTexCoords(new Rect(start, drawnRect.y, end - start, drawnRect.height), texture, uv);
			}
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

			DrawBarSegment(immunityRect, ResolveBarTexture(immunityTexture, settings.internalBarHeight), immunityColor, settings.internalBarHeight);
			DrawBarSegment(immunityRectMirror, ResolveBarTexture(immunityTexture, settings.internalBarHeight), immunityColor, settings.internalBarHeight);

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

			// 食物中毒（FoodPoisoning）没有 maxSeverity，严重度从 initialSeverity=1 单调衰减到 0，
			// 原版因此不画进度条。它的上限是确定的 1，这里特判一下，好让 Bar_DNA 的“恢复进度”能显示出来。
			if (hediff.def == HediffDefOf.FoodPoisoning)
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

		/// <summary>
		/// 按治疗优先级重排身体部位分组。
		/// 原来是 OrderByDescending(...).ThenByDescending(...) 的惰性链，每帧要枚举两次（DrawHediffListing 与
		/// AnyHediffsDisplayed 各一次），每次都要给每个分组用 Max(lambda) 算键。这里改为显式循环 + 稳定排序，
		/// 少掉每组的闭包与枚举器分配，排序结果与 LINQ 完全一致（末位用原始下标做稳定化）。
		/// </summary>
		public static IEnumerable<IGrouping<BodyPartRecord, Hediff>> ReorderHediffGroups(IEnumerable<IGrouping<BodyPartRecord, Hediff>> returned, Pawn pawn, bool showBloodLoss)
		{
			var entries = new List<(IGrouping<BodyPartRecord, Hediff> Group, float Priority, float ListPriority, int Index)>();

			int index = 0;
			foreach (IGrouping<BodyPartRecord, Hediff> group in returned)
			{
				float priority = -1f;
				foreach (Hediff hediff in group)
				{
					if (!hediff.TendableNow(true))
						continue;

					float tendPriority = hediff.TendPriority;
					if (tendPriority > priority)
						priority = tendPriority;
				}

				float listPriority = getListPriority != null ? getListPriority(group.First().Part) : 0f;
				entries.Add((group, priority, listPriority, index++));
			}

			entries.Sort((a, b) =>
			{
				int compare = b.Priority.CompareTo(a.Priority);
				if (compare != 0)
					return compare;

				compare = b.ListPriority.CompareTo(a.ListPriority);
				if (compare != 0)
					return compare;

				return a.Index.CompareTo(b.Index);
			});

			var result = new List<IGrouping<BodyPartRecord, Hediff>>(entries.Count);
			foreach (var entry in entries)
				result.Add(entry.Group);

			return result;
		}
	}
}
