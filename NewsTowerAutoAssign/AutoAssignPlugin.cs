using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace NewsTowerAutoAssign
{
    // Plugin bootstrap. Owns configuration, logger, and Harmony patch install.
    // All decision logic lives in AssignmentEvaluator; this class just wires it up.
    //
    // Every ConfigEntry below is currently DEVELOPER-ONLY: hidden from the
    // in-game ConfigurationManager UI and tagged IsAdvanced so even mods that
    // bypass Browsable still categorise them as "power user only". The .cfg
    // file on disk still works for my own testing. When we decide which knobs
    // real players should see, we'll drop the `Hidden` helper for just those.
    [BepInPlugin("idontcare.autoassign", "News Auto Assign", "1.0.0")]
    public class AutoAssignPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> AutoAssignEnabled;
        internal static ConfigEntry<bool> ChaseGoalsEnabled;
        internal static ConfigEntry<bool> AvoidRisksEnabled;
        internal static ConfigEntry<float> DiscardIfNoReporterForHours;
        internal static ConfigEntry<bool> AutoSkipRiskPopups;
        internal static ConfigEntry<bool> AutoSkipSuitcasePopups;
        internal static ConfigEntry<bool> AutoResolveBribeMinigame;
        internal static ConfigEntry<bool> DiscardFreshStoriesOnWeekend;
        internal static ConfigEntry<int> MinReportersToActivate;
        internal static ConfigEntry<bool> VerboseLogs;
        internal static ConfigEntry<bool> OnlyLogTests;

        private void Awake()
        {
            Log = BepInEx.Logging.Logger.CreateLogSource("AutoAssign");
            BindConfig();
            new Harmony("com.yourname.newstower.autoassign").PatchAll();
            VerifyReflection();
            AssignmentLog.Info(
                "SYSTEM",
                "Loaded and Harmony patches applied. Auto-assign is "
                    + (AutoAssignEnabled.Value ? "enabled." : "disabled.")
            );
        }

        // Builds a ConfigDescription that ConfigurationManager will hide from
        // its UI entirely (Browsable=false) and, as a belt-and-braces fallback
        // for custom UI forks, also marks as IsAdvanced. Every flag uses this
        // for now - no knob is ready to expose to regular players yet.
        private static ConfigDescription Hidden(string description) =>
            new ConfigDescription(
                description,
                null,
                new ConfigurationManagerAttributes { Browsable = false, IsAdvanced = true }
            );

        private void BindConfig()
        {
            AutoAssignEnabled = Config.Bind(
                "Dev",
                "Enabled",
                true,
                Hidden("Automatically assign reporters to news items when they appear.")
            );
            ChaseGoalsEnabled = Config.Bind(
                "Dev",
                "ChaseGoals",
                true,
                Hidden(
                    "Prefer story file paths whose skill matches a current weekly goal tag (does not skip stories)."
                )
            );
            AvoidRisksEnabled = Config.Bind(
                "Dev",
                "AvoidRisks",
                true,
                Hidden(
                    "Skip risky news items (Injury, Lawsuit, etc.) unless they also match a weekly goal."
                )
            );
            DiscardIfNoReporterForHours = Config.Bind(
                "Dev",
                "DiscardIfNoReporterForHours",
                4.0f,
                Hidden(
                    "Discard a news item if no reporter with the right skill will be free within this many in-game hours (0 = disabled). Fractional values honoured."
                )
            );
            AutoSkipRiskPopups = Config.Bind(
                "Dev",
                "AutoSkipRiskPopups",
                true,
                Hidden(
                    "Automatically dismiss risk spinner popups. Outcome is identical; the popup is cosmetic."
                )
            );
            AutoSkipSuitcasePopups = Config.Bind(
                "Dev",
                "AutoSkipSuitcasePopups",
                true,
                Hidden(
                    "Automatically dismiss the new-story suitcase popup. Locks the game until dismissed otherwise."
                )
            );
            AutoResolveBribeMinigame = Config.Bind(
                "Dev",
                "AutoResolveBribeMinigame",
                true,
                Hidden(
                    "Automatically pay bribe nodes when affordable. Cost matches manual play (same DRNG call). Left for manual handling if not affordable."
                )
            );
            DiscardFreshStoriesOnWeekend = Config.Bind(
                "Dev",
                "DiscardFreshStoriesOnWeekend",
                true,
                Hidden(
                    "Discard fresh (unstarted) stories that arrive on Saturday or Sunday - not enough time to finish. Invested stories never discarded here."
                )
            );
            MinReportersToActivate = Config.Bind(
                "Dev",
                "MinReportersToActivate",
                4,
                Hidden(
                    "Below this many globetrotter reporters the mod is fully passive (tutorial / early-game safety)."
                )
            );
            VerboseLogs = Config.Bind(
                "Dev",
                "VerboseLogs",
                false,
                Hidden("Enable low-level diagnostic logs. Intended for bug reports.")
            );
            OnlyLogTests = Config.Bind(
                "Dev",
                "OnlyLogTests",
                false,
                Hidden(
                    "Suppress every non-test log line. Only in-game test banners + PASS/FAIL/SKIP summaries print. Errors still fire."
                )
            );
        }

        private void VerifyReflection()
        {
            if (!AssignmentEvaluator.ProgressDoneEventFieldAvailable)
                Log.LogWarning(
                    "REFLECTION: NewsItemStoryFile.progressDoneEvent not found - ghost-assignment detection disabled"
                );
        }
    }
}
