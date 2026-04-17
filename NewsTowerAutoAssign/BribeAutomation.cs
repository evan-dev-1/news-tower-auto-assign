using System;
using System.Reflection;
using GameState;
using Global_News_System.News_Items.Newsbook.Minigames.Bribe;
using GlobalNews;
using Reportables;
using Tower_Stats;
using UnityEngine;

namespace NewsTowerAutoAssign
{
    // Pays newsbook bribe nodes directly without opening the UI popup.
    //
    // Why bypass the popup: the bribe node lives in a story's newsbook page. The
    // MinigamePopup only opens when the player clicks the node while the newsbook is
    // open. Calling OnClicked() from a background patch is unsafe - if the event
    // timeline hasn't armed its recorder yet, Play == null and OnClicked() silently
    // sets IsChosen = true, permanently blocking future manual clicks.
    //
    // Instead we replicate BribeMinigame.Initialize()'s exact cost calculation:
    //   cost = DRNG.Range(bribeComponent, minMoney, maxMoney)
    // Then we set IsCompleted = true so the popup can never open, meaning DRNG
    // advances this slot exactly once - identical to the normal path.
    //
    // If we cannot afford the cost we leave the bribe untouched so the player can
    // handle it manually; we do NOT discard the story for an unpaid bribe.
    internal static class BribeAutomation
    {
        // Static event backing field on BribeMinigame - invoked when we pay directly
        // so finance tracking and quest requirements still fire.
        private static readonly FieldInfo _bribedEventField = typeof(BribeMinigame).GetField(
            "Bribed",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        internal static void TryPayBribes(NewsItem newsItem)
        {
            if (!AutoAssignPlugin.AutoResolveBribeMinigame.Value || newsItem == null)
                return;
            // Mid-save-load, TowerStats.Money may not yet reflect the restored
            // balance and the bribe component's IsCompleted flag may be about
            // to be reset by SetComponentData. Paying now can either charge
            // against a transient zero balance (free bribe) or double-charge
            // a bribe the save had already completed. Defer to the central
            // gate - see SafetyGate for the open/close event map.
            if (!SafetyGate.IsOpen)
                return;
            if (TowerStats.Instance == null)
                return;
            var difficulty = GameModeSettings.GetCurrentDifficultySettings();
            if (difficulty?.bribeSettings == null)
                return;

            try
            {
                int minMoney = difficulty.bribeSettings.minMoney;
                var newspaper = NewspaperManager.Instance?.CurrentNewspaper;

                foreach (
                    var bribe in newsItem.GetComponentsInChildren<NewsItemBribeComponent>(true)
                )
                {
                    if (bribe == null || bribe.IsCompleted || bribe.IsDestroyed || bribe.IsChosen)
                        continue;

                    var node = bribe.GetComponentInParent<NewsItemNode>(true);
                    if (node?.NodeState != NewsItemNodeState.Unlocked)
                        continue;

                    int maxMoney = Mathf.Max(bribe.GetMaxMoney(difficulty, newspaper), minMoney);
                    int cost = DRNG.Range(bribe, minMoney, maxMoney);

                    if ((float)cost > TowerStats.Instance.Money)
                    {
                        AssignmentLog.DecisionOnce(
                            newsItem,
                            "bribe_unaffordable",
                            AssignmentLog.StoryName(newsItem)
                                + " "
                                + AssignmentLog.StoryTagList(newsItem)
                                + " → WAIT (bribe): cost "
                                + cost
                                + " exceeds available money "
                                + (int)TowerStats.Instance.Money
                                + " (will retry next scan)."
                        );
                        continue;
                    }

                    TowerStats.Instance.AddMoney(-(float)cost, false);
                    var handler = _bribedEventField?.GetValue(null) as Action<float>;
                    handler?.Invoke(-(float)cost);
                    bribe.IsCompleted = true;
                    AssignmentLog.ClearSuppression(newsItem);
                    AssignmentLog.Decision(
                        AssignmentLog.StoryName(newsItem)
                            + " "
                            + AssignmentLog.StoryTagList(newsItem)
                            + " → BRIBE PAID: cost "
                            + cost
                            + "."
                    );
                }
            }
            catch (Exception e)
            {
                // Reflection / Unity component walks on an in-flight story can
                // surface transient nulls - log once and move on rather than
                // let the exception climb into the evaluator or Harmony patch.
                AssignmentLog.Error(
                    "BribeAutomation.TryPayBribes(" + AssignmentLog.StoryName(newsItem) + "): " + e
                );
            }
        }
    }
}
