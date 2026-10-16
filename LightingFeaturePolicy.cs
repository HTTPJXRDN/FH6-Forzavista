namespace ForzavistaFreeRoam;

// Signals are deferred for the hotfix. The saved amber preview is retained
// behind the explicit build switch for later development.
internal static class LightingFeaturePolicy
{
    internal static bool ExperimentalSignalsEnabled =>
#if EXPERIMENTAL_SIGNALS
        true;
#else
        false;
#endif

    internal const string SignalsDeferredMessage = "Hazards and turn signals are deferred until front/rear coverage is complete.";

    // Front-only DRL hazards remain research, outside the amber four-corner trial.
    internal static bool DrlHazardFallbackEnabled => false;

    internal static bool IsSignalAction(string action) =>
        action.Equals("signalleft", StringComparison.OrdinalIgnoreCase) ||
        action.Equals("signalright", StringComparison.OrdinalIgnoreCase) ||
        action.Equals("signalhazards", StringComparison.OrdinalIgnoreCase);
}
