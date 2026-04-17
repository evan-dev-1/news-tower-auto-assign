// Minimal ConfigurationManagerAttributes mirror. BepInEx.ConfigurationManager
// reads these fields via reflection (matched by name), so ANY class named
// "ConfigurationManagerAttributes" in ANY assembly works. We use this to hide
// debug-only flags from the in-game config UI - they still live in the .cfg
// file so power users can flip them, but regular players never see them.
//
// Keep this class `public` and field names exact - ConfigurationManager
// resolves them with case-sensitive reflection.
#pragma warning disable CS0649 // field is never assigned to (ConfigurationManager reads them)
namespace NewsTowerAutoAssign
{
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public bool? IsAdvanced;
        public string Category;
        public int? Order;
    }
}
#pragma warning restore CS0649
