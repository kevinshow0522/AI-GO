using System;

namespace AISwitchDesk
{
    // Marks a failure that clears by itself once the person finishes what they
    // are doing in the client (an unsent draft, a sign-in, an open dialog). The
    // background relay waits and retries these; every other failure pauses the
    // handoff for a decision. The mark keeps the exception's own type, so
    // existing handlers behave as before.
    internal static class Retry
    {
        private const string Key = "AISwitchDesk.RetryLater";

        internal static T Later<T>(T error) where T : Exception
        {
            error.Data[Key] = true;
            return error;
        }

        internal static bool IsLater(Exception error)
        {
            return error != null && (error.Data.Contains(Key) || error.GetBaseException().Data.Contains(Key));
        }
    }
}
