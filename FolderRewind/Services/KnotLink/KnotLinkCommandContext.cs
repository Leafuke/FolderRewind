namespace FolderRewind.Services.KnotLink
{
    public sealed class KnotLinkCommandContext
    {
        public KnotLinkCommandContext(KnotLinkCommandRequest request)
        {
            System.ArgumentNullException.ThrowIfNull(request);

            Request = request;
            Metadata = KnotLinkCommandMetadata.FromRequest(request);
        }

        public KnotLinkCommandRequest Request { get; }

        public KnotLinkCommandMetadata Metadata { get; }

        public bool IsParameterized => Request.IsParameterized;
    }
}
