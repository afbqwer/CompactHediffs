using PeteTimesSix.CompactHediffs.ModCompat;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace PeteTimesSix.CompactHediffs.Rimworld.UI_compat
{
    public static class UI_SmartMedicine
	{

		public static void DrawSmartMedicineIcon(Rect rowRect, List<Hediff> hediffs, ref float widthAccumulator, int iconWidth)
		{
            var icons = SmartMedicine.GetElementsByList(hediffs);

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

            var list = SmartMedicine.CreateCareMenuOptionsWithList(hediffs.ToList());
            Find.WindowStack.Add(new FloatMenu(list));
        }
	}
}

