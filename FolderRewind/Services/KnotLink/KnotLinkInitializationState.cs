using System.Threading;

namespace FolderRewind.Services.KnotLink
{
    /// <summary>
    /// Tracks whether the host-side KnotLink components were initialized.
    /// KnotLink SDK 2.0 does not define its transport's connection flag as a
    /// durable service-liveness contract, so transport shutdown must not
    /// implicitly rewrite this state.
    /// </summary>
    internal sealed class KnotLinkInitializationState
    {
        private int _senderInitialized;
        private int _responserInitialized;

        public bool SenderInitialized => Volatile.Read(ref _senderInitialized) != 0;

        public bool ResponserInitialized => Volatile.Read(ref _responserInitialized) != 0;

        public bool IsInitialized => SenderInitialized && ResponserInitialized;

        public void Record(bool senderInitialized, bool responserInitialized)
        {
            Volatile.Write(ref _senderInitialized, senderInitialized ? 1 : 0);
            Volatile.Write(ref _responserInitialized, responserInitialized ? 1 : 0);
        }

        public void Reset() => Record(false, false);
    }
}
