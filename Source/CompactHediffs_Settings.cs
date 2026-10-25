using PeteTimesSix.CompactHediffs.HarmonyPatches;
using PeteTimesSix.CompactHediffs.Rimworld.UI;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs
{
    public class CompactHediffs_Settings : ModSettings
    {
        public enum SeverityBarMode
        {
            Off,
            LeftToRight,
            EdgeToMiddle,
            MiddleToEdge,
        }

        public enum BarPosition
        {
            Above,
            Below
        }

        public bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (value)
                    ITab_Pawn_Health_Patches.SetExtraSize(extraTabWidth, extraTabHeight);
                else
                    ITab_Pawn_Health_Patches.SetExtraSize(0f, 0f);
            }
        }

        public bool replacingPartInBodypart = true;
        public bool italicizeMissing = true;
        public bool verticalSeparator = true;
        public bool horizontalSeparator = true;
        public bool internalSeparator = true;
        public bool separatorNightMode = true;
        public bool tendPrioritySort = true;
        public bool evenOddHighlights = false;

        public bool bodypartHealthbars = true;
        public BarPosition healtbarsBarsPosition = BarPosition.Above;
        public bool bleedingIcons = true;
        public bool tendingIcons = true;
        public bool bloodlossSpecialHandling = true;

        public BarPosition severityBarsPosition = BarPosition.Above;
        public SeverityBarMode severityBarMode = SeverityBarMode.LeftToRight;
        public bool severityBarHighContrast = true;
        public bool severityBarTextured = true;
        public bool showCumulativeThreatment = true;
        public bool showHiddenProgressConditions = false;

        public int extraTabWidth = 100;
        public int extraTabHeight = 0;

        public int verticalSeparatorWidth = 2;
        public int horizontalSeparatorHeight = 2;
        public int internalSeparatorHeight = 2;
        public int healthBarHeight = 4;
        public int internalBarHeight = 4;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _enabled, "isEnabled", true);

            Scribe_Values.Look(ref replacingPartInBodypart, "replacingPartInBodypart", true);
            Scribe_Values.Look(ref italicizeMissing, "italicizeMissing", true);
            Scribe_Values.Look(ref evenOddHighlights, "evenOddHighlights", false);
            Scribe_Values.Look(ref verticalSeparator, "verticalSeparator", true);
            Scribe_Values.Look(ref horizontalSeparator, "horizontalSeparator", true);
            Scribe_Values.Look(ref internalSeparator, "internalSeparator", true);
            Scribe_Values.Look(ref separatorNightMode, "separatorNightMode", true);
            Scribe_Values.Look(ref tendPrioritySort, "tendPrioritySort", true);
            Scribe_Values.Look(ref bodypartHealthbars, "bodypartHealthbars", true);
            Scribe_Values.Look(ref healtbarsBarsPosition, "healtbarsBarsPosition", BarPosition.Above);
            Scribe_Values.Look(ref bleedingIcons, "bleedingIcons", true);
            Scribe_Values.Look(ref tendingIcons, "tendingIcons", true);
            Scribe_Values.Look(ref bloodlossSpecialHandling, "bloodlossSpecialHandling", true);

            Scribe_Values.Look(ref extraTabWidth, "extraTabWidth", 100);
            Scribe_Values.Look(ref extraTabHeight, "extraTabHeight", 0);

            Scribe_Values.Look(ref severityBarsPosition, "severityBarsPosition", BarPosition.Above);
            Scribe_Values.Look(ref severityBarMode, "severityBarMode", SeverityBarMode.LeftToRight);
            Scribe_Values.Look(ref severityBarHighContrast, "severityBarHighContrast", true);
            Scribe_Values.Look(ref severityBarTextured, "severityBarTextured", true);
            Scribe_Values.Look(ref showCumulativeThreatment, "showCumulativeThreatment", true);
            Scribe_Values.Look(ref showHiddenProgressConditions, "showHiddenProgressConditions", false);
        }

        internal void DoSettingsWindowContents(Rect inRect)
        {
            bool didDraw = false;

            Rect previewRect = inRect.RightPartPixels(inRect.width - 250f).TopPart(0.6f).ContractedBy(5f);

            if (Current.ProgramState == ProgramState.Playing)
            {
                var selector = Find.Selector;
                if (selector != null && selector.NumSelected > 0 && selector.SelectedObjects[0] is Pawn pawn)
                {
                    float width = (1 - 0.375f) * (630f + extraTabWidth);
                    width += 10f;
                    Rect rect = previewRect;

                    if (rect.width > width)
                    {
                        float diff = rect.width - width;
                        rect.x += diff / 2f;
                        rect.width = width;
                    }

                    Widgets.DrawWindowBackground(rect);
                    rect = rect.ContractedBy(5f);

                    HealthCardUtility.DrawHediffListing(rect, pawn, true);
                    didDraw = true;
                }
            }

            if (!didDraw)
            {
                Widgets.NoneLabelCenteredVertically(previewRect.ContractedBy(10f), "settings_selectExamplePawnIngame".Translate());
            }

            Listing_Standard listingStandard = new Listing_Standard();
            listingStandard.Begin(inRect.LeftPartPixels(250f).TopPart(0.6f));

            listingStandard.ColumnWidth = 250f;

            bool localEnabled = Enabled;
            listingStandard.CheckboxLabeled("settings_IsEnabled".Translate(), ref localEnabled, "settings_IsEnabled_tooltip".Translate());
            Enabled = localEnabled;
            listingStandard.GapLine();

            listingStandard.CheckboxLabeled("settings_replacingPartInBodypart".Translate(), ref replacingPartInBodypart, "settings_replacingPartInBodypart_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_italicizeMissing".Translate(), ref italicizeMissing, "settings_italicizeMissing_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_evenOddHighlights".Translate(), ref evenOddHighlights, "settings_evenOddHighlights_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_verticalSeparator".Translate(), ref verticalSeparator, "settings_verticalSeparator_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_horizontalSeparator".Translate(), ref horizontalSeparator, "settings_horizontalSeparator_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_internalSeparator".Translate(), ref internalSeparator, "settings_internalSeparator_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_separatorNightMode".Translate(), ref separatorNightMode, "settings_separatorNightMode_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_tendPrioritySort".Translate(), ref tendPrioritySort, "settings_tendPrioritySort_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_bodypartHealthbars".Translate(), ref bodypartHealthbars, "settings_bodypartHealthbars_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_bleedingIcons".Translate(), ref bleedingIcons, "settings_bleedingIcons_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_tendingIcons".Translate(), ref tendingIcons, "settings_tendingIcons_tooltip".Translate());
            listingStandard.CheckboxLabeled("settings_bloodlossSpecialHandling".Translate(), ref bloodlossSpecialHandling, "settings_bloodlossSpecialHandling_tooltip".Translate());

            listingStandard.End();

            Listing_Standard listingStandardWide = new Listing_Standard();
            listingStandardWide.Begin(inRect.BottomPart(0.4f));

            listingStandardWide.ColumnWidth = (inRect.width - 17f) / 2f;

            listingStandardWide.EnumSelector(ref severityBarMode, "settings_severityBars".Translate(), valueLabelPrefix: "SeverityBarMode_", tooltip: "settings_severityBars_tooltip".Translate());
            listingStandardWide.EnumSelector(ref severityBarsPosition, "settings_severityBarPosition".Translate(), valueLabelPrefix: "BarPosition_", tooltip: "settings_severityBarPosition_tooltip".Translate());

            listingStandardWide.CheckboxLabeled("settings_severityBarTextured".Translate(), ref severityBarTextured, "settings_severityBarTextured_tooltip".Translate());
            listingStandardWide.CheckboxLabeled("settings_severityBarHighContrast".Translate(), ref severityBarHighContrast, "settings_severityBarHighContrast_tooltip".Translate());
            listingStandardWide.CheckboxLabeled("settings_showCumulativeThreatment".Translate(), ref showCumulativeThreatment, "settings_showCumulativeThreatment_tooltip".Translate());
            listingStandardWide.CheckboxLabeled("settings_showHiddenProgressConditions".Translate(), ref showHiddenProgressConditions, "settings_showHiddenProgressConditions_tooltip".Translate());

            listingStandardWide.NewColumn();

            listingStandardWide.Label($"{"settings_extraTabWidth".Translate()}: {extraTabWidth.ToString("F0")}px");
            int newWidth = (int)listingStandardWide.Slider(extraTabWidth, 0, 300);
            listingStandardWide.Label($"{"settings_extraTabHeight".Translate()}: {extraTabHeight.ToString("F0")}px");
            int newHeight = (int)listingStandardWide.Slider(extraTabHeight, 0, 200);

            if (newWidth != extraTabWidth || newHeight != extraTabHeight)
            {
                extraTabWidth = newWidth;
                extraTabHeight = newHeight;
                ITab_Pawn_Health_Patches.SetExtraSize(extraTabWidth, extraTabHeight);
            }

            listingStandardWide.End();
        }
    }
}
