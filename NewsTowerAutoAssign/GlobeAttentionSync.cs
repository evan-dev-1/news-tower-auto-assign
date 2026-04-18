using Reportables;

namespace NewsTowerAutoAssign
{
    // Globe pins use LocationStatusLabel: Unseen/HalfSeen still draw the "!" sprite;
    // FullSeen falls through to IsInDrawer (in-progress arrows). Opening the newsbook
    // normally sets FullSeen; we mirror that for mod-driven assignments.
    internal static class GlobeAttentionSync
    {
        internal static void PromoteFullySeen(NewsItem newsItem)
        {
            if (newsItem == null)
                return;
            if (newsItem.UnseenState == UnseenState.FullSeen)
                return;
            newsItem.UnseenState = UnseenState.FullSeen;
        }
    }
}
