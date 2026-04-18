using System;
using System.Reflection;
using HarmonyLib;
using Reportables;
using UI;
using UnityEngine;
using UnityEngine.UI;

namespace NewsTowerAutoAssign
{
    [HarmonyPatch(typeof(LocationDisplay), "Refresh")]
    internal static class Patch_LocationDisplayRefreshOwnership
    {
        private const string LegacyLabelChildName = "NewsTowerAutoAssign_OwnershipBadge";

        private static readonly FieldInfo StatusInnerImagesField =
            typeof(LocationStatusLabel).GetField(
                "innerImage",
                BindingFlags.Instance | BindingFlags.NonPublic
            );

        static void Postfix(LocationDisplay __instance)
        {
            try
            {
                DestroyLegacyTextBadgeIfPresent(__instance);
                ApplyOwnershipVisuals(__instance);
            }
            catch (Exception ex)
            {
                AssignmentLog.Error("Patch_LocationDisplayRefreshOwnership.Postfix: " + ex);
            }
        }

        private static void DestroyLegacyTextBadgeIfPresent(LocationDisplay display)
        {
            if (display?.Canvas == null)
                return;
            var legacy = display.Canvas.transform.Find(LegacyLabelChildName);
            if (legacy != null)
                UnityEngine.Object.Destroy(legacy.gameObject);
        }

        private static void ApplyOwnershipVisuals(LocationDisplay display)
        {
            if (display == null || !AutoAssignPlugin.GlobePinOwnershipEnabled.Value)
            {
                ClearOwnershipVisuals(display, restorePinColors: true);
                return;
            }

            var items = display.CurrentItems;
            if (items == null || items.Count == 0)
            {
                ClearOwnershipVisuals(display, restorePinColors: true);
                return;
            }

            int auto = 0;
            foreach (var item in items)
                if (AutoAssignOwnershipRegistry.IsModAutoAssigned(item))
                    auto++;
            int manual = items.Count - auto;

            OwnershipMode mode;
            if (auto == 0)
                mode = OwnershipMode.Manual;
            else if (manual == 0)
                mode = OwnershipMode.Auto;
            else
                mode = OwnershipMode.Mixed;

            var statusLabel = display.GetComponentInChildren<LocationStatusLabel>(true);
            TintStatusImages(statusLabel, mode);
        }

        private enum OwnershipMode
        {
            Manual,
            Auto,
            Mixed,
        }

        private static Color TintForMode(OwnershipMode mode)
        {
            switch (mode)
            {
                case OwnershipMode.Auto:
                    return new Color(0.72f, 1f, 0.78f, 1f);
                case OwnershipMode.Mixed:
                    return new Color(1f, 0.88f, 0.55f, 1f);
                default:
                    return Color.white;
            }
        }

        private static void TintStatusImages(LocationStatusLabel statusLabel, OwnershipMode mode)
        {
            var images = ResolvePinImages(statusLabel);
            var tint = TintForMode(mode);
            foreach (var image in images)
            {
                if (image != null)
                    image.color = tint;
            }
        }

        private static Image[] ResolvePinImages(LocationStatusLabel statusLabel)
        {
            if (statusLabel == null)
                return Array.Empty<Image>();
            if (StatusInnerImagesField != null)
            {
                var arr = StatusInnerImagesField.GetValue(statusLabel) as Image[];
                if (arr != null && arr.Length > 0)
                    return arr;
            }
            return statusLabel.GetComponents<Image>();
        }

        private static void ClearOwnershipVisuals(LocationDisplay display, bool restorePinColors)
        {
            if (display == null)
                return;
            var statusLabel = display.GetComponentInChildren<LocationStatusLabel>(true);
            if (restorePinColors)
                TintStatusImages(statusLabel, OwnershipMode.Manual);
        }
    }
}
