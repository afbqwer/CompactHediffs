using PeteTimesSix.CompactHediffs.ModCompat;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.Rimworld.UI_compat
{
    /// <summary>SmartMedicine 存在时的行内图标与右键菜单桥接。</summary>
    public static class UI_SmartMedicine
    {
        public static void DrawSmartMedicineIcon(Rect rowRect, List<Hediff> hediffs, ref float widthAccumulator, int iconWidth)
        {
            if (SmartMedicine.GetElementsByList is not { } getElements)
                return;

            var icons = getElements(hediffs);

            foreach (var icon in icons)
            {
                var iconRect = new Rect(rowRect.width - widthAccumulator - iconWidth, rowRect.y, iconWidth, rowRect.height).Rounded();
                icon.Invoke(iconRect);
                widthAccumulator += iconWidth;
            }
        }

        public static void AddSmartMedicineFloatMenuButton(Rect buttonRect, List<Hediff> hediffs)
        {
            if (Event.current.button != 1 || !Widgets.ButtonInvisible(buttonRect) || !hediffs.Any(h => h.TendableNow(true)))
                return;

            if (SmartMedicine.CreateCareMenuOptionsWithList is not { } createOptions)
                return;

            var list = createOptions(hediffs.ToList());
            Find.WindowStack.Add(new FloatMenu(list));
        }
    }
}
