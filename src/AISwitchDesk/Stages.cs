namespace AISwitchDesk
{
    // Every value Handoff.Stage can hold. The text is what state.json stores,
    // so it must not change.
    internal static class Stages
    {
        // A writing task relayed through the sidebar.
        internal const string Ask = "ask";
        internal const string Writing = "writing";

        // A file captured from a GPT conversation.
        internal const string Capture = "capture";
        internal const string Linked = "linked";

        // Delivery to the other app.
        internal const string Ready = "ready";
        internal const string Confirm = "confirm";

        // Waiting for, then returning, the edited file.
        internal const string AwaitResult = "await-result";
        internal const string ReturnReady = "return-ready";
        internal const string ReturnConfirm = "return-confirm";

        // Finished.
        internal const string Done = "done";
        internal const string Cancel = "cancel";

        // Results of a conversation-pairing command. Also finished.
        internal const string Paired = "paired";
        internal const string Unpaired = "unpaired";
        internal const string PairMissing = "pair-missing";

        internal static bool Finished(string stage)
        {
            return stage == Done || stage == Cancel || stage == Paired || stage == Unpaired || stage == PairMissing;
        }
    }
}
