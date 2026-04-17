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
    [BepInPlugin("newstower.autoassign", "News Tower Auto Assign", "1.0.1")]
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
        internal static ConfigEntry<bool> AutoAssignAds;

#if DEBUG
        // Developer-only logging knobs. In Release builds AssignmentLog's
        // non-error helpers are [Conditional("DEBUG")] no-ops, so these
        // toggles have nothing to gate and are omitted from the .cfg file.
        internal static ConfigEntry<bool> VerboseLogs;
        internal static ConfigEntry<bool> OnlyLogTests;
#endif

        private void Awake()
        {
            Log = BepInEx.Logging.Logger.CreateLogSource("AutoAssign");
            try
            {
                BindConfig();
                new Harmony("com.yourname.newstower.autoassign").PatchAll();
                VerifyReflection();
                AssignmentLog.Info(
                    "SYSTEM",
                    "Loaded and Harmony patches applied. Auto-assign is "
                        + (AutoAssignEnabled.Value ? "enabled." : "disabled.")
                );
            }
            catch (System.Exception e)
            {
                // A patch that fails to install is the single most dangerous
                // thing we can do to someone's save. Log loudly and let the
                // game continue unpatched rather than crash.
                AssignmentLog.Error("Awake failed - auto-assign will not run this session: " + e);
            }
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
                    "Automatically handle new-item suitcase rewards: pre-resolves unlocked suitcase nodes so the chain never stalls waiting for the player to view the story, and auto-skips the popup if it still manages to open. Unlock side-effect is identical to manual play (same DRNG draw)."
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
                3,
                Hidden(
                    "Below this many reporters the mod is fully passive (tutorial / early-game safety)."
                )
            );
            AutoAssignAds = Config.Bind(
                "Dev",
                "AutoAssignAds",
                true,
                Hidden(
                    "Automatically assign idle staff to ads on the Ads tab. Uses the same "
                        + "skill-matching logic as the news automation - whoever has the right "
                        + "skill and is free gets the work. Boycotted ads are skipped. The "
                        + "MinReportersToActivate gate does NOT apply to ads."
                )
            );
#if DEBUG
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
#endif
        }

        private void VerifyReflection()
        {
            // Real compat signal - fires when a game update renames or removes
            // the field we reflect against. Routed through AssignmentLog.Error
            // so it survives the Release-build log suppression and shows up
            // in players' BepInEx logs for bug reports.
            //
            // Every reflection target in the mod is probed at startup rather
            // than lazily at first use, so a game-update compat regression
            // surfaces once at plugin load rather than once per scan target
            // (which would be noisy) and independent of whether the relevant
            // automation ever fires in the player's session.
            if (!AssignmentEvaluator.ProgressDoneEventFieldAvailable)
                AssignmentLog.Error(
                    "REFLECTION: NewsItemStoryFile.progressDoneEvent not found - ghost-assignment detection disabled. This usually means News Tower got a game update that renamed or removed the field."
                );
            // AdAutomation re-uses the same private field by reflection; if
            // the news-side probe found it then this one will too, but we
            // probe independently so a future refactor that splits the field
            // surfaces here too.
            if (!AdAutomation.ProgressDoneEventFieldAvailable)
                AssignmentLog.Error(
                    "REFLECTION: NewsItemStoryFile.progressDoneEvent not found from AdAutomation - ad ghost-assignment detection disabled."
                );

            var (suitcaseType, missingSuitcaseMembers) =
                SuitcaseAutomation.ProbeReflectionTargets();
            if (missingSuitcaseMembers != null && missingSuitcaseMembers.Length > 0)
                AssignmentLog.Error(
                    "REFLECTION: SuitcaseAutomation cannot find ["
                        + string.Join(", ", missingSuitcaseMembers)
                        + "] on "
                        + suitcaseType
                        + " - suitcase auto-resolve will no-op this session. "
                        + "Likely a News Tower game update; file a bug report including the game version."
                );
        }
    }
}
