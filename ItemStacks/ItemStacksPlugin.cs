using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ItemStacks
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [HarmonyPatch]
    public class ItemStacksPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.xlerii.itemstacks-xlerii";

        public const string NAME = "ItemStacks-xLerii";

        public const string VERSION = "1.0.0";

        public static ManualLogSource logger;

        public static ConfigFile config;

        private static ConfigEntry<bool> stackSizeEnabledConfig;
        private static ConfigEntry<float> stackSizeMultiplierConfig;
        private static ConfigEntry<bool> weightEnabledConfig;
        private static ConfigEntry<float> weightMultiplierConfig;
        private static ConfigEntry<bool> additionalItemsEnabledConfig;
        private static ConfigEntry<string> additionalItemsConfig;

        // Cached parsed list for fast lookup
        private static readonly HashSet<string> additionalItemsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, ItemTracker> itemTrackers = new Dictionary<string, ItemTracker>();

        void Awake()
        {
            logger = Logger;
            config = Config;

            stackSizeEnabledConfig = config.Bind(NAME + ".ItemStackSize", "enabled", true,
                new ConfigDescription(
                    "Should item stack size be modified?",
                    null,
                    new ConfigurationManagerAttributes { Order = 1 }
                )
            );

            weightEnabledConfig = config.Bind(NAME + ".ItemWeight", "enabled", true,
                new ConfigDescription(
                    "Should item weight be modified?",
                    null,
                    new ConfigurationManagerAttributes { Order = 1 }
                )
            );

            additionalItemsEnabledConfig = config.Bind(NAME + ".AdditionalItems", "enabled", true,
                new ConfigDescription(
                    "Enable applying stack/weight changes to items listed in the Additional Items textbox.",
                    null,
                    new ConfigurationManagerAttributes { Order = 10 }
                )
            );

            // shared names (comma-separated)
            additionalItemsConfig = config.Bind(NAME + ".AdditionalItems", "items", "",
                new ConfigDescription(
                    "Comma-separated list of item shared names, prefab names or plain names to include (examples: rk_pork, rk_porkrind, $item_rk_pork). " +
                    "The parser will normalize and add both plain and $item_ prefixed forms.",
                    null,
                    new ConfigurationManagerAttributes { Order = 11 }
                )
            );
            additionalItemsConfig.SettingChanged += (sender, args) => ParseAdditionalItems();
            additionalItemsEnabledConfig.SettingChanged += (sender, args) => ParseAdditionalItems();


            stackSizeMultiplierConfig = config.Bind(NAME + ".ItemMultipliers", "stack_size_multiplier", 10f,
                "Multiply the original item stack size by this value\n" +
                "Minimum resulting stack size is 1\n" +
                "Overwritten by individual item _stack_size values."
            );

            weightMultiplierConfig = config.Bind(NAME + ".ItemMultipliers", "weight_multiplier", .1f,
                "Multiply the original item weight by this value\n" +
                "Overwritten by individual item _weight values."
            );
            ParseAdditionalItems();

            Harmony harmony = new Harmony(GUID);
            harmony.PatchAll();

            logger.LogInfo(NAME + " loaded.");
        }
        private static void ParseAdditionalItems()
        {
            additionalItemsSet.Clear();

            if (!additionalItemsEnabledConfig.Value) return;

            var raw = additionalItemsConfig.Value;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var name = part.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                name = name.Trim('\"', '\'');

                if (name.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
                {
                    additionalItemsSet.Add(name);
                    var stripped = name.Substring(6); // remove "$item_"
                    if (!string.IsNullOrEmpty(stripped)) additionalItemsSet.Add(stripped);
                }
                else
                {
                    additionalItemsSet.Add(name);
                    additionalItemsSet.Add("$item_" + name);
                }
            }

            logger.LogDebug($"{NAME}: Parsed additional items: {string.Join(", ", additionalItemsSet)}");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        static void ModifyItemStackSizeAndWeight(ObjectDB __instance)
        {
            bool stackSizeEnabled = stackSizeEnabledConfig.Value;
            float stackSizeMultiplier = Mathf.Clamp(stackSizeMultiplierConfig.Value, 0, int.MaxValue);

            bool weightEnabled = weightEnabledConfig.Value;
            float weightMultiplier = Mathf.Clamp(weightMultiplierConfig.Value, 0, int.MaxValue);

            if (!(stackSizeEnabled || weightEnabled)) return;

            foreach (ItemDrop.ItemData.ItemType type in (ItemDrop.ItemData.ItemType[])Enum.GetValues(typeof(ItemDrop.ItemData.ItemType)))
            {
                foreach (ItemDrop item in __instance.GetAllItems(type, ""))
                {
                    var sharedName = item.m_itemData.m_shared.m_name;
                    var prefabName = item.gameObject != null ? item.gameObject.name : null;
                    var strippedShared = sharedName != null && sharedName.StartsWith("$item_", StringComparison.OrdinalIgnoreCase)
                        ? sharedName.Substring(6)
                        : sharedName;

                    // Apply to standard items ($item_...) OR to ones explicitly listed in additionalItems config
                    bool isDefaultItem = sharedName != null && sharedName.StartsWith("$item_", StringComparison.OrdinalIgnoreCase);

                    bool isAdditional = false;
                    if (additionalItemsEnabledConfig.Value)
                    {
                        if (sharedName != null && additionalItemsSet.Contains(sharedName)) isAdditional = true;
                        if (!isAdditional && strippedShared != null && additionalItemsSet.Contains(strippedShared)) isAdditional = true;
                        if (!isAdditional && prefabName != null && additionalItemsSet.Contains(prefabName)) isAdditional = true;
                        // also try without any "(Clone)" suffix just in case
                        if (!isAdditional && prefabName != null && prefabName.EndsWith("(Clone)"))
                        {
                            var clean = prefabName.Replace("(Clone)", "").Trim();
                            if (additionalItemsSet.Contains(clean)) isAdditional = true;
                        }
                    }

                    if (isDefaultItem || isAdditional)
                    {
                        ItemTracker tracker = GetItemTracker(item);

                        if (stackSizeEnabled && (tracker.OriginalStackSize > 1)) tracker.SetStackSize(stackSizeMultiplier, item);

                        if (weightEnabled) tracker.SetWeight(weightMultiplier, item);

                        if (isAdditional)
                        {
                            logger.LogDebug($"{NAME}: Applied changes to additional item: shared='{sharedName}', prefab='{prefabName}'");
                        }
                    }
                }
            }
        }

        private static ItemTracker GetItemTracker(ItemDrop item)
        {
            bool gotIt = itemTrackers.TryGetValue(item.m_itemData.m_shared.m_name, out ItemTracker tracker);
            if (gotIt)
            {
                return tracker;
            }
            tracker = new ItemTracker(item);
            itemTrackers.Add(item.m_itemData.m_shared.m_name, tracker);
            return tracker;
        }
    }
}
